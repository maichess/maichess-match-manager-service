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

    Task<(IReadOnlyList<MatchDocument> Matches, int Total)> ListAsync(
        string status,
        string? category,
        int page,
        int pageSize,
        CancellationToken ct);
}
