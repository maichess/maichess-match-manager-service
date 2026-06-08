using System.Globalization;
using Avro.Generic;

namespace MaichessMatchManagerService.Kafka;

// Pure transform: a user.events.v1 envelope -> the partial set of user:{id} hash
// fields it updates. Each event type contributes only the fields it carries, so the
// Redis upsert merges into the existing replica row rather than overwriting it:
//   UserRegistered -> username
//   ProfileUpdated -> username, dev_mode
//   RatingUpdated  -> rating, rating_deviation, volatility, elo, wins, losses, draws
// MatchResultRecorded and any unknown payload are not replica facts (the resulting
// rating arrives as a RatingUpdated), so they project to nothing. Stateless and
// deterministic; the consumer that drives it is the only impure part.
internal static class UserReplicaProjection
{
    internal static UserReplicaUpsert? Project(GenericRecord envelope) =>
        envelope["aggregate_id"] is string userId && userId.Length > 0
        && envelope["payload"] is GenericRecord payload
            ? Project(userId, payload)
            : null;

    private static UserReplicaUpsert? Project(string userId, GenericRecord payload)
    {
        return payload.Schema.Name switch
        {
            "UserRegistered" => new UserReplicaUpsert(userId, [Str(payload, "username")]),
            "ProfileUpdated" => new UserReplicaUpsert(
                userId,
                [Str(payload, "username"), Bool(payload, "dev_mode")]),
            "RatingUpdated" => new UserReplicaUpsert(
                userId,
                [
                    Dbl(payload, "rating"),
                    Dbl(payload, "rating_deviation"),
                    Dbl(payload, "volatility"),
                    Int(payload, "elo"),
                    Int(payload, "wins"),
                    Int(payload, "losses"),
                    Int(payload, "draws"),
                ]),
            _ => null,
        };
    }

    private static KeyValuePair<string, string> Str(GenericRecord r, string field) =>
        new(field, r[field] as string ?? string.Empty);

    private static KeyValuePair<string, string> Bool(GenericRecord r, string field) =>
        new(field, r[field] is true ? "true" : "false");

    private static KeyValuePair<string, string> Dbl(GenericRecord r, string field) =>
        new(field, Convert.ToDouble(r[field], CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));

    private static KeyValuePair<string, string> Int(GenericRecord r, string field) =>
        new(field, Convert.ToInt32(r[field], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
}
