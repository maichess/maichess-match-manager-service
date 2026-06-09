using Maichess.Events.V1;

namespace MaichessMatchManagerService.Kafka;

// Pure fold of the match.events.v1 log into the LiveMatchState read model. Each
// durable fact updates only the fields it owns, so the projection is the
// deterministic replay of the log:
//   MatchCreated  -> initial state (players, clocks = base_ms, start fen, increment)
//   MoveValidated -> the opaque position_history blob (the only place it rides)
//   MoveApplied   -> current fen, authoritative clocks, move index, last-move time
//   MatchEnded    -> terminal status; clears position_history
// The transient pipeline payloads (MoveSubmitted/MoveRejected/BotMove*/Draw*) carry
// nothing the read model reconstructs, so they leave the state unchanged. Driving an
// event before MatchCreated (no state yet) is ignored, keeping replay from a partial
// log safe. The consumer that calls this and writes Redis is the only impure part.
internal static class MatchProjection
{
    internal static LiveMatchState? Apply(LiveMatchState? state, MatchEvent ev) =>
        ev.PayloadCase switch
        {
            MatchEvent.PayloadOneofCase.MatchCreated => Init(ev, ev.MatchCreated),
            MatchEvent.PayloadOneofCase.MoveValidated => state is null ? null : WithHistory(state, ev),
            MatchEvent.PayloadOneofCase.MoveApplied => state is null ? null : WithMove(state, ev, ev.MoveApplied),
            MatchEvent.PayloadOneofCase.MatchEnded => state is null ? null : WithEnd(state, ev, ev.MatchEnded),
            _ => state,
        };

    // Reconstructs the read model from a (partial or full) ordered log slice.
    internal static LiveMatchState? Rebuild(IEnumerable<MatchEvent> log) =>
        log.Aggregate((LiveMatchState?)null, Apply);

    internal static string StatusToString(MatchStatus status) => status switch
    {
        MatchStatus.Ongoing => "ongoing",
        MatchStatus.WhiteWon => "white_won",
        MatchStatus.BlackWon => "black_won",
        MatchStatus.Draw => "draw",
        _ => "ongoing",
    };

    private static LiveMatchState Init(MatchEvent ev, MatchCreated created) =>
        new(
            MatchId: ev.AggregateId,
            CurrentFen: created.StartFen,
            Status: "ongoing",
            WhiteTimeMs: created.TimeFormat.BaseMs,
            BlackTimeMs: created.TimeFormat.BaseMs,
            MoveIndex: 0,
            LastMoveAtMs: ev.OccurredAt,
            IncrementMs: created.TimeFormat.IncrementMs,
            PositionHistory: [],
            White: ToPlayerRef(created.White),
            Black: ToPlayerRef(created.Black),
            Sequence: ev.Sequence);

    private static LiveMatchState WithHistory(LiveMatchState state, MatchEvent ev) =>
        state with
        {
            PositionHistory = [.. ev.MoveValidated.PositionHistory],
            Sequence = ev.Sequence,
        };

    private static LiveMatchState WithMove(LiveMatchState state, MatchEvent ev, MoveApplied applied) =>
        state with
        {
            CurrentFen = applied.ResultingFen,
            WhiteTimeMs = applied.WhiteTimeMs,
            BlackTimeMs = applied.BlackTimeMs,
            MoveIndex = applied.Index,
            LastMoveAtMs = applied.AppliedAtMs,
            Sequence = ev.Sequence,
        };

    private static LiveMatchState WithEnd(LiveMatchState state, MatchEvent ev, MatchEnded ended) =>
        state with
        {
            Status = StatusToString(ended.Status),
            PositionHistory = [],
            Sequence = ev.Sequence,
        };

    private static PlayerRef ToPlayerRef(Player player) =>
        new(
            player.IdentityCase == Player.IdentityOneofCase.UserId ? player.UserId : null,
            player.IdentityCase == Player.IdentityOneofCase.BotId ? player.BotId : null);
}
