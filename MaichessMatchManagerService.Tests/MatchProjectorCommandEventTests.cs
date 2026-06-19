using System.Text.Json;
using Maichess.Events.V1;
using MaichessMatchManagerService.Kafka;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Projector handling of the command-originated facts the write side emits in Kafka
// task 06: MatchEnded (resign / accept-draw / timeout), DrawOffered, DrawDeclined.
// Each is applied to the live read model and fans out the matching socket push; the
// projector's own self-emitted MatchEnded is deduped (sequence <= state.Sequence) so
// the push fires exactly once. The move/clock cases live in MatchProjectorTests.
public sealed class MatchProjectorCommandEventTests
{
    private const string Fen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private static string NewId() => Guid.NewGuid().ToString();

    private static LiveMatchState State(long sequence = 3, string? pendingDrawOfferer = null) =>
        new(
            MatchId: "m1",
            CurrentFen: Fen,
            Status: "ongoing",
            WhiteTimeMs: 180_000,
            BlackTimeMs: 180_000,
            MoveIndex: 4,
            LastMoveAtMs: 1_000,
            IncrementMs: 0,
            PositionHistory: ["p"],
            White: new PlayerRef("white", null),
            Black: new PlayerRef("black", null),
            Sequence: sequence,
            PendingDrawOffererUserId: pendingDrawOfferer);

    private static MatchEvent Event(long sequence, string eventType) =>
        new()
        {
            EventId = NewId(),
            EventType = eventType,
            AggregateId = "m1",
            Sequence = sequence,
            OccurredAt = 5_000,
            CorrelationId = "corr",
            CausationId = string.Empty,
            Producer = "match-manager-service",
        };

    private static string Field(OutboundEvent push, string key) =>
        JsonDocument.Parse(push.Push.PayloadJson).RootElement.GetProperty(key).GetString()!;

    [Fact]
    public void MatchEnded_Resign_AppliesStatusAndPushesMatchEnded()
    {
        MatchEvent ended = Event(4, "match.MatchEnded");
        ended.MatchEnded = new MatchEnded
        {
            Status = MatchStatus.BlackWon,
            EndReason = EndReason.Resignation,
            FinishedAtMs = 5_000,
        };

        ProjectorOutcome outcome = MatchProjector.Decide(State(), ended, 5_000, NewId);

        Assert.Equal("black_won", outcome.State!.Status);
        Assert.Empty(outcome.Events);
        OutboundEvent push = Assert.Single(outcome.Pushes);
        Assert.Equal("match_ended", push.Push.EventName);
        Assert.Equal("black_won", Field(push, "status"));
        Assert.Equal("resignation", Field(push, "reason"));
    }

    [Fact]
    public void MatchEnded_AcceptDraw_PushesDrawAgreementReason()
    {
        MatchEvent ended = Event(4, "match.MatchEnded");
        ended.MatchEnded = new MatchEnded
        {
            Status = MatchStatus.Draw,
            EndReason = EndReason.DrawAgreement,
            FinishedAtMs = 5_000,
        };

        ProjectorOutcome outcome = MatchProjector.Decide(State(), ended, 5_000, NewId);

        Assert.Equal("draw", outcome.State!.Status);
        OutboundEvent push = Assert.Single(outcome.Pushes);
        Assert.Equal("draw_agreement", Field(push, "reason"));
    }

    [Fact]
    public void MatchEnded_AlreadyAppliedSequence_IsDedupedWithNoPush()
    {
        MatchEvent ended = Event(3, "match.MatchEnded");
        ended.MatchEnded = new MatchEnded { Status = MatchStatus.WhiteWon, EndReason = EndReason.Checkmate };

        ProjectorOutcome outcome = MatchProjector.Decide(State(sequence: 3), ended, 5_000, NewId);

        Assert.Empty(outcome.Pushes);
        Assert.Empty(outcome.Events);
        Assert.Equal("ongoing", outcome.State!.Status);
    }

    [Fact]
    public void DrawOffered_RecordsPendingOffererAndPushesDrawOffered()
    {
        MatchEvent offered = Event(4, "match.DrawOffered");
        offered.DrawOffered = new DrawOffered { By = new Player { UserId = "white" } };

        ProjectorOutcome outcome = MatchProjector.Decide(State(), offered, 5_000, NewId);

        Assert.Equal("white", outcome.State!.PendingDrawOffererUserId);
        Assert.Empty(outcome.Events);
        OutboundEvent push = Assert.Single(outcome.Pushes);
        Assert.Equal("draw_offered", push.Push.EventName);
        Assert.Equal("m1", Field(push, "match_id"));
        JsonElement payload = JsonDocument.Parse(push.Push.PayloadJson).RootElement;
        Assert.Equal("white", payload.GetProperty("player").GetProperty("user_id").GetString());
    }

    [Fact]
    public void DrawDeclined_ClearsPendingOffererAndPushesDrawDeclined()
    {
        MatchEvent declined = Event(4, "match.DrawDeclined");
        declined.DrawDeclined = new DrawDeclined { By = new Player { UserId = "black" } };

        ProjectorOutcome outcome = MatchProjector.Decide(State(pendingDrawOfferer: "white"), declined, 5_000, NewId);

        Assert.Null(outcome.State!.PendingDrawOffererUserId);
        OutboundEvent push = Assert.Single(outcome.Pushes);
        Assert.Equal("draw_declined", push.Push.EventName);
    }

    // ── MatchProjection folds (read-model only) ──────────────────────────────

    [Fact]
    public void Projection_DrawOffered_WithNonUserBy_RecordsNoPendingOfferer()
    {
        MatchEvent offered = Event(4, "match.DrawOffered");
        offered.DrawOffered = new DrawOffered { By = new Player { BotId = "bot-a" } };

        LiveMatchState next = MatchProjection.Apply(State(), offered)!;

        Assert.Null(next.PendingDrawOffererUserId);
        Assert.Equal(4, next.Sequence);
    }

    [Fact]
    public void Projection_DrawEventsBeforeMatchCreated_AreIgnored()
    {
        MatchEvent offered = Event(1, "match.DrawOffered");
        offered.DrawOffered = new DrawOffered { By = new Player { UserId = "white" } };
        MatchEvent declined = Event(1, "match.DrawDeclined");
        declined.DrawDeclined = new DrawDeclined { By = new Player { UserId = "white" } };

        Assert.Null(MatchProjection.Apply(null, offered));
        Assert.Null(MatchProjection.Apply(null, declined));
    }
}
