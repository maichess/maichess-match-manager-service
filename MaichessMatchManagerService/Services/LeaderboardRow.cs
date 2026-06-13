namespace MaichessMatchManagerService.Services;

// An enriched leaderboard entry: the ZSET rating joined with the user replica's
// username/elo/deviation and a provisional flag. Rank is the 1-based ZSET position
// (ZREVRANK + 1), so it stays consistent between the top-N list and the "your rank"
// query even when flagged players are hidden from the list (leaving rank gaps).
internal sealed record LeaderboardRow(
    int Rank,
    string UserId,
    string? Username,
    int Elo,
    double RatingDeviation,
    bool Provisional);
