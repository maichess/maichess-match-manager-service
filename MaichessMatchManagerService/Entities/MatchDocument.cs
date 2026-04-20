using MongoDB.Bson.Serialization.Attributes;

namespace MaichessMatchManagerService.Entities;

internal sealed class MatchDocument
{
    [BsonId]
    public required string Id { get; set; }

    public required PlayerDocument White { get; set; }

    public required PlayerDocument Black { get; set; }

    public required string CurrentFen { get; set; }

    public required string Status { get; set; }

    public List<string> Moves { get; set; } = [];

    // FenHistory[0] is the starting position; FenHistory[N] is the position after move N.
    // Always has Moves.Count + 1 entries.
    public List<string> FenHistory { get; set; } = [];

    public required string TimeControl { get; set; }

    public long WhiteTimeMs { get; set; }

    public long BlackTimeMs { get; set; }

    // Timestamp of when the last move was made; used to compute elapsed clock time.
    public DateTimeOffset LastMoveAt { get; set; }
}
