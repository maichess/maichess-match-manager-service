using Maichess.Events.V1;

namespace MaichessMatchManagerService.Kafka;

// Pure transform: a cheat.events.v1 envelope -> the flagged bit it sets on the
// user:{id} replica hash. Only the durable verdicts touch the flag:
//   PlayerFlagged   -> flagged=true
//   PlayerUnflagged -> flagged=false
// LiveSuspicionRaised is an advisory in-game signal and MUST NOT set the
// persistent flag (anticheat contract), so it — and any unknown payload —
// projects to nothing. Stateless and deterministic, mirroring
// UserReplicaProjection; the consumer that drives it is the only impure part.
internal static class CheatFlagProjection
{
    internal static UserReplicaUpsert? Project(CheatEvent envelope) =>
        envelope.AggregateId.Length == 0
            ? null
            : envelope.PayloadCase switch
            {
                CheatEvent.PayloadOneofCase.PlayerFlagged =>
                    new UserReplicaUpsert(envelope.AggregateId, [new("flagged", "true")]),
                CheatEvent.PayloadOneofCase.PlayerUnflagged =>
                    new UserReplicaUpsert(envelope.AggregateId, [new("flagged", "false")]),
                _ => null,
            };
}
