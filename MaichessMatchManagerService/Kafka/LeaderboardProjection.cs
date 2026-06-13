using Maichess.Events.V1;

namespace MaichessMatchManagerService.Kafka;

// Pure transform: a user.events.v1 UserEvent envelope -> the leaderboard score it
// updates. Only RatingUpdated carries a rating, so every other payload (registration,
// profile change, MatchResultRecorded, unknown) projects to nothing — the rating it
// produces arrives as its own RatingUpdated. Shares the Stage 3 consumer with
// UserReplicaProjection so there is a single source of truth for the rating fact.
internal static class LeaderboardProjection
{
    internal static LeaderboardUpsert? Project(UserEvent envelope) =>
        !string.IsNullOrEmpty(envelope.AggregateId)
        && envelope.PayloadCase == UserEvent.PayloadOneofCase.RatingUpdated
            ? new LeaderboardUpsert(envelope.AggregateId, envelope.RatingUpdated.Rating)
            : null;
}
