using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MaichessMatchManagerService.Kafka;
using StackExchange.Redis;

namespace MaichessMatchManagerService.Data;

// StackExchange.Redis implementation of the live match read model. The per-match
// projection is stored as a JSON blob at match:live:{id} with no expiry — it is the
// CQRS read side for ongoing matches, maintained by the projector and rebuildable by
// replaying match.events.v1 (MatchProjection.Rebuild), so it never needs to survive
// as a system of record. Excluded from coverage like RedisMatchCache/RedisUserReplica:
// it requires a live Redis and is exercised through the seam against a mock.
[ExcludeFromCodeCoverage]
internal sealed class RedisLiveMatchState(IConnectionMultiplexer redis) : ILiveMatchState
{
    private IDatabase Db => redis.GetDatabase();

    public async Task<LiveMatchState?> GetAsync(string matchId, CancellationToken ct)
    {
        RedisValue value = await Db.StringGetAsync(Key(matchId));
        return value.HasValue ? JsonSerializer.Deserialize<LiveMatchState>((string)value!) : null;
    }

    public async Task SetAsync(LiveMatchState state, CancellationToken ct) =>
        await Db.StringSetAsync(Key(state.MatchId), JsonSerializer.Serialize(state));

    private static string Key(string matchId) => $"match:live:{matchId}";
}
