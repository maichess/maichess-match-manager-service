using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Services;
using NSubstitute;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Read-side orchestration of the rating leaderboard: ranked ZSET positions enriched
// from the user replica, flagged players hidden, provisional ratings annotated.
public sealed class LeaderboardServiceTests
{
    private readonly ILeaderboard leaderboard = Substitute.For<ILeaderboard>();
    private readonly IUserReplica replica = Substitute.For<IUserReplica>();
    private readonly LeaderboardService service;

    public LeaderboardServiceTests()
    {
        service = new LeaderboardService(leaderboard, replica);
        leaderboard.CountAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(0L));
    }

    private void SetupTop(params LeaderboardEntry[] entries) =>
        leaderboard.TopAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<LeaderboardEntry>>([.. entries]));

    private void SetupCount(long count) =>
        leaderboard.CountAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(count));

    private void SetupReplica(
        string userId, string? username, int elo, double ratingDeviation, bool flagged = false) =>
        replica.GetAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserReplicaRecord?>(new UserReplicaRecord(
                username, null, ratingDeviation, null, elo, null, null, null, null, flagged)));

    [Fact]
    public async Task GetTop_EnrichesRowsInRankOrder_WithTotal()
    {
        SetupCount(2);
        SetupTop(new LeaderboardEntry("u1", 1800), new LeaderboardEntry("u2", 1700));
        SetupReplica("u1", "alice", 1800, 60);
        SetupReplica("u2", "bob", 1700, 70);

        LeaderboardPage page = await service.GetTopAsync(10, CancellationToken.None);

        Assert.Equal(2, page.Total);
        Assert.Collection(
            page.Rows,
            r => { Assert.Equal(1, r.Rank); Assert.Equal("u1", r.UserId); Assert.Equal("alice", r.Username); Assert.Equal(1800, r.Elo); Assert.False(r.Provisional); },
            r => { Assert.Equal(2, r.Rank); Assert.Equal("u2", r.UserId); Assert.Equal("bob", r.Username); });
    }

    [Fact]
    public async Task GetTop_HidesFlagged_AndKeepsRawRankNumbering()
    {
        SetupCount(3);
        SetupTop(
            new LeaderboardEntry("u1", 1800),
            new LeaderboardEntry("cheat", 1750),
            new LeaderboardEntry("u3", 1700));
        SetupReplica("u1", "alice", 1800, 50);
        SetupReplica("cheat", "mallory", 1750, 50, flagged: true);
        SetupReplica("u3", "carol", 1700, 50);

        LeaderboardPage page = await service.GetTopAsync(10, CancellationToken.None);

        // The flagged player is dropped but ranks reflect the true ZSET positions (1, 3).
        Assert.Equal(3, page.Total);
        Assert.Collection(
            page.Rows,
            r => { Assert.Equal(1, r.Rank); Assert.Equal("u1", r.UserId); },
            r => { Assert.Equal(3, r.Rank); Assert.Equal("u3", r.UserId); });
    }

    [Fact]
    public async Task GetTop_AnnotatesProvisional_ForHighDeviation()
    {
        SetupCount(1);
        SetupTop(new LeaderboardEntry("u1", 420));
        SetupReplica("u1", "newbie", 420, 290);

        LeaderboardPage page = await service.GetTopAsync(10, CancellationToken.None);

        LeaderboardRow row = Assert.Single(page.Rows);
        Assert.True(row.Provisional);
        Assert.Equal(290, row.RatingDeviation);
    }

    [Fact]
    public async Task GetTop_PrefersReplicaElo_OverRoundedZsetRating()
    {
        // The replica elo (1850) is authoritative; the ZSET rating (1800) is only the
        // fallback used when the replica is cold. They are deliberately distinct so a
        // row reporting 1800 would mean the replica elo was dropped.
        SetupCount(1);
        SetupTop(new LeaderboardEntry("u1", 1800));
        SetupReplica("u1", "alice", 1850, 50);

        LeaderboardPage page = await service.GetTopAsync(10, CancellationToken.None);

        Assert.Equal(1850, Assert.Single(page.Rows).Elo);
    }

    [Fact]
    public async Task GetTop_AtProvisionalThreshold_IsNotProvisional()
    {
        // A deviation exactly at the threshold is settled (the cutoff is strictly
        // greater-than), so the boundary value must not be annotated provisional.
        SetupCount(1);
        SetupTop(new LeaderboardEntry("u1", 1500));
        SetupReplica("u1", "alice", 1500, 110.0);

        LeaderboardPage page = await service.GetTopAsync(10, CancellationToken.None);

        Assert.False(Assert.Single(page.Rows).Provisional);
    }

    [Fact]
    public async Task GetTop_ColdReplica_FallsBackToRoundedRating_AndProvisional()
    {
        SetupCount(1);
        SetupTop(new LeaderboardEntry("u1", 1623.6));
        replica.GetAsync("u1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserReplicaRecord?>(null));

        LeaderboardPage page = await service.GetTopAsync(10, CancellationToken.None);

        LeaderboardRow row = Assert.Single(page.Rows);
        Assert.Null(row.Username);
        Assert.Equal(1624, row.Elo);
        Assert.True(row.Provisional);
    }

    [Fact]
    public async Task GetTop_RespectsRequestedLimit()
    {
        SetupCount(3);
        SetupTop(
            new LeaderboardEntry("u1", 1800),
            new LeaderboardEntry("u2", 1700),
            new LeaderboardEntry("u3", 1600));
        SetupReplica("u1", "a", 1800, 50);
        SetupReplica("u2", "b", 1700, 50);
        SetupReplica("u3", "c", 1600, 50);

        LeaderboardPage page = await service.GetTopAsync(2, CancellationToken.None);

        Assert.Equal(2, page.Rows.Count);
        Assert.Equal("u1", page.Rows[0].UserId);
        Assert.Equal("u2", page.Rows[1].UserId);
    }

    [Theory]
    [InlineData(0, LeaderboardService.DefaultLimit)]
    [InlineData(-5, LeaderboardService.DefaultLimit)]
    [InlineData(5000, LeaderboardService.MaxLimit)]
    public async Task GetTop_NormalisesLimit_WhenFetching(int requested, int expectedSize)
    {
        SetupCount(0);
        SetupTop();

        await service.GetTopAsync(requested, CancellationToken.None);

        // The fetch over-fetches by the flagged buffer beyond the normalised page size.
        await leaderboard.Received().TopAsync(
            Arg.Is<int>(c => c > expectedSize), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRank_ReturnsOneBasedRank_AndTotal()
    {
        leaderboard.RankAsync("u2", Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(4));
        leaderboard.ScoreAsync("u2", Arg.Any<CancellationToken>()).Returns(Task.FromResult<double?>(1555));
        SetupCount(42);
        SetupReplica("u2", "bob", 1555, 90);

        (LeaderboardRow Row, long Total)? result = await service.GetRankAsync("u2", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5, result!.Value.Row.Rank);
        Assert.Equal("bob", result.Value.Row.Username);
        Assert.False(result.Value.Row.Provisional);
        Assert.Equal(42, result.Value.Total);
    }

    [Fact]
    public async Task GetRank_NotOnBoard_ReturnsNull()
    {
        leaderboard.RankAsync("ghost", Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(null));
        leaderboard.ScoreAsync("ghost", Arg.Any<CancellationToken>()).Returns(Task.FromResult<double?>(null));

        Assert.Null(await service.GetRankAsync("ghost", CancellationToken.None));
    }

    [Fact]
    public async Task GetRank_RankWithoutScore_ReturnsNull()
    {
        leaderboard.RankAsync("u9", Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(0));
        leaderboard.ScoreAsync("u9", Arg.Any<CancellationToken>()).Returns(Task.FromResult<double?>(null));

        Assert.Null(await service.GetRankAsync("u9", CancellationToken.None));
    }
}
