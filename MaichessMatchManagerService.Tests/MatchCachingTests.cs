using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;
using MaichessMatchManagerService.Tests.Support;
using NSubstitute;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Verifies the Redis finished-match caching orchestration in MatchService: the
// immutable doc cache (GetMatch), the ListUserMatches page cache, and the
// event-driven invalidation when a match ends. The cache is an adapter concern,
// so these assert on the mocked IMatchCache interactions, mirroring the plain
// xUnit style used for the other non-business-logic layers.
public sealed class MatchCachingTests
{
    private const string EndingFen = "rnbqkbnr/ppppp2p/5p2/6pQ/4P3/8/PPPP1PPP/RNB1KBNR b KQkq - 0 1";

    // ── Finished-match document cache (GetMatch) ─────────────────────────────

    [Fact]
    public async Task GetMatch_serves_ended_match_from_cache_without_touching_the_repository()
    {
        MatchServiceContext context = new();
        MatchDocument cached = MatchServiceContext.BuildHumanMatch("m1", "alice", "bob", "white_won");
        context.Cache.GetMatchAsync("m1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MatchDocument?>(cached));

        MatchDocument result = await context.MatchService.GetMatchAsync("m1", CancellationToken.None);

        Assert.Same(cached, result);
        await context.Repository.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await context.Cache.DidNotReceive().SetMatchAsync(Arg.Any<MatchDocument>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMatch_caches_an_ended_match_loaded_from_the_repository()
    {
        MatchServiceContext context = new();
        MatchDocument ended = MatchServiceContext.BuildHumanMatch("m1", "alice", "bob", "white_won");
        context.SetupMatch(ended);

        MatchDocument result = await context.MatchService.GetMatchAsync("m1", CancellationToken.None);

        Assert.Same(ended, result);
        await context.Cache.Received(1).SetMatchAsync(ended, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMatch_does_not_cache_an_ongoing_match()
    {
        MatchServiceContext context = new();
        MatchDocument ongoing = MatchServiceContext.BuildHumanMatch("m1", "alice", "bob", "ongoing");
        context.SetupMatch(ongoing);

        await context.MatchService.GetMatchAsync("m1", CancellationToken.None);

        await context.Cache.DidNotReceive().SetMatchAsync(Arg.Any<MatchDocument>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMatch_throws_and_caches_nothing_when_the_match_is_absent()
    {
        MatchServiceContext context = new();

        await Assert.ThrowsAsync<MatchNotFoundException>(
            () => context.MatchService.GetMatchAsync("missing", CancellationToken.None));

        await context.Cache.DidNotReceive().SetMatchAsync(Arg.Any<MatchDocument>(), Arg.Any<CancellationToken>());
    }

    // ── ListUserMatches page cache ───────────────────────────────────────────

    [Fact]
    public async Task ListUserMatches_serves_an_ended_page_from_cache_without_querying_match_db()
    {
        MatchServiceContext context = new();
        IReadOnlyList<MatchDocument> page = [MatchServiceContext.BuildHumanMatch("m1", "alice", "bob", "white_won")];
        context.Cache.GetUserPageAsync("alice", "ended", 1, 20, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<MatchDocument> Matches, int Total)?>((page, 7)));

        (IReadOnlyList<MatchDocument> Matches, int Total) result =
            await context.MatchService.ListUserMatchesAsync("alice", "ended", 1, 20, CancellationToken.None);

        Assert.Same(page, result.Matches);
        Assert.Equal(7, result.Total);
        await context.Repository.DidNotReceive().FindForUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListUserMatches_computes_and_caches_an_ended_page_on_a_miss()
    {
        MatchServiceContext context = new();
        context.SetupFindForUser([
            MatchServiceContext.BuildMatch(
                "m1",
                new PlayerDocument { UserId = "alice" },
                new PlayerDocument { UserId = "bob" },
                status: "white_won",
                finishedAtMs: 1000),
        ]);

        (IReadOnlyList<MatchDocument> Matches, int Total) result =
            await context.MatchService.ListUserMatchesAsync("alice", "ended", 1, 20, CancellationToken.None);

        Assert.Single(result.Matches);
        await context.Cache.Received(1).SetUserPageAsync(
            "alice", "ended", 1, 20,
            Arg.Is<IReadOnlyList<MatchDocument>>(m => m.Count == 1 && m[0].Id == "m1"),
            1,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListUserMatches_never_caches_the_ongoing_page()
    {
        MatchServiceContext context = new();
        context.SetupFindForUser([
            MatchServiceContext.BuildHumanMatch("m1", "alice", "bob", "ongoing"),
        ]);

        await context.MatchService.ListUserMatchesAsync("alice", "ongoing", 1, 20, CancellationToken.None);

        await context.Cache.DidNotReceive().GetUserPageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await context.Cache.DidNotReceive().SetUserPageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyList<MatchDocument>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListUserMatches_keys_the_cache_on_the_canonical_user_id()
    {
        MatchServiceContext context = new();
        const string upper = "A1B2C3D4-1111-2222-3333-444455556666";
        const string canonical = "a1b2c3d4-1111-2222-3333-444455556666";
        context.SetupFindForUser([]);

        await context.MatchService.ListUserMatchesAsync(upper, "ended", 1, 20, CancellationToken.None);

        await context.Cache.Received(1).GetUserPageAsync(
            canonical, "ended", 1, 20, Arg.Any<CancellationToken>());
        await context.Cache.Received(1).SetUserPageAsync(
            canonical, "ended", 1, 20, Arg.Any<IReadOnlyList<MatchDocument>>(), 0, Arg.Any<CancellationToken>());
    }

    // ── Event-driven invalidation on match end ───────────────────────────────
    // The native move/resign/draw/timeout end paths now flow through the projector's
    // write-through (Kafka task 06), which refreshes the finished-match cache and evicts
    // participant pages off the event log — exercised via the projector tests, not here.
    // Only the external-match sync path still ends a match synchronously in MatchService.

    [Fact]
    public async Task Syncing_an_external_match_to_an_ended_state_evicts_only_the_initiators_pages()
    {
        MatchServiceContext context = new();
        MatchDocument match = MatchServiceContext.BuildMatch(
            "ext1",
            new PlayerDocument { ExternalName = "WhiteBot" },
            new PlayerDocument { ExternalName = "BlackBot" },
            status: "ongoing",
            createdBy: new PlayerDocument { UserId = "dave" });
        match.Source = "external";
        context.SetupMatch(match);

        await context.MatchService.SyncExternalMatchAsync(
            "ext1", EndingFen, ["g4h5"], "white_won", 300_000, 300_000, 123, "checkmate", CancellationToken.None);

        await context.Cache.Received(1).SetMatchAsync(
            Arg.Is<MatchDocument>(m => m.Id == "ext1" && m.Status == "white_won"), Arg.Any<CancellationToken>());
        // White and black are external (null user ids), so only the initiator is evicted.
        await context.Cache.Received(1).InvalidateUserPagesAsync("dave", Arg.Any<CancellationToken>());
        await context.Cache.Received(1).InvalidateUserPagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
