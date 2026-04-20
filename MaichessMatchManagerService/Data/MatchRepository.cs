using MaichessMatchManagerService.Entities;
using MongoDB.Driver;

namespace MaichessMatchManagerService.Data;

internal sealed class MatchRepository
{
    private readonly IMongoCollection<MatchDocument> _collection;

    internal MatchRepository(IMongoDatabase db)
    {
        _collection = db.GetCollection<MatchDocument>("matches");
    }

    internal async Task InsertAsync(MatchDocument match, CancellationToken ct) =>
        await _collection.InsertOneAsync(match, cancellationToken: ct);

    internal async Task<MatchDocument?> GetByIdAsync(string id, CancellationToken ct) =>
        await _collection.Find(m => m.Id == id).FirstOrDefaultAsync(ct);

    internal async Task ReplaceAsync(MatchDocument match, CancellationToken ct) =>
        await _collection.ReplaceOneAsync(m => m.Id == match.Id, match, cancellationToken: ct);
}
