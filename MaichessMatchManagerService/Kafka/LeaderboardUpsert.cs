namespace MaichessMatchManagerService.Kafka;

// The leaderboard mutation a single user.events.v1 record implies: set this user's
// rating score. Produced by LeaderboardProjection only for RatingUpdated events.
internal sealed record LeaderboardUpsert(string UserId, double Rating);
