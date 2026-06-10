using System.Diagnostics.CodeAnalysis;

namespace MaichessMatchManagerService.Kafka;

// The live per-match read model: the CQRS read side for an ongoing match, projected
// from match.events.v1 and held in Redis (behind ILiveMatchState). REST live reads
// serve ongoing matches from this projection; finished matches keep using the
// immutable finished-match cache / match-db path. Rebuildable by replaying the log
// (MatchProjection.Rebuild), so it is never a system of record.
//
// A pure data carrier — excluded from coverage like the other read-model records.
[ExcludeFromCodeCoverage]
internal sealed record LiveMatchState(
    string MatchId,
    string CurrentFen,
    string Status,
    long WhiteTimeMs,
    long BlackTimeMs,
    int MoveIndex,
    long LastMoveAtMs,
    long IncrementMs,
    IReadOnlyList<string> PositionHistory,
    PlayerRef White,
    PlayerRef Black,
    long Sequence,

    // The UCI move accepted into the pipeline but not yet applied — stashed from
    // MoveSubmitted because MoveValidated (which drives MoveApplied) does not carry
    // it. Null between moves. The only transient projector state in the read model.
    string? PendingMoveUci = null);
