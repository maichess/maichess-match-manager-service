namespace MaichessMatchManagerService.Kafka;

// The userId and the subset of user:{id} hash fields a single user.events record
// updates. An empty field list is never produced (the projection returns null
// instead), so a consumer can upsert without a presence check.
internal sealed record UserReplicaUpsert(string UserId, IReadOnlyList<KeyValuePair<string, string>> Fields);
