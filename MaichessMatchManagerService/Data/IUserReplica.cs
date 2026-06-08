namespace MaichessMatchManagerService.Data;

// Read/write seam over the Redis-materialised user replica (user:{id}). Mirrors the
// IMatchCache/IMatchRepository adapter split: the Redis implementation is excluded
// from coverage (needs live Redis); the orchestration that reads it (replica-first
// with a GetUser fallback) is unit-tested against a mock.
internal interface IUserReplica
{
    // Returns the materialised row, or null on a cold miss (key not yet present).
    Task<UserReplicaRecord?> GetAsync(string userId, CancellationToken ct);

    // Merges the supplied fields into user:{id} (a partial hash upsert).
    Task UpsertAsync(string userId, IReadOnlyList<KeyValuePair<string, string>> fields, CancellationToken ct);
}
