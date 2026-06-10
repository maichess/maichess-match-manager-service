using Maichess.Events.V1;
using MaichessMatchManagerService.Kafka;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Unit tests for the pure match.events.v1 -> LiveMatchState fold. Each durable fact
// updates only the fields it owns; transient pipeline payloads and events that
// arrive before MatchCreated leave the state unchanged; a full replay reconstructs
// the read model.
public sealed class MatchProjectionTests
{
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string AfterE4Fen = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";

    private static MatchEvent Created(
        long sequence = 1,
        long occurredAt = 1_000,
        Player? white = null,
        Player? black = null,
        long baseMs = 180_000,
        long incrementMs = 2_000) =>
        new()
        {
            AggregateId = "m1",
            Sequence = sequence,
            OccurredAt = occurredAt,
            MatchCreated = new MatchCreated
            {
                White = white ?? new Player { UserId = "w" },
                Black = black ?? new Player { BotId = "bot-1" },
                StartFen = StartFen,
                TimeFormat = new TimeFormat { BaseMs = baseMs, IncrementMs = incrementMs },
            },
        };

    [Fact]
    public void MatchCreated_InitialisesStateFromTheCreatedFact()
    {
        LiveMatchState? state = MatchProjection.Apply(null, Created());

        Assert.NotNull(state);
        Assert.Equal("m1", state!.MatchId);
        Assert.Equal(StartFen, state.CurrentFen);
        Assert.Equal("ongoing", state.Status);
        Assert.Equal(180_000, state.WhiteTimeMs);
        Assert.Equal(180_000, state.BlackTimeMs);
        Assert.Equal(0, state.MoveIndex);
        Assert.Equal(1_000, state.LastMoveAtMs);
        Assert.Equal(2_000, state.IncrementMs);
        Assert.Empty(state.PositionHistory);
        Assert.Equal("w", state.White.UserId);
        Assert.Null(state.White.BotId);
        Assert.Equal("bot-1", state.Black.BotId);
        Assert.Null(state.Black.UserId);
        Assert.Equal(1, state.Sequence);
    }

    [Fact]
    public void ExternalPlayer_ProjectsToNeitherUserNorBot()
    {
        LiveMatchState state = MatchProjection.Apply(
            null, Created(white: new Player { ExternalName = "ext" }))!;

        Assert.Null(state.White.UserId);
        Assert.Null(state.White.BotId);
    }

    [Fact]
    public void MoveValidated_CarriesThePositionHistoryBlob()
    {
        LiveMatchState created = MatchProjection.Apply(null, Created())!;

        MatchEvent validated = new()
        {
            AggregateId = "m1",
            Sequence = 2,
            MoveValidated = new MoveValidated
            {
                ResultingFen = AfterE4Fen,
                GameResult = GameResult.Unspecified,
                PositionHistory = { "h0", "h1" },
            },
        };

        LiveMatchState state = MatchProjection.Apply(created, validated)!;

        Assert.Equal(new[] { "h0", "h1" }, state.PositionHistory);
        Assert.Equal(2, state.Sequence);
        // MoveValidated owns only the history blob; fen/clocks come from MoveApplied.
        Assert.Equal(StartFen, state.CurrentFen);
    }

    [Fact]
    public void MoveApplied_AdvancesFenClocksIndexAndTime()
    {
        LiveMatchState created = MatchProjection.Apply(null, Created())!;

        MatchEvent applied = new()
        {
            AggregateId = "m1",
            Sequence = 3,
            MoveApplied = new MoveApplied
            {
                MoveUci = "e2e4",
                ResultingFen = AfterE4Fen,
                Index = 1,
                WhiteTimeMs = 179_000,
                BlackTimeMs = 180_000,
                AppliedAtMs = 5_000,
            },
        };

        LiveMatchState state = MatchProjection.Apply(created, applied)!;

        Assert.Equal(AfterE4Fen, state.CurrentFen);
        Assert.Equal(179_000, state.WhiteTimeMs);
        Assert.Equal(180_000, state.BlackTimeMs);
        Assert.Equal(1, state.MoveIndex);
        Assert.Equal(5_000, state.LastMoveAtMs);
        Assert.Equal(3, state.Sequence);
    }

    [Fact]
    public void MatchEnded_SetsTerminalStatusAndClearsHistory()
    {
        LiveMatchState created = MatchProjection.Apply(null, Created())!;
        LiveMatchState withHistory = created with { PositionHistory = ["h0"] };

        MatchEvent ended = new()
        {
            AggregateId = "m1",
            Sequence = 9,
            MatchEnded = new MatchEnded { Status = MatchStatus.WhiteWon, EndReason = EndReason.Checkmate },
        };

        LiveMatchState state = MatchProjection.Apply(withHistory, ended)!;

        Assert.Equal("white_won", state.Status);
        Assert.Empty(state.PositionHistory);
        Assert.Equal(9, state.Sequence);
    }

    [Fact]
    public void MoveSubmitted_StashesThePendingMove()
    {
        LiveMatchState created = MatchProjection.Apply(null, Created())!;

        MatchEvent submitted = new()
        {
            AggregateId = "m1",
            Sequence = 2,
            MoveSubmitted = new MoveSubmitted { MoveUci = "e2e4", By = new Player { UserId = "w" } },
        };

        LiveMatchState state = MatchProjection.Apply(created, submitted)!;

        Assert.Equal("e2e4", state.PendingMoveUci);
        Assert.Equal(2, state.Sequence);
    }

    [Fact]
    public void MoveRejected_ClearsThePendingMove()
    {
        LiveMatchState pending = MatchProjection.Apply(null, Created())! with { PendingMoveUci = "e2e4" };

        MatchEvent rejected = new()
        {
            AggregateId = "m1",
            Sequence = 3,
            MoveRejected = new MoveRejected { MoveUci = "e2e4", Reason = "illegal" },
        };

        LiveMatchState state = MatchProjection.Apply(pending, rejected)!;

        Assert.Null(state.PendingMoveUci);
        Assert.Equal(3, state.Sequence);
    }

    [Fact]
    public void MoveApplied_ClearsThePendingMove()
    {
        LiveMatchState pending = MatchProjection.Apply(null, Created())! with { PendingMoveUci = "e2e4" };

        MatchEvent applied = new()
        {
            AggregateId = "m1",
            Sequence = 3,
            MoveApplied = new MoveApplied { ResultingFen = AfterE4Fen, Index = 1, AppliedAtMs = 5_000 },
        };

        Assert.Null(MatchProjection.Apply(pending, applied)!.PendingMoveUci);
    }

    [Theory]
    [InlineData(MatchEvent.PayloadOneofCase.MoveSubmitted)]
    [InlineData(MatchEvent.PayloadOneofCase.MoveRejected)]
    public void PendingMoveEventsBeforeMatchCreated_ProjectNothing(MatchEvent.PayloadOneofCase which)
    {
        MatchEvent ev = which == MatchEvent.PayloadOneofCase.MoveSubmitted
            ? new MatchEvent { MoveSubmitted = new MoveSubmitted { MoveUci = "e2e4" } }
            : new MatchEvent { MoveRejected = new MoveRejected { MoveUci = "e2e4" } };

        Assert.Null(MatchProjection.Apply(null, ev));
    }

    [Fact]
    public void TransientPayload_LeavesStateUnchanged()
    {
        LiveMatchState created = MatchProjection.Apply(null, Created())!;

        MatchEvent botRequested = new()
        {
            AggregateId = "m1",
            Sequence = 4,
            BotMoveRequested = new BotMoveRequested { Fen = StartFen, BotId = "bot-1", RequestId = "r1" },
        };

        Assert.Same(created, MatchProjection.Apply(created, botRequested));
    }

    [Theory]
    [InlineData(MatchEvent.PayloadOneofCase.MoveValidated)]
    [InlineData(MatchEvent.PayloadOneofCase.MoveApplied)]
    [InlineData(MatchEvent.PayloadOneofCase.MatchEnded)]
    public void EventBeforeMatchCreated_ProjectsNothing(MatchEvent.PayloadOneofCase which)
    {
        MatchEvent ev = which switch
        {
            MatchEvent.PayloadOneofCase.MoveValidated =>
                new MatchEvent { MoveValidated = new MoveValidated { ResultingFen = "f" } },
            MatchEvent.PayloadOneofCase.MoveApplied =>
                new MatchEvent { MoveApplied = new MoveApplied { ResultingFen = "f" } },
            _ => new MatchEvent { MatchEnded = new MatchEnded { Status = MatchStatus.Draw } },
        };

        Assert.Null(MatchProjection.Apply(null, ev));
    }

    [Fact]
    public void Rebuild_ReplaysTheLogIntoTheCurrentState()
    {
        MatchEvent[] log =
        [
            Created(sequence: 1),
            new MatchEvent
            {
                AggregateId = "m1",
                Sequence = 2,
                MoveValidated = new MoveValidated { ResultingFen = AfterE4Fen, PositionHistory = { "h0" } },
            },
            new MatchEvent
            {
                AggregateId = "m1",
                Sequence = 3,
                MoveApplied = new MoveApplied
                {
                    ResultingFen = AfterE4Fen,
                    Index = 1,
                    WhiteTimeMs = 179_000,
                    BlackTimeMs = 180_000,
                    AppliedAtMs = 5_000,
                },
            },
        ];

        LiveMatchState state = MatchProjection.Rebuild(log)!;

        Assert.Equal(AfterE4Fen, state.CurrentFen);
        Assert.Equal(1, state.MoveIndex);
        Assert.Equal(179_000, state.WhiteTimeMs);
        Assert.Equal(new[] { "h0" }, state.PositionHistory);
        Assert.Equal(3, state.Sequence);
        Assert.Equal("ongoing", state.Status);
    }

    [Fact]
    public void Rebuild_EmptyLog_ProjectsNothing()
    {
        Assert.Null(MatchProjection.Rebuild([]));
    }

    [Theory]
    [InlineData(MatchStatus.Ongoing, "ongoing")]
    [InlineData(MatchStatus.WhiteWon, "white_won")]
    [InlineData(MatchStatus.BlackWon, "black_won")]
    [InlineData(MatchStatus.Draw, "draw")]
    [InlineData(MatchStatus.Unspecified, "ongoing")]
    public void StatusToString_MapsEveryMatchStatus(MatchStatus status, string expected)
    {
        Assert.Equal(expected, MatchProjection.StatusToString(status));
    }
}
