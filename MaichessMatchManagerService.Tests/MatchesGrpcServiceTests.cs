using Grpc.Core;
using Maichess.MatchManager.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Tests.Support;
using NSubstitute;
using Xunit;

namespace MaichessMatchManagerService.Tests;

public sealed class MatchesGrpcServiceTests
{
    private const string InitialFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string BlackToMoveFen = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";

    private static TimeFormat BlitzFormat() => new()
    {
        Id = "5+0",
        BaseMs = 300_000,
        IncrementMs = 0,
        Category = "blitz",
    };

    private static TimeFormat BulletFormat() => new()
    {
        Id = "1+0",
        BaseMs = 60_000,
        IncrementMs = 0,
        Category = "bullet",
    };

    private static TimeFormat RapidFormat() => new()
    {
        Id = "10+0",
        BaseMs = 600_000,
        IncrementMs = 0,
        Category = "rapid",
    };

    private static TimeFormat ClassicalFormat() => new()
    {
        Id = "30+0",
        BaseMs = 1_800_000,
        IncrementMs = 0,
        Category = "classical",
    };

    // ── CreateMatch ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateMatch_WithUserIdPlayers_ReturnsMatchWithUserIds()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "white-1" },
                Black = new Player { UserId = "black-1" },
                TimeFormat = BlitzFormat(),
            },
            ctx.CallContext);

        Assert.Equal("white-1", response.Match.White.UserId);
        Assert.Equal("black-1", response.Match.Black.UserId);
        Assert.Equal(MatchStatus.Ongoing, response.Match.Status);
    }

    [Fact]
    public async Task CreateMatch_WithBotPlayer_ReturnsMatchWithBotId()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { BotId = "bot-1" },
                Black = new Player { UserId = "black-1" },
                TimeFormat = BlitzFormat(),
            },
            ctx.CallContext);

        Assert.Equal("bot-1", response.Match.White.BotId);
    }

    [Fact]
    public async Task CreateMatch_WithInvalidPlayer_ThrowsRpcExceptionInvalidArgument()
    {
        GrpcServiceContext ctx = new();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.CreateMatch(
                new CreateMatchRequest
                {
                    White = new Player(),
                    Black = new Player { UserId = "black-1" },
                    TimeFormat = BlitzFormat(),
                },
                ctx.CallContext));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task CreateMatch_WithoutTimeFormat_ThrowsRpcExceptionInvalidArgument()
    {
        GrpcServiceContext ctx = new();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.CreateMatch(
                new CreateMatchRequest
                {
                    White = new Player { UserId = "w" },
                    Black = new Player { UserId = "b" },
                },
                ctx.CallContext));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task CreateMatch_BulletTimeFormat_SetsCorrectBaseMs()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeFormat = BulletFormat(),
            },
            ctx.CallContext);

        Assert.Equal(60_000L, response.Match.WhiteTimeMs);
        Assert.Equal("1+0", response.Match.TimeFormat.Id);
        Assert.Equal("bullet", response.Match.TimeFormat.Category);
    }

    [Fact]
    public async Task CreateMatch_RapidTimeFormat_SetsCorrectBaseMs()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeFormat = RapidFormat(),
            },
            ctx.CallContext);

        Assert.Equal(600_000L, response.Match.WhiteTimeMs);
        Assert.Equal("10+0", response.Match.TimeFormat.Id);
    }

    [Fact]
    public async Task CreateMatch_ClassicalTimeFormat_SetsCorrectBaseMs()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeFormat = ClassicalFormat(),
            },
            ctx.CallContext);

        Assert.Equal(1_800_000L, response.Match.WhiteTimeMs);
        Assert.Equal("classical", response.Match.TimeFormat.Category);
    }

    [Fact]
    public async Task CreateMatch_TimeFormatWithIncrement_PersistsIncrement()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeFormat = new TimeFormat
                {
                    Id = "3+2",
                    BaseMs = 180_000,
                    IncrementMs = 2_000,
                    Category = "blitz",
                },
            },
            ctx.CallContext);

        Assert.Equal(180_000L, response.Match.WhiteTimeMs);
        Assert.Equal(2_000L, response.Match.TimeFormat.IncrementMs);
        Assert.Equal("3+2", response.Match.TimeFormat.Id);
    }

    [Fact]
    public async Task CreateMatch_UnknownTimeFormatId_UsesCallerValues()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeFormat = new TimeFormat
                {
                    Id = "custom-format",
                    BaseMs = 240_000,
                    IncrementMs = 4_000,
                    Category = "blitz",
                },
            },
            ctx.CallContext);

        Assert.Equal(240_000L, response.Match.WhiteTimeMs);
        Assert.Equal("custom-format", response.Match.TimeFormat.Id);
        Assert.Equal(4_000L, response.Match.TimeFormat.IncrementMs);
        Assert.Equal("blitz", response.Match.TimeFormat.Category);
    }

    [Fact]
    public async Task CreateMatch_UnknownTimeFormatId_WithoutBaseFallsBackToDefault()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeFormat = new TimeFormat { Id = "custom-format" },
            },
            ctx.CallContext);

        Assert.Equal(300_000L, response.Match.WhiteTimeMs);
        Assert.Equal("blitz", response.Match.TimeFormat.Category);
    }

    [Fact]
    public async Task CreateMatch_TimeFormatWithIdOnly_ResolvesFromRegistry()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeFormat = new TimeFormat { Id = "5+3" },
            },
            ctx.CallContext);

        Assert.Equal(300_000L, response.Match.WhiteTimeMs);
        Assert.Equal(3_000L, response.Match.TimeFormat.IncrementMs);
        Assert.Equal("blitz", response.Match.TimeFormat.Category);
    }

    [Fact]
    public async Task CreateMatch_WithCustomStartFen_SeedsCurrentFen()
    {
        GrpcServiceContext ctx = new();
        const string fen = "rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq c6 0 2";

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { BotId = "bot-a" },
                Black = new Player { BotId = "bot-b" },
                TimeFormat = BlitzFormat(),
                StartFen = fen,
            },
            ctx.CallContext);

        Assert.Equal(fen, response.Match.CurrentFen);
    }

    [Fact]
    public async Task CreateMatch_WithStandardStartFen_UsesInitialPosition()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeFormat = BlitzFormat(),
                StartFen = "standard",
            },
            ctx.CallContext);

        Assert.Equal(InitialFen, response.Match.CurrentFen);
    }

    [Fact]
    public async Task CreateMatch_WithInvalidStartFen_ThrowsRpcExceptionInvalidArgument()
    {
        GrpcServiceContext ctx = new();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.CreateMatch(
                new CreateMatchRequest
                {
                    White = new Player { UserId = "w" },
                    Black = new Player { UserId = "b" },
                    TimeFormat = BlitzFormat(),
                    StartFen = "not-a-fen",
                },
                ctx.CallContext));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // ── GetMatch ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMatch_ExistingMatch_ReturnsMatch()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1");
        ctx.SetupMatch(match);

        GetMatchResponse response = await ctx.Service.GetMatch(
            new GetMatchRequest { MatchId = "match-1" },
            ctx.CallContext);

        Assert.Equal("match-1", response.Match.Id);
        Assert.Equal(MatchStatus.Ongoing, response.Match.Status);
    }

    [Fact]
    public async Task GetMatch_WhiteWonMatch_ReturnsWhiteWonStatus()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b", "white_won");
        ctx.SetupMatch(match);

        GetMatchResponse response = await ctx.Service.GetMatch(
            new GetMatchRequest { MatchId = "match-1" },
            ctx.CallContext);

        Assert.Equal(MatchStatus.WhiteWon, response.Match.Status);
    }

    [Fact]
    public async Task GetMatch_BlackWonMatch_ReturnsBlackWonStatus()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b", "black_won");
        ctx.SetupMatch(match);

        GetMatchResponse response = await ctx.Service.GetMatch(
            new GetMatchRequest { MatchId = "match-1" },
            ctx.CallContext);

        Assert.Equal(MatchStatus.BlackWon, response.Match.Status);
    }

    [Fact]
    public async Task GetMatch_DrawMatch_ReturnsDrawStatus()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b", "draw");
        ctx.SetupMatch(match);

        GetMatchResponse response = await ctx.Service.GetMatch(
            new GetMatchRequest { MatchId = "match-1" },
            ctx.CallContext);

        Assert.Equal(MatchStatus.Draw, response.Match.Status);
    }

    [Fact]
    public async Task GetMatch_PreservesTimeFormatOnMatch()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b", "ongoing", "rapid");
        ctx.SetupMatch(match);

        GetMatchResponse response = await ctx.Service.GetMatch(
            new GetMatchRequest { MatchId = "match-1" },
            ctx.CallContext);

        Assert.Equal("10+0", response.Match.TimeFormat.Id);
        Assert.Equal("rapid", response.Match.TimeFormat.Category);
    }

    [Fact]
    public async Task GetMatch_NotFound_ThrowsRpcExceptionNotFound()
    {
        GrpcServiceContext ctx = new();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.GetMatch(
                new GetMatchRequest { MatchId = "unknown" },
                ctx.CallContext));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    // ── ListMatches ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListMatches_ReturnsRepositoryResults()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1");
        ctx.SetupListMatches([match], total: 1);

        ListMatchesResponse response = await ctx.Service.ListMatches(
            new ListMatchesRequest
            {
                Status = MatchStatusFilter.Ongoing,
                Category = "blitz",
                Page = 1,
                PageSize = 20,
            },
            ctx.CallContext);

        Assert.Single(response.Matches);
        Assert.Equal(1, response.Total);
        Assert.Equal(20, response.PageSize);
        Assert.Equal("match-1", response.Matches[0].Id);
    }

    [Fact]
    public async Task ListMatches_DefaultsPageAndSizeWhenZero()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupListMatches([], total: 0);

        ListMatchesResponse response = await ctx.Service.ListMatches(
            new ListMatchesRequest { Status = MatchStatusFilter.Ongoing },
            ctx.CallContext);

        Assert.Equal(1, response.Page);
        Assert.Equal(20, response.PageSize);
    }

    [Fact]
    public async Task ListMatches_CapsPageSizeAt100()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupListMatches([], total: 0);

        ListMatchesResponse response = await ctx.Service.ListMatches(
            new ListMatchesRequest
            {
                Status = MatchStatusFilter.Ongoing,
                Page = 1,
                PageSize = 500,
            },
            ctx.CallContext);

        Assert.Equal(100, response.PageSize);
    }

    [Fact]
    public async Task ListMatches_UnspecifiedStatus_DefaultsToOngoing()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupListMatches([], total: 0);

        ListMatchesResponse response = await ctx.Service.ListMatches(
            new ListMatchesRequest { Status = MatchStatusFilter.Unspecified },
            ctx.CallContext);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task CreateMatch_WithExplicitCreatedBy_StampsInitiatorAndNativeSource()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { BotId = "bot-a" },
                Black = new Player { BotId = "bot-b" },
                TimeFormat = BlitzFormat(),
                CreatedBy = new Player { UserId = "starter-1" },
            },
            ctx.CallContext);

        Assert.Equal("starter-1", response.Match.CreatedBy.UserId);
        Assert.Equal(MatchSource.Native, response.Match.Source);
        Assert.Equal(0L, response.Match.FinishedAtMs);
    }

    [Fact]
    public async Task CreateMatch_WithoutCreatedBy_DerivesFromHumanSide()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "white-1" },
                Black = new Player { BotId = "bot-b" },
                TimeFormat = BlitzFormat(),
            },
            ctx.CallContext);

        Assert.Equal("white-1", response.Match.CreatedBy.UserId);
    }

    [Fact]
    public async Task CreateMatch_WithBotCreatedBy_StampsBotInitiator()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "white-1" },
                Black = new Player { UserId = "black-1" },
                TimeFormat = BlitzFormat(),
                CreatedBy = new Player { BotId = "watcher-bot" },
            },
            ctx.CallContext);

        Assert.Equal("watcher-bot", response.Match.CreatedBy.BotId);
    }

    [Fact]
    public async Task CreateMatch_WithIdentitylessCreatedBy_DerivesFromHumanSide()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "white-1" },
                Black = new Player { BotId = "bot-b" },
                TimeFormat = BlitzFormat(),
                CreatedBy = new Player(),
            },
            ctx.CallContext);

        Assert.Equal("white-1", response.Match.CreatedBy.UserId);
    }

    // ── ListUserMatches ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListUserMatches_ReturnsEndedMatchesForUser()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildMatch(
            "match-1",
            new PlayerDocument { UserId = "user-1" },
            new PlayerDocument { UserId = "opp" },
            status: "white_won",
            finishedAtMs: 5000);
        ctx.SetupFindForUser([match]);

        ListUserMatchesResponse response = await ctx.Service.ListUserMatches(
            new ListUserMatchesRequest
            {
                UserId = "user-1",
                Status = MatchStatusFilter.Ended,
                Page = 1,
                PageSize = 20,
            },
            ctx.CallContext);

        Assert.Single(response.Matches);
        Assert.Equal("match-1", response.Matches[0].Id);
        Assert.Equal(MatchStatus.WhiteWon, response.Matches[0].Status);
        Assert.Equal(5000L, response.Matches[0].FinishedAtMs);
        Assert.Equal(MatchSource.Native, response.Matches[0].Source);
        Assert.Equal(1, response.Total);
    }

    [Fact]
    public async Task ListUserMatches_MapsExternalSourceAndProvider()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildMatch(
            "match-1",
            new PlayerDocument { UserId = "user-1" },
            new PlayerDocument { UserId = "opp" },
            status: "draw",
            finishedAtMs: 1);
        match.Source = "external";
        match.ExternalProvider = "lichess";
        ctx.SetupFindForUser([match]);

        ListUserMatchesResponse response = await ctx.Service.ListUserMatches(
            new ListUserMatchesRequest { UserId = "user-1" },
            ctx.CallContext);

        Assert.Equal(MatchSource.External, response.Matches[0].Source);
        Assert.Equal("lichess", response.Matches[0].ExternalProvider);
    }

    [Fact]
    public async Task ListUserMatches_DefaultsPageAndSizeWhenZero()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupFindForUser([]);

        ListUserMatchesResponse response = await ctx.Service.ListUserMatches(
            new ListUserMatchesRequest { UserId = "user-1" },
            ctx.CallContext);

        Assert.Equal(1, response.Page);
        Assert.Equal(20, response.PageSize);
    }

    [Fact]
    public async Task ListUserMatches_OngoingFilter_CapsPageSizeAt100()
    {
        GrpcServiceContext ctx = new();
        ctx.SetupFindForUser([]);

        ListUserMatchesResponse response = await ctx.Service.ListUserMatches(
            new ListUserMatchesRequest
            {
                UserId = "user-1",
                Status = MatchStatusFilter.Ongoing,
                Page = 1,
                PageSize = 500,
            },
            ctx.CallContext);

        Assert.Equal(100, response.PageSize);
    }

    // Matches.MakeMove / Matches.ResignMatch gRPC RPCs were retired in Kafka task 06
    // (moves ride match.events.v1; the synchronous gRPC move path is gone). The
    // command-side behaviour is covered by MatchCommandsTests + MatchServiceCommandTests.

    // ── CreateMatch — external ──────────────────────────────────────────────

    [Fact]
    public async Task CreateMatch_External_SetsSourceAndProvider()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { ExternalName = "Alice" },
                Black = new Player { ExternalName = "Bob" },
                TimeFormat = BlitzFormat(),
                Source = MatchSource.External,
                ExternalProvider = "tournament-server",
                ExternalRef = "game-99",
            },
            ctx.CallContext);

        Assert.Equal(MatchSource.External, response.Match.Source);
        Assert.Equal("tournament-server", response.Match.ExternalProvider);
        Assert.Equal("game-99", response.Match.ExternalRef);
    }

    [Fact]
    public async Task CreateMatch_ExternalNamePlayer_MapsToProtoExternalName()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { ExternalName = "Alice" },
                Black = new Player { BotId = "bot-a" },
                TimeFormat = BlitzFormat(),
                Source = MatchSource.External,
                ExternalProvider = "ts",
            },
            ctx.CallContext);

        Assert.Equal("Alice", response.Match.White.ExternalName);
        Assert.Equal("bot-a", response.Match.Black.BotId);
    }

    // ── SyncExternalMatch ───────────────────────────────────────────────────

    [Fact]
    public async Task SyncExternalMatch_UpdatesStateAndReturnsMatch()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildMatch(
            "ext-1",
            new PlayerDocument { ExternalName = "Alice" },
            new PlayerDocument { ExternalName = "Bob" });
        match.Source = "external";
        ctx.SetupMatch(match);

        SyncExternalMatchResponse response = await ctx.Service.SyncExternalMatch(
            new SyncExternalMatchRequest
            {
                MatchId = "ext-1",
                CurrentFen = "fen-after-e4",
                Moves = { "e2e4" },
                Status = MatchStatus.Ongoing,
                WhiteTimeMs = 290_000,
                BlackTimeMs = 300_000,
            },
            ctx.CallContext);

        Assert.Equal("ext-1", response.Match.Id);
        Assert.Equal("fen-after-e4", response.Match.CurrentFen);
        Assert.Single(response.Match.Moves);
        Assert.Equal(290_000L, response.Match.WhiteTimeMs);
    }

    [Fact]
    public async Task SyncExternalMatch_NotFound_ThrowsRpcExceptionNotFound()
    {
        GrpcServiceContext ctx = new();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.SyncExternalMatch(
                new SyncExternalMatchRequest { MatchId = "missing" },
                ctx.CallContext));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task SyncExternalMatch_NativeMatch_ThrowsRpcExceptionFailedPrecondition()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("nat-1", "w", "b");
        ctx.SetupMatch(match);

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.SyncExternalMatch(
                new SyncExternalMatchRequest { MatchId = "nat-1" },
                ctx.CallContext));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    // ── GetMatchPosition ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetMatchPosition_ValidIndex_ReturnsFenAndMove()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b", "white_won");
        match.Moves.Add("e2e4");
        match.FenHistory.Add(BlackToMoveFen);
        match.CurrentFen = BlackToMoveFen;
        ctx.SetupMatch(match);

        GetMatchPositionResponse response = await ctx.Service.GetMatchPosition(
            new GetMatchPositionRequest { MatchId = "match-1", Index = 1 },
            ctx.CallContext);

        Assert.Equal(BlackToMoveFen, response.Fen);
        Assert.Equal("e2e4", response.Move);
        Assert.True(response.IsCurrent);
    }

    [Fact]
    public async Task GetMatchPosition_NotFound_ThrowsRpcExceptionNotFound()
    {
        GrpcServiceContext ctx = new();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.GetMatchPosition(
                new GetMatchPositionRequest { MatchId = "unknown", Index = 0 },
                ctx.CallContext));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task GetMatchPosition_AnalysisNotPermitted_ThrowsRpcExceptionPermissionDenied()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b");
        ctx.SetupMatch(match);

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.GetMatchPosition(
                new GetMatchPositionRequest { MatchId = "match-1", Index = 0 },
                ctx.CallContext));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task GetMatchPosition_IndexOutOfRange_ThrowsRpcExceptionInvalidArgument()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b", "white_won");
        ctx.SetupMatch(match);

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.GetMatchPosition(
                new GetMatchPositionRequest { MatchId = "match-1", Index = 99 },
                ctx.CallContext));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
