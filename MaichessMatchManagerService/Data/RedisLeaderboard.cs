using System.Diagnostics.CodeAnalysis;
using StackExchange.Redis;

namespace MaichessMatchManagerService.Data;

// StackExchange.Redis implementation of the rating leaderboard. A single sorted set at
// leaderboard:rating holds userId members scored by their Glicko-2 rating; ranked reads
// are native ZSET ops (ZREVRANGE / ZREVRANK / ZSCORE / ZCARD). No expiry — the set is
// rebuildable by replaying user.events.v1, so it survives allkeys-lru like the user
// replica. Excluded from coverage (requires live Redis); the read orchestration in
// LeaderboardService is unit-tested against a mocked ILeaderboard.
[ExcludeFromCodeCoverage]
internal sealed class RedisLeaderboard(IConnectionMultiplexer redis) : ILeaderboard
{
    private const string Key = "leaderboard:rating";

    private IDatabase Db => redis.GetDatabase();

    public async Task UpsertAsync(string userId, double rating, CancellationToken ct) =>
        await Db.SortedSetAddAsync(Key, userId, rating);

    public async Task<IReadOnlyList<LeaderboardEntry>> TopAsync(int count, CancellationToken ct)
    {
        if (count <= 0)
        {
            return [];
        }

        SortedSetEntry[] entries = await Db.SortedSetRangeByRankWithScoresAsync(
            Key, 0, count - 1, Order.Descending);
        return [.. entries.Select(e => new LeaderboardEntry((string)e.Element!, e.Score))];
    }

    public async Task<long?> RankAsync(string userId, CancellationToken ct) =>
        await Db.SortedSetRankAsync(Key, userId, Order.Descending);

    public async Task<double?> ScoreAsync(string userId, CancellationToken ct) =>
        await Db.SortedSetScoreAsync(Key, userId);

    public async Task<long> CountAsync(CancellationToken ct) =>
        await Db.SortedSetLengthAsync(Key);
}
