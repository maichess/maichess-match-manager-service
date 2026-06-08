using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using StackExchange.Redis;

namespace MaichessMatchManagerService.Data;

// Redis-backed user replica. The compacted user.events.v1 topic is materialised into
// one hash per user at user:{id}; this is the shared read model the match-end rating
// enrichment and username resolution consult before falling back to GetUser. No
// expiry: the row is mutable, event-maintained, and rebuildable from the topic, so it
// must survive allkeys-lru pressure differently from the immutable match caches — see
// caching-and-read-models.md. Excluded from coverage (requires live Redis).
[ExcludeFromCodeCoverage]
internal sealed class RedisUserReplica(IConnectionMultiplexer redis) : IUserReplica
{
    private IDatabase Db => redis.GetDatabase();

    public async Task<UserReplicaRecord?> GetAsync(string userId, CancellationToken ct)
    {
        HashEntry[] fields = await Db.HashGetAllAsync(Key(userId));
        if (fields.Length == 0)
        {
            return null;
        }

        Dictionary<string, string> map = fields.ToDictionary(f => (string)f.Name!, f => (string)f.Value!);
        return new UserReplicaRecord(
            map.GetValueOrDefault("username"),
            Dbl(map, "rating"),
            Dbl(map, "rating_deviation"),
            Dbl(map, "volatility"),
            Int(map, "elo"),
            Int(map, "wins"),
            Int(map, "losses"),
            Int(map, "draws"),
            Bool(map, "dev_mode"),
            Bool(map, "flagged"));
    }

    public async Task UpsertAsync(
        string userId, IReadOnlyList<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        HashEntry[] entries = [.. fields.Select(f => new HashEntry(f.Key, f.Value))];
        await Db.HashSetAsync(Key(userId), entries);
    }

    private static string Key(string userId) => $"user:{userId}";

    private static double? Dbl(Dictionary<string, string> map, string field) =>
        map.TryGetValue(field, out string? v)
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : null;

    private static int? Int(Dictionary<string, string> map, string field) =>
        map.TryGetValue(field, out string? v)
        && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
            ? i
            : null;

    private static bool? Bool(Dictionary<string, string> map, string field) =>
        map.TryGetValue(field, out string? v) ? v == "true" : null;
}
