using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Data;

internal interface IMatchRepository
{
    Task InsertAsync(MatchDocument match, CancellationToken ct);

    Task<MatchDocument?> GetByIdAsync(string id, CancellationToken ct);

    Task ReplaceAsync(MatchDocument match, CancellationToken ct);
}
