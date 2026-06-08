using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Data;

// Redis-backed cache for immutable finished-match reads. Holds ended match
// documents (`match:{id}`) and ListUserMatches page results
// (`matches:user:{userId}:{statusFilter}:{page}:{pageSize}`). Every key is
// rebuildable from match-db, so entries carry no expiry and rely on LRU
// eviction; correctness comes from event-driven invalidation when a match ends,
// never from TTLs. Only ended (immutable) data is ever cached — ongoing matches
// are the live read model's job.
internal interface IMatchCache
{
    // Returns the cached finished-match document, or null on a miss.
    Task<MatchDocument?> GetMatchAsync(string matchId, CancellationToken ct);

    // Stores a finished-match document with no expiry. Callers must only cache
    // matches in an ended status.
    Task SetMatchAsync(MatchDocument match, CancellationToken ct);

    // Returns a cached page of a user's matches, or null on a miss.
    Task<(IReadOnlyList<MatchDocument> Matches, int Total)?> GetUserPageAsync(
        string userId, string statusFilter, int page, int pageSize, CancellationToken ct);

    // Stores a page of a user's matches with no expiry.
    Task SetUserPageAsync(
        string userId,
        string statusFilter,
        int page,
        int pageSize,
        IReadOnlyList<MatchDocument> matches,
        int total,
        CancellationToken ct);

    // Evicts every cached page for a user (all status filters, pages, and sizes),
    // so a newly-finished match reappears on the next read.
    Task InvalidateUserPagesAsync(string userId, CancellationToken ct);
}
