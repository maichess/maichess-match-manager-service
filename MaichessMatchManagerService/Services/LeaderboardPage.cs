namespace MaichessMatchManagerService.Services;

// A page of the leaderboard: the visible (non-flagged) rows plus the total number of
// rated players on the board (ZCARD), which is independent of how many rows are shown.
internal sealed record LeaderboardPage(IReadOnlyList<LeaderboardRow> Rows, long Total);
