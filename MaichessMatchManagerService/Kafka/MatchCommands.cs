using Maichess.Events.V1;
using MaichessMatchManagerService.Services;

namespace MaichessMatchManagerService.Kafka;

// Pure command-side decision logic for the match write entrypoint. Given the live
// read-model state of a match and a player's intent, it validates the intent against
// the same participant/turn/draw rules the retired synchronous path enforced and
// builds the single match.events.v1 event to produce. Invalid intents throw the
// existing exceptions, which the REST/gRPC adapters already translate to 4xx.
//
// The orchestrator (MatchService) loads the state from ILiveMatchState, calls one of
// these, and produces the returned event via IMatchEventProducer; this class stays
// pure and fully unit-tested. Envelope/sequence conventions mirror MatchProjector:
// a command starts a new logical flow (fresh correlation_id, empty causation_id) and
// advances the per-aggregate sequence by one past the read model.
internal static class MatchCommands
{
    private const string Producer = "match-manager-service";

    // POST /moves: the mover must be a participant and it must be their turn.
    internal static MatchEvent SubmitMove(
        LiveMatchState state, string userId, string moveUci, long nowMs, Func<string> newId)
    {
        EnsureOngoing(state);
        bool isWhite = ResolveSide(state, userId);
        if (isWhite != (ActiveColor(state.CurrentFen) == 'w'))
        {
            throw new NotYourTurnException();
        }

        MatchEvent ev = Envelope(state, newId, "match.MoveSubmitted", nowMs);
        ev.MoveSubmitted = new MoveSubmitted
        {
            MoveUci = moveUci,
            By = new Player { UserId = userId },
            Fen = state.CurrentFen,
        };
        ev.MoveSubmitted.PositionHistory.AddRange(state.PositionHistory);
        return ev;
    }

    // POST /resign: a participant forfeits; the opponent wins immediately.
    internal static MatchEvent Resign(LiveMatchState state, string userId, long nowMs, Func<string> newId)
    {
        EnsureOngoing(state);
        bool isWhite = ResolveSide(state, userId);

        MatchEvent ev = Envelope(state, newId, "match.MatchEnded", nowMs);
        ev.MatchEnded = new MatchEnded
        {
            Status = isWhite ? MatchStatus.BlackWon : MatchStatus.WhiteWon,
            EndReason = EndReason.Resignation,
            FinishedAtMs = nowMs,
        };
        return ev;
    }

    // POST /draw-offer: offer a draw to a human opponent. Only one offer at a time.
    internal static MatchEvent OfferDraw(LiveMatchState state, string userId, long nowMs, Func<string> newId)
    {
        EnsureOngoing(state);
        bool isWhite = ResolveSide(state, userId);

        PlayerRef opponent = isWhite ? state.Black : state.White;
        if (opponent.BotId is not null)
        {
            throw new NotParticipantException();
        }

        if (state.PendingDrawOffererUserId is not null)
        {
            throw new DrawOfferAlreadyPendingException();
        }

        MatchEvent ev = Envelope(state, newId, "match.DrawOffered", nowMs);
        ev.DrawOffered = new DrawOffered { By = new Player { UserId = userId } };
        return ev;
    }

    // POST /draw-offer/accept: the recipient of a pending offer accepts; match is drawn.
    internal static MatchEvent AcceptDraw(LiveMatchState state, string userId, long nowMs, Func<string> newId)
    {
        EnsureOngoing(state);
        ResolveSide(state, userId);

        if (state.PendingDrawOffererUserId is null)
        {
            throw new NoDrawOfferPendingException();
        }

        if (state.PendingDrawOffererUserId == userId)
        {
            throw new NotDrawRecipientException();
        }

        MatchEvent ev = Envelope(state, newId, "match.MatchEnded", nowMs);
        ev.MatchEnded = new MatchEnded
        {
            Status = MatchStatus.Draw,
            EndReason = EndReason.DrawAgreement,
            FinishedAtMs = nowMs,
        };
        return ev;
    }

    // DELETE /draw-offer: decline (or withdraw) a pending offer.
    internal static MatchEvent DeclineDraw(LiveMatchState state, string userId, long nowMs, Func<string> newId)
    {
        EnsureOngoing(state);
        ResolveSide(state, userId);

        if (state.PendingDrawOffererUserId is null)
        {
            throw new NoDrawOfferPendingException();
        }

        MatchEvent ev = Envelope(state, newId, "match.DrawDeclined", nowMs);
        ev.DrawDeclined = new DrawDeclined { By = new Player { UserId = userId } };
        return ev;
    }

    // The watchdog's intent for a flagged clock: the side to move ran out, so it loses.
    internal static MatchEvent Timeout(LiveMatchState state, bool whiteFlagged, long nowMs, Func<string> newId)
    {
        MatchEvent ev = Envelope(state, newId, "match.MatchEnded", nowMs);
        ev.MatchEnded = new MatchEnded
        {
            Status = whiteFlagged ? MatchStatus.BlackWon : MatchStatus.WhiteWon,
            EndReason = EndReason.Timeout,
            FinishedAtMs = nowMs,
        };
        return ev;
    }

    private static void EnsureOngoing(LiveMatchState state)
    {
        if (state.Status != "ongoing")
        {
            throw new MatchAlreadyEndedException();
        }
    }

    // Returns true when the user plays white, false for black; throws when the user
    // is neither side. Mirrors the synchronous path's direct id comparison.
    private static bool ResolveSide(LiveMatchState state, string userId) => userId switch
    {
        _ when userId == state.White.UserId => true,
        _ when userId == state.Black.UserId => false,
        _ => throw new NotParticipantException(),
    };

    private static MatchEvent Envelope(LiveMatchState state, Func<string> newId, string eventType, long nowMs) =>
        new()
        {
            EventId = newId(),
            EventType = eventType,
            AggregateId = state.MatchId,
            Sequence = state.Sequence + 1,
            OccurredAt = nowMs,
            CorrelationId = newId(),
            CausationId = string.Empty,
            Producer = Producer,
        };

    private static char ActiveColor(string fen)
    {
        string[] parts = fen.Split(' ');
        return parts.Length >= 2 ? parts[1][0] : 'w';
    }
}
