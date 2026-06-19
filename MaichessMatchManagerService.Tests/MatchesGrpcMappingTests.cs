using Grpc.Core;
using Maichess.MatchManager.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Tests.Support;
using NSubstitute;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// The gRPC handler is a thin proto↔domain translator (per CLAUDE.md): these tests pin
// down the exact wire mapping it performs — status/source/end-reason enum strings,
// page echo, created_by identity, and the error-detail messages — which the
// behaviour-focused service tests do not observe.
public sealed class MatchesGrpcMappingTests
{
    private static TimeFormat Blitz() => new()
    {
        Id = "5+0",
        BaseMs = 300_000,
        IncrementMs = 0,
        Category = "blitz",
    };

    private static MatchDocument ExternalMatch(string id = "ext-1")
    {
        MatchDocument match = MatchServiceContext.BuildMatch(
            id,
            new PlayerDocument { ExternalName = "Alice" },
            new PlayerDocument { ExternalName = "Bob" });
        match.Source = "external";
        return match;
    }

    // ── SyncExternalMatch: status + end-reason wire mapping ──────────────────

    [Theory]
    [InlineData(MatchStatus.WhiteWon, EndReason.Checkmate, "white_won", "checkmate")]
    [InlineData(MatchStatus.BlackWon, EndReason.Resignation, "black_won", "resignation")]
    [InlineData(MatchStatus.Draw, EndReason.Stalemate, "draw", "stalemate")]
    [InlineData(MatchStatus.Draw, EndReason.DrawAgreement, "draw", "draw_agreement")]
    [InlineData(MatchStatus.Draw, EndReason.FiftyMoveRule, "draw", "fifty_move_rule")]
    [InlineData(MatchStatus.Draw, EndReason.ThreefoldRepetition, "draw", "threefold_repetition")]
    [InlineData(MatchStatus.Draw, EndReason.InsufficientMaterial, "draw", "insufficient_material")]
    [InlineData(MatchStatus.WhiteWon, EndReason.Timeout, "white_won", "timeout")]
    public async Task SyncExternalMatch_EndedStatus_MapsEnumsToWireStringsAndBroadcasts(
        MatchStatus status, EndReason reason, string statusString, string reasonString)
    {
        GrpcServiceContext ctx = new();
        ctx.SetupMatch(ExternalMatch());

        SyncExternalMatchResponse response = await ctx.Service.SyncExternalMatch(
            new SyncExternalMatchRequest
            {
                MatchId = "ext-1",
                CurrentFen = "fen",
                Status = status,
                WhiteTimeMs = 1_000,
                BlackTimeMs = 1_000,
                FinishedAtMs = 5_000,
                EndReason = reason,
            },
            ctx.CallContext);

        Assert.Equal(status, response.Match.Status);
        ctx.ServiceContext.SocketBroadcaster.Received(1).BroadcastMatchEnded(
            Arg.Any<MatchDocument>(), statusString, reasonString);
    }

    [Theory]
    [InlineData(MatchStatus.Ongoing)]
    [InlineData(MatchStatus.Unspecified)]
    public async Task SyncExternalMatch_OngoingStatus_MapsToOngoingAndDoesNotBroadcastEnd(MatchStatus status)
    {
        GrpcServiceContext ctx = new();
        ctx.SetupMatch(ExternalMatch());

        SyncExternalMatchResponse response = await ctx.Service.SyncExternalMatch(
            new SyncExternalMatchRequest
            {
                MatchId = "ext-1",
                CurrentFen = "fen",
                Status = status,
                WhiteTimeMs = 1_000,
                BlackTimeMs = 1_000,
            },
            ctx.CallContext);

        Assert.Equal(MatchStatus.Ongoing, response.Match.Status);
        ctx.ServiceContext.SocketBroadcaster.DidNotReceive().BroadcastMatchEnded(
            Arg.Any<MatchDocument>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SyncExternalMatch_UnknownEndReason_DefaultsToCheckmate()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupMatch(ExternalMatch());

        await ctx.Service.SyncExternalMatch(
            new SyncExternalMatchRequest
            {
                MatchId = "ext-1",
                CurrentFen = "fen",
                Status = MatchStatus.WhiteWon,
                FinishedAtMs = 5_000,
                EndReason = EndReason.Unspecified,
            },
            ctx.CallContext);

        ctx.ServiceContext.SocketBroadcaster.Received(1).BroadcastMatchEnded(
            Arg.Any<MatchDocument>(), "white_won", "checkmate");
    }

    // ── ListMatches: status arg + page echo ──────────────────────────────────

    [Theory]
    [InlineData(MatchStatusFilter.Ongoing)]
    [InlineData(MatchStatusFilter.Unspecified)]
    [InlineData(MatchStatusFilter.Ended)]
    public async Task ListMatches_AlwaysQueriesOngoing(MatchStatusFilter filter)
    {
        GrpcServiceContext ctx = new();
        ctx.SetupListMatches([], total: 0);

        await ctx.Service.ListMatches(
            new ListMatchesRequest { Status = filter, Page = 1, PageSize = 20 },
            ctx.CallContext);

        // The public ListMatches RPC only ever surfaces ongoing matches.
        await ctx.ServiceContext.Repository.Received(1).ListAsync(
            "ongoing", Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListMatches_EchoesRequestedPage()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupListMatches([], total: 0);

        ListMatchesResponse response = await ctx.Service.ListMatches(
            new ListMatchesRequest { Status = MatchStatusFilter.Ongoing, Page = 3, PageSize = 20 },
            ctx.CallContext);

        Assert.Equal(3, response.Page);
    }

    [Fact]
    public async Task ListUserMatches_EchoesRequestedPage()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupFindForUser([]);

        ListUserMatchesResponse response = await ctx.Service.ListUserMatches(
            new ListUserMatchesRequest { UserId = "user-1", Page = 4, PageSize = 20 },
            ctx.CallContext);

        Assert.Equal(4, response.Page);
    }

    [Fact]
    public async Task SearchMatches_EchoesRequestedPage()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupSearch([]);

        SearchMatchesResponse response = await ctx.Service.SearchMatches(
            new SearchMatchesRequest { Page = 5, PageSize = 20 },
            ctx.CallContext);

        Assert.Equal(5, response.Page);
    }

    // ── ListUserMatches: ongoing/ended status filter ─────────────────────────

    [Fact]
    public async Task ListUserMatches_OngoingFilter_ReturnsOnlyOngoing()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupFindForUser(
        [
            MatchServiceContext.BuildMatch(
                "ongoing-1", new PlayerDocument { UserId = "user-1" }, new PlayerDocument { UserId = "opp" }),
            MatchServiceContext.BuildMatch(
                "ended-1", new PlayerDocument { UserId = "user-1" }, new PlayerDocument { UserId = "opp" },
                status: "white_won", finishedAtMs: 5_000),
        ]);

        ListUserMatchesResponse response = await ctx.Service.ListUserMatches(
            new ListUserMatchesRequest { UserId = "user-1", Status = MatchStatusFilter.Ongoing },
            ctx.CallContext);

        Assert.Equal(new[] { "ongoing-1" }, response.Matches.Select(m => m.Id));
    }

    [Fact]
    public async Task ListUserMatches_EndedFilter_ReturnsOnlyEnded()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupFindForUser(
        [
            MatchServiceContext.BuildMatch(
                "ongoing-1", new PlayerDocument { UserId = "user-1" }, new PlayerDocument { UserId = "opp" }),
            MatchServiceContext.BuildMatch(
                "ended-1", new PlayerDocument { UserId = "user-1" }, new PlayerDocument { UserId = "opp" },
                status: "white_won", finishedAtMs: 5_000),
        ]);

        ListUserMatchesResponse response = await ctx.Service.ListUserMatches(
            new ListUserMatchesRequest { UserId = "user-1", Status = MatchStatusFilter.Ended },
            ctx.CallContext);

        Assert.Equal(new[] { "ended-1" }, response.Matches.Select(m => m.Id));
    }

    // ── CreateMatch: created_by external identity + unknown-format category ───

    [Fact]
    public async Task CreateMatch_ExternalCreatedBy_MapsExternalNameIdentity()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { ExternalName = "Alice" },
                Black = new Player { ExternalName = "Bob" },
                TimeFormat = Blitz(),
                Source = MatchSource.External,
                ExternalProvider = "ts",
                CreatedBy = new Player { ExternalName = "Carol" },
            },
            ctx.CallContext);

        Assert.Equal("Carol", response.Match.CreatedBy.ExternalName);
    }

    [Fact]
    public async Task CreateMatch_UnknownFormatWithNonDefaultCategory_PreservesIt()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeFormat = new TimeFormat { Id = "custom", BaseMs = 240_000, Category = "rapid" },
            },
            ctx.CallContext);

        // A caller-supplied, non-empty category survives — it is not overwritten with
        // the registry default ("blitz").
        Assert.Equal("rapid", response.Match.TimeFormat.Category);
    }

    // ── Error-detail messages ────────────────────────────────────────────────

    [Fact]
    public async Task CreateMatch_IdentitylessPlayer_ReportsIdentityRequired()
    {
        GrpcServiceContext ctx = new();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.CreateMatch(
                new CreateMatchRequest { White = new Player(), Black = new Player { UserId = "b" }, TimeFormat = Blitz() },
                ctx.CallContext));

        Assert.Equal("player identity is required", ex.Status.Detail);
    }

    [Fact]
    public async Task CreateMatch_MissingTimeFormat_ReportsTimeFormatRequired()
    {
        GrpcServiceContext ctx = new();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.CreateMatch(
                new CreateMatchRequest { White = new Player { UserId = "w" }, Black = new Player { UserId = "b" } },
                ctx.CallContext));

        Assert.Equal("time_format is required", ex.Status.Detail);
    }
}
