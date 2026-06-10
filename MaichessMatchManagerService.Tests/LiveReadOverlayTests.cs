using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Kafka;
using MaichessMatchManagerService.Tests.Support;
using NSubstitute;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Tests for the REST live-read overlay: an ongoing match serves the projector's live
// read-model fields (fen, clocks, last-move time, status) on top of the durable
// document; a cold model or a finished match falls back to the durable document.
public sealed class LiveReadOverlayTests
{
    private static LiveMatchState Live(string status = "ongoing") =>
        new(
            MatchId: "m1",
            CurrentFen: "fen-live",
            Status: status,
            WhiteTimeMs: 12_345,
            BlackTimeMs: 67_890,
            MoveIndex: 3,
            LastMoveAtMs: 9_000,
            IncrementMs: 0,
            PositionHistory: ["h"],
            White: new PlayerRef("w", null),
            Black: new PlayerRef("b", null),
            Sequence: 7);

    [Fact]
    public async Task OngoingMatch_WithLiveProjection_OverlaysVolatileFields()
    {
        MatchServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("m1", "w", "b");
        ctx.SetupMatch(match);
        ctx.SetupLiveState(Live(status: "white_won"));

        MatchDocument result = await ctx.MatchService.GetMatchForReadAsync("m1", CancellationToken.None);

        Assert.Equal("fen-live", result.CurrentFen);
        Assert.Equal(12_345, result.WhiteTimeMs);
        Assert.Equal(67_890, result.BlackTimeMs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(9_000), result.LastMoveAt);
        Assert.Equal("white_won", result.Status);
    }

    [Fact]
    public async Task OngoingMatch_WithoutLiveProjection_ReturnsDurableDocument()
    {
        MatchServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("m1", "w", "b");
        ctx.SetupMatch(match);

        MatchDocument result = await ctx.MatchService.GetMatchForReadAsync("m1", CancellationToken.None);

        Assert.Same(match, result);
    }

    [Fact]
    public async Task FinishedMatch_IsNeverOverlaid()
    {
        MatchServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("m1", "w", "b", status: "draw");
        ctx.SetupMatch(match);

        MatchDocument result = await ctx.MatchService.GetMatchForReadAsync("m1", CancellationToken.None);

        Assert.Same(match, result);
        await ctx.LiveState.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
