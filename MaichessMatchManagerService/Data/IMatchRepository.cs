using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Data;

internal interface IMatchRepository
{
    Task<MatchDocument> InsertAsync(MatchDocument match, CancellationToken ct);

    Task<MatchDocument?> GetByIdAsync(string id, CancellationToken ct);

    Task ReplaceAsync(MatchDocument match, CancellationToken ct);

    Task<IReadOnlyList<MatchDocument>> FindOngoingAsync(CancellationToken ct);

    // Returns candidate matches a user took part in or initiated (white, black,
    // or created_by). The service applies the authoritative membership and
    // status filtering, ordering, and paging.
    Task<IReadOnlyList<MatchDocument>> FindForUserAsync(string userId, CancellationToken ct);

    // Returns the candidate set for a global match search. When a participant or
    // initiator id is supplied the lookups are scoped to it (an equality push-down
    // the generic filter supports); with neither, the whole collection is the
    // candidate set. The service applies the authoritative membership, status,
    // source, time-range filtering, ordering, and paging on top.
    Task<IReadOnlyList<MatchDocument>> SearchAsync(
        string? playerId,
        string? initiatorId,
        CancellationToken ct);

    Task<(IReadOnlyList<MatchDocument> Matches, int Total)> ListAsync(
        string status,
        string? category,
        int page,
        int pageSize,
        CancellationToken ct);
}
