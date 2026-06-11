using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Tests.Support;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// The global match-browse query behind the Dev "All games" browser. The repository
// returns a candidate set (mocked here); these assert the service-layer membership,
// status, source, time-range filtering, chronological ordering, and paging that the
// MatchRepository.SearchAsync push-down leaves to the service.
public sealed class MatchServiceSearchTests
{
    private static MatchDocument Match(
        string id,
        string white,
        string black,
        string status = "white_won",
        long finishedAt = 0,
        string? createdBy = null,
        string source = "native")
    {
        MatchDocument match = MatchServiceContext.BuildMatch(
            id,
            new PlayerDocument { UserId = white },
            new PlayerDocument { UserId = black },
            status: status,
            createdBy: createdBy is null ? null : new PlayerDocument { UserId = createdBy },
            finishedAtMs: finishedAt);
        match.Source = source;
        return match;
    }

    private static MatchDocument BotMatch(
        string id,
        string createdBy,
        long finishedAt,
        string source = "native")
    {
        MatchDocument match = MatchServiceContext.BuildMatch(
            id,
            new PlayerDocument { BotId = "bot-a" },
            new PlayerDocument { BotId = "bot-b" },
            status: "draw",
            createdBy: new PlayerDocument { UserId = createdBy },
            finishedAtMs: finishedAt);
        match.Source = source;
        return match;
    }

    private static Task<(IReadOnlyList<MatchDocument> Matches, int Total)> Search(
        MatchServiceContext ctx,
        IEnumerable<MatchDocument> candidates,
        string? playerId = null,
        string? initiatorId = null,
        string status = "all",
        string source = "all",
        long sinceMs = 0,
        long untilMs = 0,
        bool ascending = false,
        int page = 1,
        int pageSize = 20)
    {
        ctx.SetupSearch(candidates);
        return ctx.MatchService.SearchMatchesAsync(
            playerId, initiatorId, status, source, sinceMs, untilMs, ascending, page, pageSize,
            CancellationToken.None);
    }

    [Fact]
    public async Task NoFilters_ReturnsAllNewestFirst()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, int total) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", finishedAt: 1000),
                Match("m2", "carol", "dave", finishedAt: 3000),
                Match("m3", "erin", "frank", finishedAt: 2000),
            ]);

        Assert.Equal(3, total);
        Assert.Equal(new[] {"m2", "m3", "m1" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task PlayerFilter_KeepsOnlyParticipantGames()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", finishedAt: 1000),
                Match("m2", "carol", "alice", finishedAt: 2000),
                BotMatch("m3", createdBy: "alice", finishedAt: 3000),
                Match("mX", "carol", "dave", finishedAt: 4000),
            ],
            playerId: "alice");

        // Participant filter excludes the bot-vs-bot game alice only *initiated*.
        Assert.Equal(new[] {"m2", "m1" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task InitiatorFilter_KeepsOnlyCreatedByGames()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                BotMatch("m1", createdBy: "alice", finishedAt: 1000),
                BotMatch("m2", createdBy: "bob", finishedAt: 2000),
                Match("m3", "alice", "bob", finishedAt: 3000, createdBy: "carol"),
            ],
            initiatorId: "alice");

        Assert.Equal(new[] {"m1" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task PlayerAndInitiator_AreAnded()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                // alice participates AND carol initiated → kept.
                Match("m1", "alice", "bob", finishedAt: 1000, createdBy: "carol"),
                // alice participates but dave initiated → dropped.
                Match("m2", "alice", "bob", finishedAt: 2000, createdBy: "dave"),
                // carol initiated but alice does not participate → dropped.
                BotMatch("m3", createdBy: "carol", finishedAt: 3000),
            ],
            playerId: "alice",
            initiatorId: "carol");

        Assert.Equal(new[] {"m1" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task StatusOngoing_KeepsOnlyOngoing()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", status: "white_won", finishedAt: 1000),
                Match("m2", "carol", "dave", status: "ongoing"),
            ],
            status: "ongoing");

        Assert.Equal(new[] {"m2" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task StatusEnded_ExcludesOngoing()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", status: "white_won", finishedAt: 1000),
                Match("m2", "carol", "dave", status: "ongoing"),
            ],
            status: "ended");

        Assert.Equal(new[] {"m1" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task StatusAll_IncludesOngoingAndEnded()
    {
        MatchServiceContext ctx = new();
        (_, int total) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", status: "white_won", finishedAt: 1000),
                Match("m2", "carol", "dave", status: "ongoing"),
            ],
            status: "all");

        Assert.Equal(2, total);
    }

    [Fact]
    public async Task SourceNative_ExcludesExternal()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", finishedAt: 1000, source: "native"),
                Match("m2", "carol", "dave", finishedAt: 2000, source: "external"),
            ],
            source: "native");

        Assert.Equal(new[] {"m1" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task SourceExternal_ExcludesNative()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", finishedAt: 1000, source: "native"),
                Match("m2", "carol", "dave", finishedAt: 2000, source: "external"),
            ],
            source: "external");

        Assert.Equal(new[] {"m2" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task SinceMs_BoundsLowerInclusive()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", finishedAt: 1000),
                Match("m2", "carol", "dave", finishedAt: 2000),
                Match("m3", "erin", "frank", finishedAt: 3000),
            ],
            sinceMs: 2000);

        Assert.Equal(new[] {"m3", "m2" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task UntilMs_BoundsUpperInclusive()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", finishedAt: 1000),
                Match("m2", "carol", "dave", finishedAt: 2000),
                Match("m3", "erin", "frank", finishedAt: 3000),
            ],
            untilMs: 2000);

        Assert.Equal(new[] {"m2", "m1" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task Ascending_FlipsOrderToOldestFirst()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", finishedAt: 1000),
                Match("m2", "carol", "dave", finishedAt: 3000),
                Match("m3", "erin", "frank", finishedAt: 2000),
            ],
            ascending: true);

        Assert.Equal(new[] {"m1", "m3", "m2" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task Paging_ReturnsRequestedSliceAndFullTotal()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, int total) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", finishedAt: 1000),
                Match("m2", "alice", "bob", finishedAt: 2000),
                Match("m3", "alice", "bob", finishedAt: 3000),
            ],
            page: 2,
            pageSize: 2);

        Assert.Equal(3, total);
        Assert.Equal(new[] {"m1" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task EmptyCandidateSet_ReturnsEmpty()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, int total) = await Search(ctx, []);

        Assert.Empty(matches);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task PlayerFilter_MatchesOnCanonicalGuidForm()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [Match("m1", "A1B2C3D4-1111-2222-3333-444455556666", "bob", finishedAt: 1000)],
            playerId: "a1b2c3d4-1111-2222-3333-444455556666");

        Assert.Equal(new[] {"m1" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task InitiatorFilter_MatchesOnCanonicalGuidForm()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [BotMatch("m1", createdBy: "A1B2C3D4-1111-2222-3333-444455556666", finishedAt: 1000)],
            initiatorId: "a1b2c3d4-1111-2222-3333-444455556666");

        Assert.Equal(new[] {"m1" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task PageBelowOne_NormalizesToFirstPage()
    {
        MatchServiceContext ctx = new();
        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx,
            [
                Match("m1", "alice", "bob", finishedAt: 1000),
                Match("m2", "alice", "bob", finishedAt: 2000),
            ],
            page: 0,
            pageSize: 1);

        Assert.Equal(new[] {"m2" }, matches.Select(m => m.Id));
    }

    [Fact]
    public async Task PageSizeZero_DefaultsToTwenty()
    {
        MatchServiceContext ctx = new();
        List<MatchDocument> candidates = [];
        for (int i = 0; i < 25; i++)
        {
            candidates.Add(Match($"m{i}", "alice", "bob", finishedAt: i));
        }

        (IReadOnlyList<MatchDocument> matches, int total) = await Search(
            ctx, candidates, pageSize: 0);

        Assert.Equal(20, matches.Count);
        Assert.Equal(25, total);
    }

    [Fact]
    public async Task PageSizeAboveCap_IsCappedAtHundred()
    {
        MatchServiceContext ctx = new();
        List<MatchDocument> candidates = [];
        for (int i = 0; i < 150; i++)
        {
            candidates.Add(Match($"m{i}", "alice", "bob", finishedAt: i));
        }

        (IReadOnlyList<MatchDocument> matches, _) = await Search(
            ctx, candidates, pageSize: 500);

        Assert.Equal(100, matches.Count);
    }
}
