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
    string? PendingMoveUci = null,

    // The user id of an outstanding draw offerer, or null when no offer is pending.
    // Tracked from DrawOffered/DrawDeclined so the command side can validate accept/
    // decline against the read model (mirrors MatchDocument.PendingDrawOffererUserId
    // on the retired synchronous path).
    string? PendingDrawOffererUserId = null,

    // Participant/source snapshot stamped onto every MatchEnded for the rating
    // consumer (kafka task 08): the match source and, per bot side, the
    // engine-configured elo resolved at creation. Null elo for human sides and
    // for matches created before the snapshot existed. Defaults keep states
    // persisted before these fields deserializing safely.
    string Source = "native",
    double? WhiteBotElo = null,
    double? BlackBotElo = null);
