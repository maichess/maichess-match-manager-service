namespace MaichessMatchManagerService.Data;

// One member of the leaderboard:rating sorted set: a user and their Glicko-2 rating
// (the ZSET score). Read back from ZREVRANGE/ZSCORE; enrichment (username, elo,
// provisional, flagged) is layered on from the user replica by LeaderboardService.
internal sealed record LeaderboardEntry(string UserId, double Rating);
