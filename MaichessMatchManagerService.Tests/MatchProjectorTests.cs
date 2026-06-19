using System.Text.Json;
using Maichess.Events.V1;
using MaichessMatchManagerService.Kafka;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Unit tests for the pure projector decision logic: consuming one match.events.v1
// record yields the events to produce back (MoveApplied / MatchEnded /
// BotMoveRequested / MoveSubmitted), the socket pushes, and the advanced read-model
// state. Covers the clock math (decrement / increment / flag), terminal detection,
// the bot-to-move handoff, the BotMoveCalculated -> MoveSubmitted re-entry, envelope
// stamping, and the sequence-based dedupe.
public sealed class MatchProjectorTests
{
    private const string WhiteToMove = "8/8/8/8/8/8/8/8 w - - 0 1";
    private const string BlackToMove = "8/8/8/8/8/8/8/8 b - - 0 1";

    private static Func<string> Ids()
    {
        int n = 0;
        return () => $"id{++n}";
    }

    private static LiveMatchState State(
        string fen = WhiteToMove,
        long whiteMs = 180_000,
        long blackMs = 180_000,
        long lastMoveAtMs = 1_000,
        long incrementMs = 0,
        int moveIndex = 0,
        long sequence = 5,
        PlayerRef? white = null,
        PlayerRef? black = null,
        string? pendingMoveUci = "e2e4",
        IReadOnlyList<string>? positionHistory = null) =>
        new(
            MatchId: "m1",
            CurrentFen: fen,
            Status: "ongoing",
            WhiteTimeMs: whiteMs,
            BlackTimeMs: blackMs,
            MoveIndex: moveIndex,
            LastMoveAtMs: lastMoveAtMs,
            IncrementMs: incrementMs,
            PositionHistory: positionHistory ?? [],
            White: white ?? new PlayerRef("w", null),
            Black: black ?? new PlayerRef("b", null),
            Sequence: sequence,
            PendingMoveUci: pendingMoveUci);

    private static MatchEvent Validated(
        long sequence = 6,
        string resultingFen = BlackToMove,
        GameResult result = GameResult.Unspecified,
        IEnumerable<string>? positionHistory = null)
    {
        MatchEvent ev = new()
        {
            EventId = "ev6",
            AggregateId = "m1",
            CorrelationId = "corr",
            Sequence = sequence,
            MoveValidated = new MoveValidated { ResultingFen = resultingFen, GameResult = result },
        };
        ev.MoveValidated.PositionHistory.AddRange(positionHistory ?? ["h1"]);
        return ev;
    }

    private static JsonElement Payload(OutboundEvent push) =>
        JsonDocument.Parse(push.Push.PayloadJson).RootElement;

    [Fact]
    public void MoveValidated_OngoingHumanMove_EmitsMoveAppliedAndMoveMade()
    {
        ProjectorOutcome outcome = MatchProjector.Decide(State(), Validated(), nowMs: 3_000, Ids());

        MatchEvent applied = Assert.Single(outcome.Events);
        Assert.Equal("id1", applied.EventId);
        Assert.Equal("match.MoveApplied", applied.EventType);
        Assert.Equal("m1", applied.AggregateId);
        Assert.Equal(7, applied.Sequence);
        Assert.Equal(3_000, applied.OccurredAt);
        Assert.Equal("corr", applied.CorrelationId);
        Assert.Equal("ev6", applied.CausationId);
        Assert.Equal("match-manager-service", applied.Producer);

        MoveApplied move = applied.MoveApplied;
        Assert.Equal("e2e4", move.MoveUci);
        Assert.Equal(BlackToMove, move.ResultingFen);
        Assert.Equal(1, move.Index);
        Assert.Equal("w", move.Player.UserId);
        Assert.Equal(178_000, move.WhiteTimeMs);
        Assert.Equal(180_000, move.BlackTimeMs);
        Assert.Equal(3_000, move.AppliedAtMs);

        OutboundEvent push = Assert.Single(outcome.Pushes);
        Assert.Equal("id2", push.EventId);
        Assert.Equal("socket.move_made", push.EventType);
        Assert.Equal("m1", push.Push.TargetMatchId);
        Assert.Equal("move_made", push.Push.EventName);
        JsonElement body = Payload(push);
        Assert.Equal("m1", body.GetProperty("match_id").GetString());
        Assert.Equal("e2e4", body.GetProperty("move").GetString());
        Assert.Equal(BlackToMove, body.GetProperty("resulting_fen").GetString());
        Assert.Equal(1, body.GetProperty("index").GetInt32());
        Assert.Equal(178_000, body.GetProperty("white_time_ms").GetInt64());
        Assert.Equal(180_000, body.GetProperty("black_time_ms").GetInt64());
        Assert.Equal("w", body.GetProperty("player").GetProperty("user_id").GetString());

        LiveMatchState next = outcome.State!;
        Assert.Equal(BlackToMove, next.CurrentFen);
        Assert.Equal(178_000, next.WhiteTimeMs);
        Assert.Equal(1, next.MoveIndex);
        Assert.Equal(3_000, next.LastMoveAtMs);
        Assert.Null(next.PendingMoveUci);
        Assert.Equal(new[] { "h1" }, next.PositionHistory);
        Assert.Equal(7, next.Sequence);
    }

    [Fact]
    public void MoveValidated_WhiteMove_CreditsWhiteIncrementWhenOngoing()
    {
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(incrementMs: 2_000), Validated(), nowMs: 3_000, Ids());

        MoveApplied move = outcome.Events[0].MoveApplied;
        Assert.Equal(180_000, move.WhiteTimeMs); // 180000 - 2000 elapsed + 2000 increment
        Assert.Equal(180_000, move.BlackTimeMs);
    }

    [Fact]
    public void MoveValidated_BlackMove_DecrementsAndIncrementsBlack()
    {
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(fen: BlackToMove, incrementMs: 2_000), Validated(resultingFen: WhiteToMove),
            nowMs: 3_000, Ids());

        MoveApplied move = outcome.Events[0].MoveApplied;
        Assert.Equal("b", move.Player.UserId);
        Assert.Equal(180_000, move.WhiteTimeMs);
        Assert.Equal(180_000, move.BlackTimeMs); // 180000 - 2000 + 2000
    }

    [Theory]
    [InlineData(GameResult.WhiteWon, MatchStatus.WhiteWon, "checkmate")]
    [InlineData(GameResult.BlackWon, MatchStatus.BlackWon, "checkmate")]
    [InlineData(GameResult.Stalemate, MatchStatus.Draw, "stalemate")]
    [InlineData(GameResult.FiftyMoveRule, MatchStatus.Draw, "fifty_move_rule")]
    [InlineData(GameResult.ThreefoldRepetition, MatchStatus.Draw, "threefold_repetition")]
    [InlineData(GameResult.InsufficientMaterial, MatchStatus.Draw, "insufficient_material")]
    public void MoveValidated_TerminalResult_EmitsMatchEnded(
        GameResult result, MatchStatus expectedStatus, string expectedReason)
    {
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(), Validated(result: result), nowMs: 3_000, Ids());

        Assert.Equal(2, outcome.Events.Count);
        MatchEvent ended = outcome.Events[1];
        Assert.Equal("id3", ended.EventId);
        Assert.Equal("match.MatchEnded", ended.EventType);
        Assert.Equal(8, ended.Sequence);
        Assert.Equal(expectedStatus, ended.MatchEnded.Status);
        Assert.Equal(3_000, ended.MatchEnded.FinishedAtMs);

        OutboundEvent endedPush = Assert.Single(outcome.Pushes, p => p.Push.EventName == "match_ended");
        Assert.Equal("id4", endedPush.EventId);
        JsonElement body = Payload(endedPush);
        Assert.Equal("m1", body.GetProperty("match_id").GetString());
        Assert.Equal(MatchProjection.StatusToString(expectedStatus), body.GetProperty("status").GetString());
        Assert.Equal(expectedReason, body.GetProperty("reason").GetString());

        // The read model carries the terminal status and clears history.
        Assert.Equal(MatchProjection.StatusToString(expectedStatus), outcome.State!.Status);
        Assert.Empty(outcome.State.PositionHistory);
        Assert.Equal(8, outcome.State.Sequence);
    }

    [Fact]
    public void MoveValidated_FlaggedClock_EndsAsTimeoutRegardlessOfBoard()
    {
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(whiteMs: 1_000, lastMoveAtMs: 0), Validated(), nowMs: 5_000, Ids());

        MoveApplied move = outcome.Events[0].MoveApplied;
        Assert.Equal(0, move.WhiteTimeMs);

        MatchEvent ended = outcome.Events[1];
        Assert.Equal(MatchStatus.BlackWon, ended.MatchEnded.Status);
        Assert.Equal(EndReason.Timeout, ended.MatchEnded.EndReason);
        OutboundEvent endedPush = outcome.Pushes.Single(p => p.Push.EventName == "match_ended");
        Assert.Equal("timeout", Payload(endedPush).GetProperty("reason").GetString());
        Assert.Equal("black_won", Payload(endedPush).GetProperty("status").GetString());
    }

    [Fact]
    public void MoveValidated_BlackFlagged_WhiteWins()
    {
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(fen: BlackToMove, blackMs: 1_000, lastMoveAtMs: 0),
            Validated(resultingFen: WhiteToMove), nowMs: 5_000, Ids());

        Assert.Equal(MatchStatus.WhiteWon, outcome.Events[1].MatchEnded.Status);
        // Black's clock hit exactly zero, so the reason is the flagged-clock timeout
        // (pins down the black <= 0 boundary in ResolveEndReason).
        Assert.Equal(EndReason.Timeout, outcome.Events[1].MatchEnded.EndReason);
    }

    [Fact]
    public void MoveValidated_OngoingBotToMove_EmitsBotMoveRequested()
    {
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(black: new PlayerRef(null, "bot-1")),
            Validated(resultingFen: BlackToMove), nowMs: 3_000, Ids());

        Assert.Equal(2, outcome.Events.Count);
        MatchEvent requested = outcome.Events[1];
        Assert.Equal("id3", requested.EventId);
        Assert.Equal("match.BotMoveRequested", requested.EventType);
        Assert.Equal(8, requested.Sequence);
        Assert.Equal(BlackToMove, requested.BotMoveRequested.Fen);
        Assert.Equal("bot-1", requested.BotMoveRequested.BotId);
        Assert.Equal(180_000, requested.BotMoveRequested.TimeLimitMs);
        Assert.Equal("id4", requested.BotMoveRequested.RequestId);
        Assert.Single(outcome.Pushes); // only move_made; bot requests are not socket pushes
    }

    [Fact]
    public void MoveValidated_BotMover_StampsBotPlayerAndEmptyHistoryWhenExternal()
    {
        ProjectorOutcome botOutcome = MatchProjector.Decide(
            State(white: new PlayerRef(null, "bot-1")), Validated(), nowMs: 3_000, Ids());
        MoveApplied botMove = botOutcome.Events[0].MoveApplied;
        Assert.Equal("bot-1", botMove.Player.BotId);
        Assert.Equal("bot-1", Payload(botOutcome.Pushes[0]).GetProperty("player").GetProperty("bot_id").GetString());

        ProjectorOutcome extOutcome = MatchProjector.Decide(
            State(white: new PlayerRef(null, null)), Validated(), nowMs: 3_000, Ids());
        MoveApplied extMove = extOutcome.Events[0].MoveApplied;
        Assert.Equal(Player.IdentityOneofCase.None, extMove.Player.IdentityCase);
        Assert.False(Payload(extOutcome.Pushes[0]).GetProperty("player").EnumerateObject().Any());
    }

    [Fact]
    public void MoveValidated_MalformedFen_TreatsActiveColorAsWhite()
    {
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(fen: "startpos"), Validated(resultingFen: "startpos"), nowMs: 3_000, Ids());

        Assert.Equal(178_000, outcome.Events[0].MoveApplied.WhiteTimeMs);
    }

    [Fact]
    public void MoveValidated_GameEndingMove_DoesNotCreditIncrement()
    {
        // A move that ends the game earns no increment, even with a non-zero increment
        // configured: the mover's clock shows only the elapsed-time decrement. Pins down
        // the ongoing && increment > 0 guard against a logical-OR mutation.
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(incrementMs: 2_000), Validated(result: GameResult.WhiteWon), nowMs: 3_000, Ids());

        Assert.Equal(178_000, outcome.Events[0].MoveApplied.WhiteTimeMs);
    }

    [Fact]
    public void MoveValidated_TwoFieldFen_ReadsActiveColorFromSecondField()
    {
        // "<board> b" is the parts.Length >= 2 boundary: black is the mover, so the
        // applied move is attributed to black's side.
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(fen: "8 b"), Validated(resultingFen: WhiteToMove), nowMs: 3_000, Ids());

        Assert.Equal("b", outcome.Events[0].MoveApplied.Player.UserId);
    }

    [Fact]
    public void MoveValidated_NoPendingMove_AppliesEmptyMoveUci()
    {
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(pendingMoveUci: null), Validated(), nowMs: 3_000, Ids());

        Assert.Equal(string.Empty, outcome.Events[0].MoveApplied.MoveUci);
    }

    [Fact]
    public void MoveValidated_WhiteBotToMove_RequestsWithWhiteClock()
    {
        ProjectorOutcome outcome = MatchProjector.Decide(
            State(fen: BlackToMove, white: new PlayerRef(null, "bot-1")),
            Validated(resultingFen: WhiteToMove), nowMs: 3_000, Ids());

        MatchEvent requested = outcome.Events[1];
        Assert.Equal("match.BotMoveRequested", requested.EventType);
        Assert.Equal("bot-1", requested.BotMoveRequested.BotId);
        Assert.Equal(180_000, requested.BotMoveRequested.TimeLimitMs); // white clock, untouched
    }

    [Fact]
    public void MoveValidated_NullState_ProjectsNothing()
    {
        ProjectorOutcome outcome = MatchProjector.Decide(null, Validated(), nowMs: 3_000, Ids());

        Assert.Null(outcome.State);
        Assert.Empty(outcome.Events);
        Assert.Empty(outcome.Pushes);
    }

    [Fact]
    public void MatchCreated_BotToMove_RequestsTheFirstBotMove()
    {
        MatchEvent created = new()
        {
            EventId = "ev1",
            AggregateId = "m1",
            CorrelationId = "corr",
            Sequence = 1,
            OccurredAt = 1_000,
            MatchCreated = new MatchCreated
            {
                White = new Player { BotId = "bot-1" },
                Black = new Player { UserId = "b" },
                StartFen = WhiteToMove,
                TimeFormat = new TimeFormat { BaseMs = 60_000, IncrementMs = 0 },
            },
        };

        ProjectorOutcome outcome = MatchProjector.Decide(null, created, nowMs: 2_000, Ids());

        MatchEvent requested = Assert.Single(outcome.Events);
        Assert.Equal("id1", requested.EventId);
        Assert.Equal("match.BotMoveRequested", requested.EventType);
        Assert.Equal(2, requested.Sequence);
        Assert.Equal(WhiteToMove, requested.BotMoveRequested.Fen);
        Assert.Equal("bot-1", requested.BotMoveRequested.BotId);
        Assert.Equal(60_000, requested.BotMoveRequested.TimeLimitMs);
        Assert.Equal("id2", requested.BotMoveRequested.RequestId);
        Assert.Empty(outcome.Pushes);
        Assert.Equal("bot-1", outcome.State!.White.BotId);
        Assert.Equal(1, outcome.State.Sequence);
    }

    [Fact]
    public void MatchCreated_BlackBotToMove_RequestsWithBlackClock()
    {
        MatchEvent created = new()
        {
            AggregateId = "m1",
            Sequence = 1,
            OccurredAt = 1_000,
            MatchCreated = new MatchCreated
            {
                White = new Player { UserId = "w" },
                Black = new Player { BotId = "bot-1" },
                StartFen = BlackToMove,
                TimeFormat = new TimeFormat { BaseMs = 60_000, IncrementMs = 0 },
            },
        };

        ProjectorOutcome outcome = MatchProjector.Decide(null, created, nowMs: 2_000, Ids());

        MatchEvent requested = Assert.Single(outcome.Events);
        Assert.Equal("bot-1", requested.BotMoveRequested.BotId);
        Assert.Equal(BlackToMove, requested.BotMoveRequested.Fen);
        Assert.Equal(60_000, requested.BotMoveRequested.TimeLimitMs);
    }

    [Fact]
    public void MatchCreated_HumanToMove_EmitsNothing()
    {
        MatchEvent created = new()
        {
            AggregateId = "m1",
            Sequence = 1,
            OccurredAt = 1_000,
            MatchCreated = new MatchCreated
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                StartFen = WhiteToMove,
                TimeFormat = new TimeFormat { BaseMs = 60_000 },
            },
        };

        ProjectorOutcome outcome = MatchProjector.Decide(null, created, nowMs: 2_000, Ids());

        Assert.Empty(outcome.Events);
        Assert.Empty(outcome.Pushes);
        Assert.NotNull(outcome.State);
    }

    [Fact]
    public void BotMoveCalculated_BotToMove_EmitsMoveSubmitted()
    {
        MatchEvent calculated = new()
        {
            EventId = "evb",
            AggregateId = "m1",
            CorrelationId = "corr",
            Sequence = 8,
            BotMoveCalculated = new BotMoveCalculated { MoveUci = "g1f3", RequestId = "r1" },
        };
        LiveMatchState state = State(
            sequence: 7, white: new PlayerRef(null, "bot-1"), positionHistory: ["h0"], pendingMoveUci: null);

        ProjectorOutcome outcome = MatchProjector.Decide(state, calculated, nowMs: 4_000, Ids());

        MatchEvent submitted = Assert.Single(outcome.Events);
        Assert.Equal("id1", submitted.EventId);
        Assert.Equal("match.MoveSubmitted", submitted.EventType);
        Assert.Equal(9, submitted.Sequence);
        Assert.Equal("evb", submitted.CausationId);
        Assert.Equal("g1f3", submitted.MoveSubmitted.MoveUci);
        Assert.Equal("bot-1", submitted.MoveSubmitted.By.BotId);
        Assert.Equal(WhiteToMove, submitted.MoveSubmitted.Fen);
        Assert.Equal(new[] { "h0" }, submitted.MoveSubmitted.PositionHistory);
        Assert.Empty(outcome.Pushes);
        Assert.Equal("g1f3", outcome.State!.PendingMoveUci);
        Assert.Equal(9, outcome.State.Sequence);
    }

    [Fact]
    public void BotMoveCalculated_BlackBotToMove_SubmitsForBlack()
    {
        MatchEvent calculated = new()
        {
            AggregateId = "m1",
            Sequence = 8,
            BotMoveCalculated = new BotMoveCalculated { MoveUci = "e7e5", RequestId = "r1" },
        };
        LiveMatchState state = State(
            fen: BlackToMove, sequence: 7, black: new PlayerRef(null, "bot-1"), pendingMoveUci: null);

        ProjectorOutcome outcome = MatchProjector.Decide(state, calculated, nowMs: 4_000, Ids());

        MatchEvent submitted = Assert.Single(outcome.Events);
        Assert.Equal("e7e5", submitted.MoveSubmitted.MoveUci);
        Assert.Equal("bot-1", submitted.MoveSubmitted.By.BotId);
    }

    [Fact]
    public void BotMoveCalculated_NotBotToMove_IsDroppedDefensively()
    {
        MatchEvent calculated = new()
        {
            AggregateId = "m1",
            Sequence = 8,
            BotMoveCalculated = new BotMoveCalculated { MoveUci = "g1f3", RequestId = "r1" },
        };
        LiveMatchState state = State(sequence: 7); // white is a human

        ProjectorOutcome outcome = MatchProjector.Decide(state, calculated, nowMs: 4_000, Ids());

        Assert.Same(state, outcome.State);
        Assert.Empty(outcome.Events);
    }

    [Fact]
    public void BotMoveCalculated_NullState_ProjectsNothing()
    {
        MatchEvent calculated = new()
        {
            AggregateId = "m1",
            Sequence = 8,
            BotMoveCalculated = new BotMoveCalculated { MoveUci = "g1f3", RequestId = "r1" },
        };

        ProjectorOutcome outcome = MatchProjector.Decide(null, calculated, nowMs: 4_000, Ids());

        Assert.Null(outcome.State);
        Assert.Empty(outcome.Events);
    }

    [Fact]
    public void MoveSubmitted_FoldsPendingMoveWithoutEmitting()
    {
        MatchEvent submitted = new()
        {
            AggregateId = "m1",
            Sequence = 6,
            MoveSubmitted = new MoveSubmitted { MoveUci = "d2d4", By = new Player { UserId = "w" } },
        };

        ProjectorOutcome outcome = MatchProjector.Decide(State(pendingMoveUci: null), submitted, nowMs: 3_000, Ids());

        Assert.Empty(outcome.Events);
        Assert.Empty(outcome.Pushes);
        Assert.Equal("d2d4", outcome.State!.PendingMoveUci);
        Assert.Equal(6, outcome.State.Sequence);
    }

    [Fact]
    public void AlreadyAppliedSequence_IsDedupedToANoOp()
    {
        LiveMatchState state = State(sequence: 6);

        ProjectorOutcome outcome = MatchProjector.Decide(state, Validated(sequence: 6), nowMs: 3_000, Ids());

        Assert.Same(state, outcome.State);
        Assert.Empty(outcome.Events);
        Assert.Empty(outcome.Pushes);
    }
}
