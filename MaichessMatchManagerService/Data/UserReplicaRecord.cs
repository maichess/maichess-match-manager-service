namespace MaichessMatchManagerService.Data;

// A user read-model row materialised from the compacted user.events.v1 topic into
// shared Redis (user:{id}). Replaces the hot GetUser RPC for username + rating
// enrichment. Every field is nullable so a partially-materialised user (e.g. only a
// UserRegistered snapshot has arrived, but no RatingUpdated yet) is distinguishable
// from a genuine zero — callers fall back to GetUser when the field they need is
// absent. Rebuildable by replaying the compacted topic.
internal sealed record UserReplicaRecord(
    string? Username,
    double? Rating,
    double? RatingDeviation,
    double? Volatility,
    int? Elo,
    int? Wins,
    int? Losses,
    int? Draws,
    bool? DevMode,
    bool? Flagged);
