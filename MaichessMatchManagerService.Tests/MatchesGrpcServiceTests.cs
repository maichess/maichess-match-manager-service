using Grpc.Core;
using Maichess.MatchManager.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;
using MaichessMatchManagerService.Tests.Support;
using NSubstitute;
using Xunit;

namespace MaichessMatchManagerService.Tests;

public sealed class MatchesGrpcServiceTests
{
    private const string InitialFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string BlackToMoveFen = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";

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
                TimeControl = TimeControl.Blitz,
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
                TimeControl = TimeControl.Blitz,
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
                    White = new Player(), // no identity
                    Black = new Player { UserId = "black-1" },
                    TimeControl = TimeControl.Blitz,
                },
                ctx.CallContext));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task CreateMatch_BulletTimeControl_SetsCorrectTimeMs()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeControl = TimeControl.Bullet,
            },
            ctx.CallContext);

        Assert.Equal(180_000L, response.Match.WhiteTimeMs);
        Assert.Equal(TimeControl.Bullet, response.Match.TimeControl);
    }

    [Fact]
    public async Task CreateMatch_RapidTimeControl_SetsCorrectTimeMs()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeControl = TimeControl.Rapid,
            },
            ctx.CallContext);

        Assert.Equal(600_000L, response.Match.WhiteTimeMs);
        Assert.Equal(TimeControl.Rapid, response.Match.TimeControl);
    }

    [Fact]
    public async Task CreateMatch_ClassicalTimeControl_SetsCorrectTimeMs()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeControl = TimeControl.Classical,
            },
            ctx.CallContext);

        Assert.Equal(1_800_000L, response.Match.WhiteTimeMs);
        Assert.Equal(TimeControl.Classical, response.Match.TimeControl);
    }

    [Fact]
    public async Task CreateMatch_UnknownTimeControl_DefaultsToBlitz()
    {
        GrpcServiceContext ctx = new();

        CreateMatchResponse response = await ctx.Service.CreateMatch(
            new CreateMatchRequest
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeControl = (TimeControl)999,
            },
            ctx.CallContext);

        Assert.Equal(300_000L, response.Match.WhiteTimeMs);
        Assert.Equal(TimeControl.Blitz, response.Match.TimeControl);
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
    public async Task GetMatch_MatchWithUnknownTimeControl_DefaultsToBlitzProtoTimeControl()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b");
        match.TimeControl = "custom_unknown";
        ctx.SetupMatch(match);

        GetMatchResponse response = await ctx.Service.GetMatch(
            new GetMatchRequest { MatchId = "match-1" },
            ctx.CallContext);

        Assert.Equal(TimeControl.Blitz, response.Match.TimeControl);
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

    // ── MakeMove ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task MakeMove_ValidMove_ReturnsUpdatedMatch()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1");
        ctx.SetupMatch(match);
        ctx.SetupMoveValidatorAccepts("e2e4", BlackToMoveFen);

        MakeMoveResponse response = await ctx.Service.MakeMove(
            new MakeMoveRequest { MatchId = "match-1", UserId = "white-1", Move = "e2e4" },
            ctx.CallContext);

        Assert.Equal(BlackToMoveFen, response.Match.CurrentFen);
    }

    [Fact]
    public async Task MakeMove_NotFound_ThrowsRpcExceptionNotFound()
    {
        GrpcServiceContext ctx = new();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.MakeMove(
                new MakeMoveRequest { MatchId = "unknown", UserId = "w", Move = "e2e4" },
                ctx.CallContext));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task MakeMove_AlreadyEnded_ThrowsRpcExceptionFailedPrecondition()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b", "white_won");
        ctx.SetupMatch(match);

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.MakeMove(
                new MakeMoveRequest { MatchId = "match-1", UserId = "w", Move = "e2e4" },
                ctx.CallContext));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task MakeMove_NotParticipant_ThrowsRpcExceptionPermissionDenied()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1");
        ctx.SetupMatch(match);

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.MakeMove(
                new MakeMoveRequest { MatchId = "match-1", UserId = "outsider", Move = "e2e4" },
                ctx.CallContext));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task MakeMove_NotYourTurn_ThrowsRpcExceptionPermissionDenied()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1");
        ctx.SetupMatch(match);

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.MakeMove(
                new MakeMoveRequest { MatchId = "match-1", UserId = "black-1", Move = "e7e5" },
                ctx.CallContext));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task MakeMove_IllegalMove_ThrowsRpcExceptionInvalidArgument()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1");
        ctx.SetupMatch(match);
        ctx.SetupMoveValidatorRejects("Piece cannot move there");

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.MakeMove(
                new MakeMoveRequest { MatchId = "match-1", UserId = "white-1", Move = "e2e6" },
                ctx.CallContext));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // ── ResignMatch ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ResignMatch_Success_ReturnsMatchWithUpdatedStatus()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1");
        ctx.SetupMatch(match);

        ResignMatchResponse response = await ctx.Service.ResignMatch(
            new ResignMatchRequest { MatchId = "match-1", UserId = "white-1" },
            ctx.CallContext);

        Assert.Equal(MatchStatus.BlackWon, response.Match.Status);
    }

    [Fact]
    public async Task ResignMatch_NotFound_ThrowsRpcExceptionNotFound()
    {
        GrpcServiceContext ctx = new();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.ResignMatch(
                new ResignMatchRequest { MatchId = "unknown", UserId = "w" },
                ctx.CallContext));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ResignMatch_AlreadyEnded_ThrowsRpcExceptionFailedPrecondition()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b", "black_won");
        ctx.SetupMatch(match);

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.ResignMatch(
                new ResignMatchRequest { MatchId = "match-1", UserId = "w" },
                ctx.CallContext));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task ResignMatch_NotParticipant_ThrowsRpcExceptionPermissionDenied()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b");
        ctx.SetupMatch(match);

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.ResignMatch(
                new ResignMatchRequest { MatchId = "match-1", UserId = "outsider" },
                ctx.CallContext));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
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
        // Ongoing human vs human match is not analyzable.
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

    // ── StreamMatch ──────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamMatch_NotFound_ThrowsRpcExceptionNotFound()
    {
        GrpcServiceContext ctx = new();
        IServerStreamWriter<MatchEvent> responseStream =
            Substitute.For<IServerStreamWriter<MatchEvent>>();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            ctx.Service.StreamMatch(
                new StreamMatchRequest { MatchId = "unknown" },
                responseStream,
                ctx.CallContext));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task StreamMatch_MoveMadeNotification_WritesMoveMadeEvent()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1");
        ctx.SetupMatch(match);

        IServerStreamWriter<MatchEvent> responseStream = Substitute.For<IServerStreamWriter<MatchEvent>>();
        responseStream.WriteAsync(Arg.Any<MatchEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Task streamTask = ctx.Service.StreamMatch(
            new StreamMatchRequest { MatchId = "match-1" },
            responseStream,
            ctx.CallContext);

        ctx.Broadcaster.Broadcast("match-1",
            new MoveMadeNotification("e2e4", BlackToMoveFen, 1, match.White, 300_000, 300_000));
        ctx.Broadcaster.Complete("match-1");

        await streamTask;

        await responseStream.Received(1).WriteAsync(
            Arg.Is<MatchEvent>(e => e.MoveMade != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamMatch_MatchEndedNotification_WritesMatchEndedEvent()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b");
        ctx.SetupMatch(match);

        IServerStreamWriter<MatchEvent> responseStream = Substitute.For<IServerStreamWriter<MatchEvent>>();
        responseStream.WriteAsync(Arg.Any<MatchEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Task streamTask = ctx.Service.StreamMatch(
            new StreamMatchRequest { MatchId = "match-1" },
            responseStream,
            ctx.CallContext);

        ctx.Broadcaster.Broadcast("match-1", new MatchEndedNotification("white_won", "checkmate"));
        ctx.Broadcaster.Complete("match-1");

        await streamTask;

        await responseStream.Received(1).WriteAsync(
            Arg.Is<MatchEvent>(e => e.MatchEnded != null && e.MatchEnded.Reason == EndReason.Checkmate),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamMatch_DrawOfferedNotification_WritesDrawOfferedEvent()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1");
        ctx.SetupMatch(match);

        IServerStreamWriter<MatchEvent> responseStream = Substitute.For<IServerStreamWriter<MatchEvent>>();
        responseStream.WriteAsync(Arg.Any<MatchEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Task streamTask = ctx.Service.StreamMatch(
            new StreamMatchRequest { MatchId = "match-1" },
            responseStream,
            ctx.CallContext);

        ctx.Broadcaster.Broadcast("match-1", new DrawOfferedNotification(match.White));
        ctx.Broadcaster.Complete("match-1");

        await streamTask;

        await responseStream.Received(1).WriteAsync(
            Arg.Is<MatchEvent>(e => e.DrawOffered != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamMatch_DrawDeclinedNotification_WritesDrawDeclinedEvent()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1");
        ctx.SetupMatch(match);

        IServerStreamWriter<MatchEvent> responseStream = Substitute.For<IServerStreamWriter<MatchEvent>>();
        responseStream.WriteAsync(Arg.Any<MatchEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Task streamTask = ctx.Service.StreamMatch(
            new StreamMatchRequest { MatchId = "match-1" },
            responseStream,
            ctx.CallContext);

        ctx.Broadcaster.Broadcast("match-1", new DrawDeclinedNotification(match.Black));
        ctx.Broadcaster.Complete("match-1");

        await streamTask;

        await responseStream.Received(1).WriteAsync(
            Arg.Is<MatchEvent>(e => e.DrawDeclined != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamMatch_UnknownNotificationType_ThrowsInvalidOperationException()
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b");
        ctx.SetupMatch(match);

        IServerStreamWriter<MatchEvent> responseStream = Substitute.For<IServerStreamWriter<MatchEvent>>();

        Task streamTask = ctx.Service.StreamMatch(
            new StreamMatchRequest { MatchId = "match-1" },
            responseStream,
            ctx.CallContext);

        ctx.Broadcaster.Broadcast("match-1", new UnknownTestNotification());
        ctx.Broadcaster.Complete("match-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => streamTask);
    }

    // ── ToProtoEndReason coverage via MatchEnded events ──────────────────────

    [Theory]
    [InlineData("resignation", EndReason.Resignation)]
    [InlineData("stalemate", EndReason.Stalemate)]
    [InlineData("timeout", EndReason.Timeout)]
    [InlineData("draw_agreement", EndReason.DrawAgreement)]
    [InlineData("fifty_move_rule", EndReason.FiftyMoveRule)]
    [InlineData("threefold_repetition", EndReason.ThreefoldRepetition)]
    [InlineData("insufficient_material", EndReason.InsufficientMaterial)]
    [InlineData("unknown_reason", EndReason.Unspecified)]
    public async Task StreamMatch_MatchEndedWithReason_MapsToCorrectEndReason(
        string reason, EndReason expectedEndReason)
    {
        GrpcServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("match-1", "w", "b");
        ctx.SetupMatch(match);

        IServerStreamWriter<MatchEvent> responseStream = Substitute.For<IServerStreamWriter<MatchEvent>>();
        MatchEvent? capturedEvent = null;
        responseStream.WriteAsync(Arg.Do<MatchEvent>(e => capturedEvent = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Task streamTask = ctx.Service.StreamMatch(
            new StreamMatchRequest { MatchId = "match-1" },
            responseStream,
            ctx.CallContext);

        ctx.Broadcaster.Broadcast("match-1", new MatchEndedNotification("white_won", reason));
        ctx.Broadcaster.Complete("match-1");

        await streamTask;

        Assert.NotNull(capturedEvent);
        Assert.Equal(expectedEndReason, capturedEvent!.MatchEnded.Reason);
    }
}
