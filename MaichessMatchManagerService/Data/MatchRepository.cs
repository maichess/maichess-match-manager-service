using MaichessMatchManagerService.Entities;
using MongoDB.Driver;

namespace MaichessMatchManagerService.Data;

internal sealed class MatchRepository
{
    private readonly IMongoCollection<MatchDocument> collection;

    public MatchRepository(IMongoDatabase db)
    {
        collection = db.GetCollection<MatchDocument>("matches");
    }

    internal async Task InsertAsync(MatchDocument match, CancellationToken ct) =>
        await collection.InsertOneAsync(match, cancellationToken: ct);

    internal async Task<MatchDocument?> GetByIdAsync(string id, CancellationToken ct) =>
        await collection.Find(m => m.Id == id).FirstOrDefaultAsync(ct);

    internal async Task ReplaceAsync(MatchDocument match, CancellationToken ct) =>
        await collection.ReplaceOneAsync(m => m.Id == match.Id, match, cancellationToken: ct);
}
