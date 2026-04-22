using MaichessMatchManagerService.Entities;
using MongoDB.Driver;

namespace MaichessMatchManagerService.Data;

internal sealed class MatchRepository : IMatchRepository
{
    private readonly IMongoCollection<MatchDocument> collection;

    public MatchRepository(IMongoDatabase db)
    {
        collection = db.GetCollection<MatchDocument>("matches");
    }

    public async Task InsertAsync(MatchDocument match, CancellationToken ct) =>
        await collection.InsertOneAsync(match, cancellationToken: ct);

    public async Task<MatchDocument?> GetByIdAsync(string id, CancellationToken ct) =>
        await collection.Find(m => m.Id == id).FirstOrDefaultAsync(ct);

    public async Task ReplaceAsync(MatchDocument match, CancellationToken ct) =>
        await collection.ReplaceOneAsync(m => m.Id == match.Id, match, cancellationToken: ct);
}
