using MaichessMatchManagerService.Kafka;

namespace MaichessMatchManagerService.Data;

// Read/write seam over the Redis-held live match read model (match:live:{id}). The
// projector writes the projection here; REST live reads overlay its volatile fields
// (fen/clocks/last-move time) onto the durable match-db doc for ongoing matches.
// Mirrors the IMatchCache/IUserReplica split: the Redis implementation is excluded
// from coverage (needs live Redis); the projector and the read overlay that consume
// this are unit-tested against a mock.
internal interface ILiveMatchState
{
    // The projected state for a match, or null when nothing has been projected yet
    // (cold model / the match's genesis events have not been consumed).
    Task<LiveMatchState?> GetAsync(string matchId, CancellationToken ct);

    // Persists the projection (the full per-match read-model blob).
    Task SetAsync(LiveMatchState state, CancellationToken ct);
}
