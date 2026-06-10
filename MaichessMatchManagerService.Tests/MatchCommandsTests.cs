using Maichess.Events.V1;
using MaichessMatchManagerService.Kafka;
using MaichessMatchManagerService.Services;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Unit tests for the pure command-side decision logic that turns a player's intent
// plus the live read model into the match.events.v1 event to produce.
public sealed class MatchCommandsTests
{
    private const string WhiteToMoveFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string BlackToMoveFen = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";
    private const string White = "white-user";
    private const string Black = "black-user";

    private static int _counter;

    private static string NextId() => $"id-{Interlocked.Increment(ref _counter)}";

    private static LiveMatchState State(
        string status = "ongoing",
        string fen = WhiteToMoveFen,
        long sequence = 7,
        string? pendingDrawOfferer = null,
        PlayerRef? white = null,
        PlayerRef? black = null) =>
        new(
            MatchId: "match-1",
            CurrentFen: fen,
            Status: status,
            WhiteTimeMs: 180_000,
            BlackTimeMs: 180_000,
            MoveIndex: 0,
            LastMoveAtMs: 1_000,
            IncrementMs: 0,
            PositionHistory: ["pos-a", "pos-b"],
            White: white ?? new PlayerRef(White, null),
            Black: black ?? new PlayerRef(Black, null),
            Sequence: sequence,
            PendingDrawOffererUserId: pendingDrawOfferer);

    // ── SubmitMove ────────────────────────────────────────────────────────────

    [Fact]
    public void SubmitMove_OnTurn_BuildsMoveSubmittedWithEnvelope()
    {
        MatchEvent ev = MatchCommands.SubmitMove(State(sequence: 7), White, "e2e4", 5_000, NextId);

        Assert.Equal(MatchEvent.PayloadOneofCase.MoveSubmitted, ev.PayloadCase);
        Assert.Equal("match.MoveSubmitted", ev.EventType);
        Assert.Equal("match-1", ev.AggregateId);
        Assert.Equal(8, ev.Sequence);
        Assert.Equal(5_000, ev.OccurredAt);
        Assert.Equal("match-manager-service", ev.Producer);
        Assert.Empty(ev.CausationId);
        Assert.NotEmpty(ev.CorrelationId);
        Assert.NotEmpty(ev.EventId);

        Assert.Equal("e2e4", ev.MoveSubmitted.MoveUci);
        Assert.Equal(White, ev.MoveSubmitted.By.UserId);
        Assert.Equal(WhiteToMoveFen, ev.MoveSubmitted.Fen);
        Assert.Equal(new[] { "pos-a", "pos-b" }, ev.MoveSubmitted.PositionHistory);
    }

    [Fact]
    public void SubmitMove_BlackOnBlacksTurn_Builds()
    {
        MatchEvent ev = MatchCommands.SubmitMove(State(fen: BlackToMoveFen), Black, "e7e5", 5_000, NextId);

        Assert.Equal(Black, ev.MoveSubmitted.By.UserId);
    }

    [Fact]
    public void SubmitMove_NotParticipant_Throws() =>
        Assert.Throws<NotParticipantException>(
            () => MatchCommands.SubmitMove(State(), "stranger", "e2e4", 0, NextId));

    [Fact]
    public void SubmitMove_WrongTurn_Throws() =>
        Assert.Throws<NotYourTurnException>(
            () => MatchCommands.SubmitMove(State(fen: WhiteToMoveFen), Black, "e7e5", 0, NextId));

    [Fact]
    public void SubmitMove_AlreadyEnded_Throws() =>
        Assert.Throws<MatchAlreadyEndedException>(
            () => MatchCommands.SubmitMove(State(status: "white_won"), White, "e2e4", 0, NextId));

    [Fact]
    public void SubmitMove_FenWithoutColor_TreatedAsWhiteToMove()
    {
        MatchEvent ev = MatchCommands.SubmitMove(State(fen: "8"), White, "a1a2", 0, NextId);
        Assert.Equal(White, ev.MoveSubmitted.By.UserId);
    }

    // ── Resign ──────────────────────────────────────────────────────────────--

    [Fact]
    public void Resign_White_BlackWonByResignation()
    {
        MatchEvent ev = MatchCommands.Resign(State(), White, 9_000, NextId);

        Assert.Equal("match.MatchEnded", ev.EventType);
        Assert.Equal(8, ev.Sequence);
        Assert.Equal(MatchStatus.BlackWon, ev.MatchEnded.Status);
        Assert.Equal(EndReason.Resignation, ev.MatchEnded.EndReason);
        Assert.Equal(9_000, ev.MatchEnded.FinishedAtMs);
    }

    [Fact]
    public void Resign_Black_WhiteWon() =>
        Assert.Equal(MatchStatus.WhiteWon, MatchCommands.Resign(State(), Black, 0, NextId).MatchEnded.Status);

    [Fact]
    public void Resign_NotParticipant_Throws() =>
        Assert.Throws<NotParticipantException>(() => MatchCommands.Resign(State(), "stranger", 0, NextId));

    [Fact]
    public void Resign_AlreadyEnded_Throws() =>
        Assert.Throws<MatchAlreadyEndedException>(() => MatchCommands.Resign(State(status: "draw"), White, 0, NextId));

    // ── OfferDraw ─────────────────────────────────────────────────────────────

    [Fact]
    public void OfferDraw_Valid_BuildsDrawOffered()
    {
        MatchEvent ev = MatchCommands.OfferDraw(State(), White, 0, NextId);

        Assert.Equal("match.DrawOffered", ev.EventType);
        Assert.Equal(MatchEvent.PayloadOneofCase.DrawOffered, ev.PayloadCase);
        Assert.Equal(White, ev.DrawOffered.By.UserId);
    }

    [Fact]
    public void OfferDraw_ByBlack_AgainstHumanWhite_Builds()
    {
        MatchEvent ev = MatchCommands.OfferDraw(State(), Black, 0, NextId);

        Assert.Equal(MatchEvent.PayloadOneofCase.DrawOffered, ev.PayloadCase);
        Assert.Equal(Black, ev.DrawOffered.By.UserId);
    }

    [Fact]
    public void OfferDraw_OpponentIsBot_Throws()
    {
        LiveMatchState state = State(black: new PlayerRef(null, "stockfish-3"));
        Assert.Throws<NotParticipantException>(() => MatchCommands.OfferDraw(state, White, 0, NextId));
    }

    [Fact]
    public void OfferDraw_AlreadyPending_Throws() =>
        Assert.Throws<DrawOfferAlreadyPendingException>(
            () => MatchCommands.OfferDraw(State(pendingDrawOfferer: Black), White, 0, NextId));

    [Fact]
    public void OfferDraw_NotParticipant_Throws() =>
        Assert.Throws<NotParticipantException>(() => MatchCommands.OfferDraw(State(), "stranger", 0, NextId));

    [Fact]
    public void OfferDraw_AlreadyEnded_Throws() =>
        Assert.Throws<MatchAlreadyEndedException>(
            () => MatchCommands.OfferDraw(State(status: "white_won"), White, 0, NextId));

    // ── AcceptDraw ────────────────────────────────────────────────────────────

    [Fact]
    public void AcceptDraw_Valid_BuildsDrawnMatchEnded()
    {
        MatchEvent ev = MatchCommands.AcceptDraw(State(pendingDrawOfferer: White), Black, 3_000, NextId);

        Assert.Equal(MatchStatus.Draw, ev.MatchEnded.Status);
        Assert.Equal(EndReason.DrawAgreement, ev.MatchEnded.EndReason);
        Assert.Equal(3_000, ev.MatchEnded.FinishedAtMs);
    }

    [Fact]
    public void AcceptDraw_NoPending_Throws() =>
        Assert.Throws<NoDrawOfferPendingException>(() => MatchCommands.AcceptDraw(State(), Black, 0, NextId));

    [Fact]
    public void AcceptDraw_SelfAccept_Throws() =>
        Assert.Throws<NotDrawRecipientException>(
            () => MatchCommands.AcceptDraw(State(pendingDrawOfferer: White), White, 0, NextId));

    [Fact]
    public void AcceptDraw_NotParticipant_Throws() =>
        Assert.Throws<NotParticipantException>(
            () => MatchCommands.AcceptDraw(State(pendingDrawOfferer: White), "stranger", 0, NextId));

    [Fact]
    public void AcceptDraw_AlreadyEnded_Throws() =>
        Assert.Throws<MatchAlreadyEndedException>(
            () => MatchCommands.AcceptDraw(State(status: "draw", pendingDrawOfferer: White), Black, 0, NextId));

    // ── DeclineDraw ───────────────────────────────────────────────────────────

    [Fact]
    public void DeclineDraw_Valid_BuildsDrawDeclined()
    {
        MatchEvent ev = MatchCommands.DeclineDraw(State(pendingDrawOfferer: White), Black, 0, NextId);

        Assert.Equal(MatchEvent.PayloadOneofCase.DrawDeclined, ev.PayloadCase);
        Assert.Equal(Black, ev.DrawDeclined.By.UserId);
    }

    [Fact]
    public void DeclineDraw_NoPending_Throws() =>
        Assert.Throws<NoDrawOfferPendingException>(() => MatchCommands.DeclineDraw(State(), Black, 0, NextId));

    [Fact]
    public void DeclineDraw_NotParticipant_Throws() =>
        Assert.Throws<NotParticipantException>(() => MatchCommands.DeclineDraw(State(), "stranger", 0, NextId));

    [Fact]
    public void DeclineDraw_AlreadyEnded_Throws() =>
        Assert.Throws<MatchAlreadyEndedException>(
            () => MatchCommands.DeclineDraw(State(status: "draw", pendingDrawOfferer: White), Black, 0, NextId));

    // ── Timeout ───────────────────────────────────────────────────────────────

    [Fact]
    public void Timeout_WhiteFlagged_BlackWonByTimeout()
    {
        MatchEvent ev = MatchCommands.Timeout(State(), whiteFlagged: true, 4_000, NextId);

        Assert.Equal(MatchStatus.BlackWon, ev.MatchEnded.Status);
        Assert.Equal(EndReason.Timeout, ev.MatchEnded.EndReason);
        Assert.Equal(8, ev.Sequence);
    }

    [Fact]
    public void Timeout_BlackFlagged_WhiteWon() =>
        Assert.Equal(
            MatchStatus.WhiteWon,
            MatchCommands.Timeout(State(), whiteFlagged: false, 0, NextId).MatchEnded.Status);
}
