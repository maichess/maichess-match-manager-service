using Maichess.Events.V1;
using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Kafka;

// Pure fold of match.events.v1 into the durable match-db document — the projector's
// write-through to the system-of-record store, run alongside the Redis read model.
// Where MatchProjection keeps only the volatile live fields, this materialises the
// full history a finished match keeps (moves, fen history, time format, attribution):
//   MatchCreated  -> the initial document (players, time format, clocks, start fen)
//   MoveValidated -> the opaque position_history blob owned by the validator
//   MoveApplied   -> append the move + resulting fen, advance clocks and last-move time
//   MatchEnded    -> terminal status + finished-at; clears position_history
// Mirrors the durable shape MatchService writes on the synchronous path, so the two
// produce identical documents until task 06 retires the synchronous writes. An event
// before MatchCreated (no document yet) is ignored, keeping replay from a partial log
// safe. The consumer loads the document, applies one event, and persists the result.
internal static class MatchHistoryProjection
{
    internal static MatchDocument? Apply(MatchDocument? doc, MatchEvent ev) =>
        ev.PayloadCase switch
        {
            MatchEvent.PayloadOneofCase.MatchCreated => Init(ev, ev.MatchCreated),
            MatchEvent.PayloadOneofCase.MoveValidated => doc is null ? null : WithHistory(doc, ev.MoveValidated),
            MatchEvent.PayloadOneofCase.MoveApplied => doc is null ? null : WithMove(doc, ev.MoveApplied),
            MatchEvent.PayloadOneofCase.MatchEnded => doc is null ? null : WithEnd(doc, ev.MatchEnded),
            _ => doc,
        };

    private static MatchDocument Init(MatchEvent ev, MatchCreated created) =>
        new()
        {
            Id = ev.AggregateId,
            White = ToPlayerDoc(created.White),
            Black = ToPlayerDoc(created.Black),
            CurrentFen = created.StartFen,
            Status = "ongoing",
            TimeFormat = new TimeFormatDocument
            {
                Id = created.TimeFormat.Id,
                BaseMs = created.TimeFormat.BaseMs,
                IncrementMs = created.TimeFormat.IncrementMs,
                Category = created.TimeFormat.Category,
            },
            WhiteTimeMs = created.TimeFormat.BaseMs,
            BlackTimeMs = created.TimeFormat.BaseMs,
            LastMoveAt = DateTimeOffset.FromUnixTimeMilliseconds(ev.OccurredAt),
            FenHistory = [created.StartFen],
            ClockHistory = [],
            CreatedBy = ToInitiator(created.CreatedBy),
            Source = created.Source == MatchSource.External ? "external" : "native",
            ExternalProvider = created.ExternalProvider,
            ExternalRef = created.ExternalRef,
        };

    private static MatchDocument WithHistory(MatchDocument doc, MoveValidated validated)
    {
        doc.PositionHistory = [.. validated.PositionHistory];
        return doc;
    }

    private static MatchDocument WithMove(MatchDocument doc, MoveApplied applied)
    {
        doc.CurrentFen = applied.ResultingFen;
        doc.Moves.Add(applied.MoveUci);
        doc.FenHistory.Add(applied.ResultingFen);
        doc.ClockHistory.Add(new ClockSnapshot(applied.WhiteTimeMs, applied.BlackTimeMs));
        doc.WhiteTimeMs = applied.WhiteTimeMs;
        doc.BlackTimeMs = applied.BlackTimeMs;
        doc.LastMoveAt = DateTimeOffset.FromUnixTimeMilliseconds(applied.AppliedAtMs);
        return doc;
    }

    private static MatchDocument WithEnd(MatchDocument doc, MatchEnded ended)
    {
        doc.Status = MatchProjection.StatusToString(ended.Status);
        doc.FinishedAtMs = ended.FinishedAtMs;
        doc.PositionHistory = [];
        return doc;
    }

    // created_by is absent for a bot-vs-bot game with no human initiator (and for any
    // event whose Player carries no identity), which maps to a null attribution.
    private static PlayerDocument? ToInitiator(Player? createdBy) =>
        createdBy is null || createdBy.IdentityCase == Player.IdentityOneofCase.None
            ? null
            : ToPlayerDoc(createdBy);

    private static PlayerDocument ToPlayerDoc(Player player) =>
        new()
        {
            UserId = player.IdentityCase == Player.IdentityOneofCase.UserId ? player.UserId : null,
            BotId = player.IdentityCase == Player.IdentityOneofCase.BotId ? player.BotId : null,
            ExternalName = player.IdentityCase == Player.IdentityOneofCase.ExternalName ? player.ExternalName : null,
        };
}
