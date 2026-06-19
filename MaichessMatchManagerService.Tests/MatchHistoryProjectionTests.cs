using Maichess.Events.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Kafka;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Unit tests for the durable write-through fold: materialising match.events.v1 into
// the full match-db document the projector persists alongside the Redis read model.
public sealed class MatchHistoryProjectionTests
{
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string AfterE4 = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";

    private static MatchEvent Created(
        Player? white = null,
        Player? black = null,
        Player? createdBy = null,
        MatchSource source = MatchSource.Native,
        string externalProvider = "",
        string externalRef = "")
    {
        MatchCreated created = new()
        {
            White = white ?? new Player { UserId = "w" },
            Black = black ?? new Player { BotId = "bot-1" },
            StartFen = StartFen,
            TimeFormat = new TimeFormat { Id = "5+0", BaseMs = 300_000, IncrementMs = 0, Category = "blitz" },
            Source = source,
            ExternalProvider = externalProvider,
            ExternalRef = externalRef,
        };
        if (createdBy is not null)
        {
            created.CreatedBy = createdBy;
        }

        return new MatchEvent { AggregateId = "m1", OccurredAt = 1_000, MatchCreated = created };
    }

    [Fact]
    public void MatchCreated_BuildsTheInitialDocument()
    {
        MatchDocument doc = MatchHistoryProjection.Apply(null, Created())!;

        Assert.Equal("m1", doc.Id);
        Assert.Equal("w", doc.White.UserId);
        Assert.Null(doc.White.ExternalName);
        Assert.Equal("bot-1", doc.Black.BotId);
        Assert.Equal(StartFen, doc.CurrentFen);
        Assert.Equal("ongoing", doc.Status);
        Assert.Equal("5+0", doc.TimeFormat.Id);
        Assert.Equal(300_000, doc.TimeFormat.BaseMs);
        Assert.Equal("blitz", doc.TimeFormat.Category);
        Assert.Equal(300_000, doc.WhiteTimeMs);
        Assert.Equal(300_000, doc.BlackTimeMs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_000), doc.LastMoveAt);
        Assert.Equal(new[] { StartFen }, doc.FenHistory);
        Assert.Empty(doc.ClockHistory);
        Assert.Equal("native", doc.Source);
        Assert.Null(doc.CreatedBy);
    }

    [Fact]
    public void MatchCreated_ExternalSource_MapsProviderAndRef()
    {
        MatchDocument doc = MatchHistoryProjection.Apply(
            null,
            Created(source: MatchSource.External, externalProvider: "lichess", externalRef: "game-7"))!;

        Assert.Equal("external", doc.Source);
        Assert.Equal("lichess", doc.ExternalProvider);
        Assert.Equal("game-7", doc.ExternalRef);
    }

    [Fact]
    public void MatchCreated_WithHumanInitiator_RecordsCreatedBy()
    {
        MatchDocument doc = MatchHistoryProjection.Apply(null, Created(createdBy: new Player { UserId = "w" }))!;

        Assert.Equal("w", doc.CreatedBy!.UserId);
    }

    [Fact]
    public void MatchCreated_WithIdentitylessInitiator_HasNoCreatedBy()
    {
        MatchDocument doc = MatchHistoryProjection.Apply(null, Created(createdBy: new Player()))!;

        Assert.Null(doc.CreatedBy);
    }

    [Fact]
    public void MatchCreated_ExternalPlayer_ProjectsExternalName()
    {
        MatchDocument doc = MatchHistoryProjection.Apply(
            null, Created(white: new Player { ExternalName = "Magnus" }))!;

        Assert.Equal("Magnus", doc.White.ExternalName);
        Assert.Null(doc.White.UserId);
        Assert.Null(doc.White.BotId);
    }

    [Fact]
    public void MoveValidated_SetsThePositionHistoryBlob()
    {
        MatchDocument doc = MatchHistoryProjection.Apply(null, Created())!;
        MatchEvent validated = new()
        {
            AggregateId = "m1",
            MoveValidated = new MoveValidated { ResultingFen = AfterE4, PositionHistory = { "p0", "p1" } },
        };

        MatchDocument updated = MatchHistoryProjection.Apply(doc, validated)!;

        Assert.Equal(new[] { "p0", "p1" }, updated.PositionHistory);
        Assert.Equal(StartFen, updated.CurrentFen); // fen still comes from MoveApplied
    }

    [Fact]
    public void MoveApplied_AppendsMoveAndFenAndAdvancesClocks()
    {
        MatchDocument doc = MatchHistoryProjection.Apply(null, Created())!;
        MatchEvent applied = new()
        {
            AggregateId = "m1",
            MoveApplied = new MoveApplied
            {
                MoveUci = "e2e4",
                ResultingFen = AfterE4,
                Index = 1,
                WhiteTimeMs = 299_000,
                BlackTimeMs = 300_000,
                AppliedAtMs = 5_000,
            },
        };

        MatchDocument updated = MatchHistoryProjection.Apply(doc, applied)!;

        Assert.Equal(AfterE4, updated.CurrentFen);
        Assert.Equal(new[] { "e2e4" }, updated.Moves);
        Assert.Equal(new[] { StartFen, AfterE4 }, updated.FenHistory);
        Assert.Equal(299_000, updated.WhiteTimeMs);
        Assert.Equal(300_000, updated.BlackTimeMs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(5_000), updated.LastMoveAt);
        Assert.Equal(new[] { new ClockSnapshot(299_000, 300_000) }, updated.ClockHistory);
    }

    [Fact]
    public void MoveApplied_AcrossMoves_AccumulatesClockHistoryParallelToMoves()
    {
        const string AfterE5 = "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq e6 0 2";
        MatchDocument doc = MatchHistoryProjection.Apply(null, Created())!;

        doc = MatchHistoryProjection.Apply(doc, Applied("e2e4", AfterE4, 1, 299_000, 300_000, 5_000))!;
        doc = MatchHistoryProjection.Apply(doc, Applied("e7e5", AfterE5, 2, 299_000, 298_500, 7_000))!;

        Assert.Equal(
            new[] { new ClockSnapshot(299_000, 300_000), new ClockSnapshot(299_000, 298_500) },
            doc.ClockHistory);
        Assert.Equal(doc.Moves.Count, doc.ClockHistory.Count);
    }

    private static MatchEvent Applied(
        string uci, string fen, int index, long whiteMs, long blackMs, long appliedAtMs) =>
        new()
        {
            AggregateId = "m1",
            MoveApplied = new MoveApplied
            {
                MoveUci = uci,
                ResultingFen = fen,
                Index = index,
                WhiteTimeMs = whiteMs,
                BlackTimeMs = blackMs,
                AppliedAtMs = appliedAtMs,
            },
        };

    [Fact]
    public void MatchEnded_SetsTerminalStatusAndClearsHistory()
    {
        MatchDocument doc = MatchHistoryProjection.Apply(null, Created())!;
        doc.PositionHistory = ["p0"];
        MatchEvent ended = new()
        {
            AggregateId = "m1",
            MatchEnded = new MatchEnded
            {
                Status = MatchStatus.WhiteWon,
                EndReason = EndReason.Checkmate,
                FinishedAtMs = 9_000,
            },
        };

        MatchDocument updated = MatchHistoryProjection.Apply(doc, ended)!;

        Assert.Equal("white_won", updated.Status);
        Assert.Equal(9_000, updated.FinishedAtMs);
        Assert.Empty(updated.PositionHistory);
    }

    [Fact]
    public void TransientPayload_LeavesTheDocumentUnchanged()
    {
        MatchDocument doc = MatchHistoryProjection.Apply(null, Created())!;
        MatchEvent botRequested = new()
        {
            AggregateId = "m1",
            BotMoveRequested = new BotMoveRequested { Fen = StartFen, BotId = "bot-1", RequestId = "r1" },
        };

        Assert.Same(doc, MatchHistoryProjection.Apply(doc, botRequested));
    }

    [Theory]
    [InlineData(MatchEvent.PayloadOneofCase.MoveValidated)]
    [InlineData(MatchEvent.PayloadOneofCase.MoveApplied)]
    [InlineData(MatchEvent.PayloadOneofCase.MatchEnded)]
    public void EventBeforeMatchCreated_ProducesNoDocument(MatchEvent.PayloadOneofCase which)
    {
        MatchEvent ev = which switch
        {
            MatchEvent.PayloadOneofCase.MoveValidated =>
                new MatchEvent { MoveValidated = new MoveValidated { ResultingFen = AfterE4 } },
            MatchEvent.PayloadOneofCase.MoveApplied =>
                new MatchEvent { MoveApplied = new MoveApplied { ResultingFen = AfterE4 } },
            _ => new MatchEvent { MatchEnded = new MatchEnded { Status = MatchStatus.Draw } },
        };

        Assert.Null(MatchHistoryProjection.Apply(null, ev));
    }
}
