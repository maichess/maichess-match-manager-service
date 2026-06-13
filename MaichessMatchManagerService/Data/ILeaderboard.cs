namespace MaichessMatchManagerService.Data;

// Redis sorted-set leaderboard (leaderboard:rating) of userId -> Glicko-2 rating. The
// right structure for ranked-by-rating queries: top-N is ZREVRANGE and "your rank" is
// ZREVRANK, both O(log N) with no DB scan. Fed by the Stage 3 rating consumer
// (UserReplicaConsumer) — a single source of truth — and rebuildable by replaying
// user.events.v1. See caching-and-read-models.md (Part B).
internal interface ILeaderboard
{
    // Sets (ZADD) a user's rating score. Idempotent — replaying the topic re-applies it.
    Task UpsertAsync(string userId, double rating, CancellationToken ct);

    // The top `count` users by rating, highest first (ZREVRANGE WITHSCORES).
    Task<IReadOnlyList<LeaderboardEntry>> TopAsync(int count, CancellationToken ct);

    // The user's 0-based rank from the top (ZREVRANK), or null if not on the board.
    Task<long?> RankAsync(string userId, CancellationToken ct);

    // The user's rating score (ZSCORE), or null if not on the board.
    Task<double?> ScoreAsync(string userId, CancellationToken ct);

    // Total number of users on the board (ZCARD).
    Task<long> CountAsync(CancellationToken ct);
}
