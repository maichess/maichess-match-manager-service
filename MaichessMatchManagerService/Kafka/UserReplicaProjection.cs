using System.Globalization;
using Maichess.Events.V1;

namespace MaichessMatchManagerService.Kafka;

// Pure transform: a user.events.v1 UserEvent envelope -> the partial set of user:{id}
// hash fields it updates. Each event type contributes only the fields it carries, so the
// Redis upsert merges into the existing replica row rather than overwriting it:
//   UserRegistered -> username
//   ProfileUpdated -> username, dev_mode
//   RatingUpdated  -> rating, rating_deviation, volatility, elo, wins, losses, draws
// MatchResultRecorded and any unknown payload are not replica facts (the resulting
// rating arrives as a RatingUpdated), so they project to nothing. Stateless and
// deterministic; the consumer that drives it is the only impure part.
internal static class UserReplicaProjection
{
    internal static UserReplicaUpsert? Project(UserEvent envelope)
    {
        if (string.IsNullOrEmpty(envelope.AggregateId))
        {
            return null;
        }

        string userId = envelope.AggregateId;
        return envelope.PayloadCase switch
        {
            UserEvent.PayloadOneofCase.UserRegistered =>
                new UserReplicaUpsert(userId, [Str("username", envelope.UserRegistered.Username)]),
            UserEvent.PayloadOneofCase.ProfileUpdated =>
                new UserReplicaUpsert(
                    userId,
                    [
                        Str("username", envelope.ProfileUpdated.Username),
                        Bool("dev_mode", envelope.ProfileUpdated.DevMode),
                    ]),
            UserEvent.PayloadOneofCase.RatingUpdated =>
                new UserReplicaUpsert(
                    userId,
                    [
                        Dbl("rating", envelope.RatingUpdated.Rating),
                        Dbl("rating_deviation", envelope.RatingUpdated.RatingDeviation),
                        Dbl("volatility", envelope.RatingUpdated.Volatility),
                        Int("elo", envelope.RatingUpdated.Elo),
                        Int("wins", envelope.RatingUpdated.Wins),
                        Int("losses", envelope.RatingUpdated.Losses),
                        Int("draws", envelope.RatingUpdated.Draws),
                    ]),
            _ => null,
        };
    }

    // Protobuf string fields are never null (an unset field reads as ""), so the
    // value is stored as-is, mirroring the Bool/Dbl/Int helpers.
    private static KeyValuePair<string, string> Str(string field, string value) =>
        new(field, value);

    private static KeyValuePair<string, string> Bool(string field, bool value) =>
        new(field, value ? "true" : "false");

    private static KeyValuePair<string, string> Dbl(string field, double value) =>
        new(field, value.ToString("R", CultureInfo.InvariantCulture));

    private static KeyValuePair<string, string> Int(string field, int value) =>
        new(field, value.ToString(CultureInfo.InvariantCulture));
}
