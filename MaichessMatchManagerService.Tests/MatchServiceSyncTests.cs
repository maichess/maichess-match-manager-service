using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Tests.Support;
using NSubstitute;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// The external-match mirror path (SyncExternalMatchAsync): it replaces the durable
// document, broadcasts the latest move and (when the game has ended) the match-ended
// event, and stamps finished_at only for a genuinely finished sync. These assert the
// side effects — repository write, socket fan-out, and finished-at gating — that the
// BDD scenarios leave implicit.
public sealed class MatchServiceSyncTests
{
    private const string Fen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string FenAfter = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";

    private static MatchServiceContext WithExternalMatch()
    {
        MatchServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildMatch(
            "ext-1",
            new PlayerDocument { ExternalName = "Alice" },
            new PlayerDocument { ExternalName = "Bob" });
        match.Source = "external";
        ctx.SetupMatch(match);
        return ctx;
    }

    [Fact]
    public async Task Sync_NewMove_ReplacesDocumentAndBroadcastsMoveByWhite()
    {
        MatchServiceContext ctx = WithExternalMatch();

        await ctx.MatchService.SyncExternalMatchAsync(
            "ext-1", FenAfter, ["e2e4"], "ongoing", 290_000, 300_000, 0, "checkmate", CancellationToken.None);

        await ctx.Repository.Received(1).ReplaceAsync(
            Arg.Is<MatchDocument>(m => m.Id == "ext-1"), Arg.Any<CancellationToken>());
        // One new move (odd count) ⇒ white moved; index is the new move count.
        ctx.SocketBroadcaster.Received(1).BroadcastMoveMade(
            Arg.Any<MatchDocument>(),
            "e2e4",
            FenAfter,
            1,
            Arg.Is<PlayerDocument>(p => p.ExternalName == "Alice"),
            290_000,
            300_000);
        ctx.SocketBroadcaster.DidNotReceive().BroadcastMatchEnded(
            Arg.Any<MatchDocument>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Sync_TwoMoves_AttributesLatestMoveToBlack()
    {
        MatchServiceContext ctx = WithExternalMatch();

        await ctx.MatchService.SyncExternalMatchAsync(
            "ext-1", Fen, ["e2e4", "e7e5"], "ongoing", 290_000, 280_000, 0, "checkmate", CancellationToken.None);

        // Two moves (even count) ⇒ black made the latest move.
        ctx.SocketBroadcaster.Received(1).BroadcastMoveMade(
            Arg.Any<MatchDocument>(),
            "e7e5",
            Fen,
            2,
            Arg.Is<PlayerDocument>(p => p.ExternalName == "Bob"),
            290_000,
            280_000);
    }

    [Fact]
    public async Task Sync_NoNewMoves_DoesNotBroadcastAMove()
    {
        MatchServiceContext ctx = WithExternalMatch();

        // The stored match has zero moves; syncing an empty move list adds none.
        await ctx.MatchService.SyncExternalMatchAsync(
            "ext-1", Fen, [], "ongoing", 290_000, 300_000, 0, "checkmate", CancellationToken.None);

        ctx.SocketBroadcaster.DidNotReceive().BroadcastMoveMade(
            Arg.Any<MatchDocument>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<PlayerDocument>(), Arg.Any<long>(), Arg.Any<long>());
    }

    [Fact]
    public async Task Sync_EndedStatus_BroadcastsMatchEndedAndStampsFinishedAt()
    {
        MatchServiceContext ctx = WithExternalMatch();

        MatchDocument result = await ctx.MatchService.SyncExternalMatchAsync(
            "ext-1", Fen, [], "white_won", 290_000, 300_000, 9_000, "checkmate", CancellationToken.None);

        ctx.SocketBroadcaster.Received(1).BroadcastMatchEnded(
            Arg.Any<MatchDocument>(), "white_won", "checkmate");
        Assert.Equal(9_000, result.FinishedAtMs);
    }

    [Fact]
    public async Task Sync_OngoingWithFinishedAt_DoesNotStampFinishedAt()
    {
        MatchServiceContext ctx = WithExternalMatch();

        // An ongoing sync never records finished_at, even if the caller supplies one.
        MatchDocument result = await ctx.MatchService.SyncExternalMatchAsync(
            "ext-1", Fen, [], "ongoing", 290_000, 300_000, 9_000, "checkmate", CancellationToken.None);

        Assert.Equal(0, result.FinishedAtMs);
    }

    [Fact]
    public async Task Sync_NativeMatch_ThrowsInvalidOperationWithExplanatoryMessage()
    {
        MatchServiceContext ctx = new();
        ctx.SetupMatch(MatchServiceContext.BuildHumanMatch("nat-1", "w", "b"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.MatchService.SyncExternalMatchAsync(
                "nat-1", Fen, [], "ongoing", 0, 0, 0, "checkmate", CancellationToken.None));

        Assert.Equal("SyncExternalMatch is only valid for external matches", ex.Message);
    }
}
