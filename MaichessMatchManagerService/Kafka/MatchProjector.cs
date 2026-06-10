using System.Text.Json;
using Maichess.Events.V1;

namespace MaichessMatchManagerService.Kafka;

// Pure decision logic for the match-manager projector: given the current live
// read-model state, one consumed match.events.v1 record, the wall-clock now, and an
// id minter, it returns the events to produce back to match.events.v1, the socket
// pushes to produce to socket.outbound.v1, and the new read-model state. The
// consumer that calls this owns the Kafka transaction and the Redis/match-db writes.
//
// The move loop turned into events:
//   MatchCreated       -> if the side to move is a bot, BotMoveRequested (first move)
//   MoveValidated      -> MoveApplied (+ socket move_made); then a terminal game_result
//                         or a flagged clock -> MatchEnded (+ socket match_ended),
//                         else a bot to move -> BotMoveRequested
//   BotMoveCalculated  -> MoveSubmitted (re-enters the validator loop)
// Everything else (MoveSubmitted/MoveRejected/BotMoveRequested/Draw*/the projector's
// own MoveApplied/MatchEnded riding the same topic) only folds into the read model.
//
// Clock math mirrors MatchService (ApplyGameResult / ApplyIncrement / GetActiveColor):
// the active side's clock is decremented by the elapsed time, a terminal result or a
// flagged clock ends the game, and the increment is credited only when the move
// leaves the game ongoing. The duplication with MatchService is deliberate and noted
// in CONTRACT_NOTES — the synchronous path is retired when task 06 cuts the write side
// over to this projector.
internal static class MatchProjector
{
    private const string Producer = "match-manager-service";

    internal static ProjectorOutcome Decide(
        LiveMatchState? state, MatchEvent consumed, long nowMs, Func<string> newId)
    {
        // Dedupe: an event whose sequence does not advance past the read model has
        // already been applied (and emitted from). Re-delivery — including the
        // projector's own MoveApplied/MatchEnded, which ride match.events.v1 — is a
        // no-op for the read side. The durable write-through is idempotent and runs
        // outside this decision.
        return state is not null && consumed.Sequence <= state.Sequence
            ? new ProjectorOutcome(state, [], [])
            : consumed.PayloadCase switch
            {
                MatchEvent.PayloadOneofCase.MatchCreated => OnCreated(consumed, nowMs, newId),
                MatchEvent.PayloadOneofCase.MoveValidated when state is not null =>
                    OnValidated(state, consumed, nowMs, newId),
                MatchEvent.PayloadOneofCase.BotMoveCalculated when state is not null =>
                    OnBotCalculated(state, consumed, nowMs, newId),
                MatchEvent.PayloadOneofCase.MatchEnded when state is not null =>
                    OnEnded(state, consumed, newId),
                MatchEvent.PayloadOneofCase.DrawOffered when state is not null =>
                    OnDrawOffered(state, consumed, newId),
                MatchEvent.PayloadOneofCase.DrawDeclined when state is not null =>
                    OnDrawDeclined(state, consumed, newId),
                _ => new ProjectorOutcome(MatchProjection.Apply(state, consumed), [], []),
            };
    }

    // A command-originated MatchEnded (resign / accept-draw / timeout) arrives on the
    // topic and is applied here: fold the terminal status and push match_ended. The
    // projector's own MatchEnded (from OnValidated) is deduped before reaching this case
    // (its sequence does not advance past the read model), so the push fires exactly once.
    private static ProjectorOutcome OnEnded(LiveMatchState state, MatchEvent consumed, Func<string> newId)
    {
        LiveMatchState next = MatchProjection.Apply(state, consumed)!;
        string status = MatchProjection.StatusToString(consumed.MatchEnded.Status);
        OutboundEvent push = MatchEndedPush(consumed, newId(), consumed.OccurredAt, status, consumed.MatchEnded.EndReason);
        return new ProjectorOutcome(next, [], [push]);
    }

    private static ProjectorOutcome OnDrawOffered(LiveMatchState state, MatchEvent consumed, Func<string> newId)
    {
        LiveMatchState next = MatchProjection.Apply(state, consumed)!;
        OutboundEvent push = DrawPush(consumed, newId(), "draw_offered", consumed.DrawOffered.By);
        return new ProjectorOutcome(next, [], [push]);
    }

    private static ProjectorOutcome OnDrawDeclined(LiveMatchState state, MatchEvent consumed, Func<string> newId)
    {
        LiveMatchState next = MatchProjection.Apply(state, consumed)!;
        OutboundEvent push = DrawPush(consumed, newId(), "draw_declined", consumed.DrawDeclined.By);
        return new ProjectorOutcome(next, [], [push]);
    }

    private static ProjectorOutcome OnCreated(MatchEvent consumed, long nowMs, Func<string> newId)
    {
        LiveMatchState state = MatchProjection.Apply(null, consumed)!;
        char toMove = ActiveColor(state.CurrentFen);
        PlayerRef mover = toMove == 'w' ? state.White : state.Black;

        if (mover.BotId is null)
        {
            return new ProjectorOutcome(state, [], []);
        }

        MatchEvent requested = BotRequest(
            consumed,
            nowMs,
            newId,
            state.CurrentFen,
            mover.BotId,
            toMove == 'w' ? state.WhiteTimeMs : state.BlackTimeMs,
            consumed.Sequence + 1);
        return new ProjectorOutcome(state, [requested], []);
    }

    private static ProjectorOutcome OnValidated(
        LiveMatchState state, MatchEvent consumed, long nowMs, Func<string> newId)
    {
        MoveValidated validated = consumed.MoveValidated;
        char mover = ActiveColor(state.CurrentFen);

        long elapsed = nowMs - state.LastMoveAtMs;
        long white = state.WhiteTimeMs;
        long black = state.BlackTimeMs;
        if (mover == 'w')
        {
            white = Math.Max(0, white - elapsed);
        }
        else
        {
            black = Math.Max(0, black - elapsed);
        }

        string status = ResolveStatus(white, black, validated.GameResult);
        bool ongoing = status == "ongoing";
        if (ongoing && state.IncrementMs > 0)
        {
            if (mover == 'w')
            {
                white += state.IncrementMs;
            }
            else
            {
                black += state.IncrementMs;
            }
        }

        MatchEvent applied = Envelope(consumed, newId(), consumed.Sequence + 1, "match.MoveApplied", nowMs);
        applied.MoveApplied = new MoveApplied
        {
            MoveUci = state.PendingMoveUci ?? string.Empty,
            ResultingFen = validated.ResultingFen,
            Index = state.MoveIndex + 1,
            Player = ToPlayer(mover == 'w' ? state.White : state.Black),
            WhiteTimeMs = white,
            BlackTimeMs = black,
            AppliedAtMs = nowMs,
        };

        List<MatchEvent> events = [applied];
        List<OutboundEvent> pushes = [MoveMade(consumed, newId(), nowMs, applied.MoveApplied)];
        LiveMatchState next = MatchProjection.Apply(MatchProjection.Apply(state, consumed)!, applied)!;

        if (!ongoing)
        {
            EndReason reason = ResolveEndReason(white, black, validated.GameResult);
            MatchEvent ended = Envelope(consumed, newId(), consumed.Sequence + 2, "match.MatchEnded", nowMs);
            ended.MatchEnded = MatchEndedFactory.Create(state, ToStatus(status), reason, nowMs);
            events.Add(ended);
            pushes.Add(MatchEndedPush(consumed, newId(), nowMs, status, reason));
            return new ProjectorOutcome(MatchProjection.Apply(next, ended)!, events, pushes);
        }

        char toMove = ActiveColor(validated.ResultingFen);
        PlayerRef nextMover = toMove == 'w' ? state.White : state.Black;
        if (nextMover.BotId is not null)
        {
            events.Add(BotRequest(
                consumed,
                nowMs,
                newId,
                validated.ResultingFen,
                nextMover.BotId,
                toMove == 'w' ? white : black,
                consumed.Sequence + 2));
        }

        return new ProjectorOutcome(next, events, pushes);
    }

    private static ProjectorOutcome OnBotCalculated(
        LiveMatchState state, MatchEvent consumed, long nowMs, Func<string> newId)
    {
        char toMove = ActiveColor(state.CurrentFen);
        PlayerRef bot = toMove == 'w' ? state.White : state.Black;

        // Defensive: a calculated move for a side that is not (or is no longer) a bot
        // is a stale/duplicate reply — drop it rather than submit on a human's behalf.
        if (bot.BotId is null)
        {
            return new ProjectorOutcome(state, [], []);
        }

        MatchEvent submitted = Envelope(consumed, newId(), consumed.Sequence + 1, "match.MoveSubmitted", nowMs);
        submitted.MoveSubmitted = new MoveSubmitted
        {
            MoveUci = consumed.BotMoveCalculated.MoveUci,
            By = ToPlayer(bot),
            Fen = state.CurrentFen,
        };
        submitted.MoveSubmitted.PositionHistory.AddRange(state.PositionHistory);

        return new ProjectorOutcome(MatchProjection.Apply(state, submitted)!, [submitted], []);
    }

    private static MatchEvent BotRequest(
        MatchEvent source, long nowMs, Func<string> newId, string fen, string botId, long timeLimitMs, long sequence)
    {
        MatchEvent requested = Envelope(source, newId(), sequence, "match.BotMoveRequested", nowMs);
        requested.BotMoveRequested = new BotMoveRequested
        {
            Fen = fen,
            BotId = botId,
            TimeLimitMs = (int)timeLimitMs,
            RequestId = newId(),
        };
        return requested;
    }

    // Mirrors MatchService.ApplyGameResult: a flagged clock decides first, then the
    // validator's game_result; anything non-terminal leaves the game ongoing.
    private static string ResolveStatus(long white, long black, GameResult gameResult) =>
        white <= 0 ? "black_won"
        : black <= 0 ? "white_won"
        : gameResult switch
        {
            GameResult.WhiteWon => "white_won",
            GameResult.BlackWon => "black_won",
            GameResult.Stalemate
                or GameResult.FiftyMoveRule
                or GameResult.ThreefoldRepetition
                or GameResult.InsufficientMaterial => "draw",
            _ => "ongoing",
        };

    // A flagged clock is a timeout regardless of the board result; otherwise the
    // reason follows the game_result (checkmate is the default for a decisive board).
    private static EndReason ResolveEndReason(long white, long black, GameResult gameResult) =>
        white <= 0 || black <= 0
            ? EndReason.Timeout
            : gameResult switch
            {
                GameResult.Stalemate => EndReason.Stalemate,
                GameResult.FiftyMoveRule => EndReason.FiftyMoveRule,
                GameResult.ThreefoldRepetition => EndReason.ThreefoldRepetition,
                GameResult.InsufficientMaterial => EndReason.InsufficientMaterial,
                _ => EndReason.Checkmate,
            };

    // Only ever called for a terminal status (when building MatchEnded), so the three
    // decisive cases are exhaustive — a drawn result is the natural default.
    private static MatchStatus ToStatus(string status) => status switch
    {
        "white_won" => MatchStatus.WhiteWon,
        "black_won" => MatchStatus.BlackWon,
        _ => MatchStatus.Draw,
    };

    // Maps the reasons ResolveEndReason can produce to the socket payload's string;
    // a decisive board result (EndReason.Checkmate) is the default.
    private static string EndReasonToString(EndReason reason) => reason switch
    {
        EndReason.Timeout => "timeout",
        EndReason.Resignation => "resignation",
        EndReason.DrawAgreement => "draw_agreement",
        EndReason.Stalemate => "stalemate",
        EndReason.FiftyMoveRule => "fifty_move_rule",
        EndReason.ThreefoldRepetition => "threefold_repetition",
        EndReason.InsufficientMaterial => "insufficient_material",
        _ => "checkmate",
    };

    private static OutboundEvent MoveMade(MatchEvent source, string eventId, long nowMs, MoveApplied applied)
    {
        Dictionary<string, object?> payload = new()
        {
            ["match_id"] = source.AggregateId,
            ["move"] = applied.MoveUci,
            ["resulting_fen"] = applied.ResultingFen,
            ["index"] = applied.Index,
            ["player"] = PlayerJson(applied.Player),
            ["white_time_ms"] = applied.WhiteTimeMs,
            ["black_time_ms"] = applied.BlackTimeMs,
        };
        return Push(source, eventId, nowMs, "move_made", payload);
    }

    private static OutboundEvent MatchEndedPush(
        MatchEvent source, string eventId, long nowMs, string status, EndReason reason)
    {
        Dictionary<string, object?> payload = new()
        {
            ["match_id"] = source.AggregateId,
            ["status"] = status,
            ["reason"] = EndReasonToString(reason),
        };
        return Push(source, eventId, nowMs, "match_ended", payload);
    }

    private static OutboundEvent DrawPush(MatchEvent source, string eventId, string eventName, Player by)
    {
        Dictionary<string, object?> payload = new()
        {
            ["match_id"] = source.AggregateId,
            ["player"] = PlayerJson(by),
        };
        return Push(source, eventId, source.OccurredAt, eventName, payload);
    }

    private static OutboundEvent Push(
        MatchEvent source, string eventId, long nowMs, string eventName, Dictionary<string, object?> payload) =>
        new()
        {
            EventId = eventId,
            EventType = $"socket.{eventName}",
            AggregateId = source.AggregateId,
            Sequence = 0L,
            OccurredAt = nowMs,
            CorrelationId = source.CorrelationId,
            CausationId = source.EventId,
            Producer = Producer,
            Push = new SocketPush
            {
                TargetMatchId = source.AggregateId,
                EventName = eventName,
                PayloadJson = JsonSerializer.Serialize(payload),
            },
        };

    private static Dictionary<string, string> PlayerJson(Player player) => player.IdentityCase switch
    {
        Player.IdentityOneofCase.UserId => new Dictionary<string, string> { ["user_id"] = player.UserId },
        Player.IdentityOneofCase.BotId => new Dictionary<string, string> { ["bot_id"] = player.BotId },
        _ => [],
    };

    private static Player ToPlayer(PlayerRef player) =>
        player.UserId is not null ? new Player { UserId = player.UserId }
        : player.BotId is not null ? new Player { BotId = player.BotId }
        : new Player();

    private static MatchEvent Envelope(
        MatchEvent source, string eventId, long sequence, string eventType, long occurredAt) =>
        new()
        {
            EventId = eventId,
            EventType = eventType,
            AggregateId = source.AggregateId,
            Sequence = sequence,
            OccurredAt = occurredAt,
            CorrelationId = source.CorrelationId,
            CausationId = source.EventId,
            Producer = Producer,
        };

    private static char ActiveColor(string fen)
    {
        string[] parts = fen.Split(' ');
        return parts.Length >= 2 ? parts[1][0] : 'w';
    }
}
