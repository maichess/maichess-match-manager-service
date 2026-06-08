using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using MaichessMatchManagerService.Entities;
using StackExchange.Redis;

namespace MaichessMatchManagerService.Data;

// StackExchange.Redis implementation of the finished-match cache. Documents and
// page results are stored as JSON strings with no expiry (LRU eviction only);
// every key is rebuildable from match-db on a miss. Excluded from coverage like
// MatchRepository: it requires a live Redis and is exercised through the service
// layer against a mocked IMatchCache.
[ExcludeFromCodeCoverage]
internal sealed class RedisMatchCache(IConnectionMultiplexer redis) : IMatchCache
{
    private IDatabase Db => redis.GetDatabase();

    public async Task<MatchDocument?> GetMatchAsync(string matchId, CancellationToken ct)
    {
        RedisValue value = await Db.StringGetAsync(MatchKey(matchId));
        return value.HasValue ? JsonSerializer.Deserialize<MatchDocument>((string)value!) : null;
    }

    public async Task SetMatchAsync(MatchDocument match, CancellationToken ct) =>
        await Db.StringSetAsync(MatchKey(match.Id), JsonSerializer.Serialize(match));

    public async Task<(IReadOnlyList<MatchDocument> Matches, int Total)?> GetUserPageAsync(
        string userId, string statusFilter, int page, int pageSize, CancellationToken ct)
    {
        RedisValue value = await Db.StringGetAsync(PageKey(userId, statusFilter, page, pageSize));
        if (!value.HasValue)
        {
            return null;
        }

        CachedPage? cached = JsonSerializer.Deserialize<CachedPage>((string)value!);
        return cached is null ? null : (cached.Matches, cached.Total);
    }

    public async Task SetUserPageAsync(
        string userId,
        string statusFilter,
        int page,
        int pageSize,
        IReadOnlyList<MatchDocument> matches,
        int total,
        CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(new CachedPage([.. matches], total));
        await Db.StringSetAsync(PageKey(userId, statusFilter, page, pageSize), json);
    }

    public async Task InvalidateUserPagesAsync(string userId, CancellationToken ct)
    {
        // SCAN every page key for the user rather than tracking an index set: with
        // allkeys-lru eviction an index could be evicted independently of the page
        // keys it points at, leaking stale entries. The per-user pattern keeps the
        // scanned keyspace small.
        RedisValue pattern = $"matches:user:{userId}:*";
        foreach (EndPoint endpoint in redis.GetEndPoints())
        {
            IServer server = redis.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            await foreach (RedisKey key in server.KeysAsync(pattern: pattern).WithCancellation(ct))
            {
                await Db.KeyDeleteAsync(key);
            }
        }
    }

    private static string MatchKey(string matchId) => $"match:{matchId}";

    private static string PageKey(string userId, string statusFilter, int page, int pageSize) =>
        $"matches:user:{userId}:{statusFilter}:{page}:{pageSize}";

    private sealed record CachedPage(List<MatchDocument> Matches, int Total);
}
