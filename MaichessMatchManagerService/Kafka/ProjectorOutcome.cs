using System.Diagnostics.CodeAnalysis;
using Maichess.Events.V1;

namespace MaichessMatchManagerService.Kafka;

// The result of running MatchProjector on a single consumed match.events.v1 record:
// the new read-model state to persist to Redis, the match-events to produce back to
// match.events.v1 (MoveApplied / MatchEnded / BotMoveRequested / MoveSubmitted), and
// the socket pushes to produce to socket.outbound.v1 (move_made / match_ended). The
// consumer produces both event lists and writes the state inside one Kafka
// transaction; everything here is rebuildable from the log.
//
// A pure data carrier — excluded from coverage like the other read-model records.
[ExcludeFromCodeCoverage]
internal sealed record ProjectorOutcome(
    LiveMatchState? State,
    IReadOnlyList<MatchEvent> Events,
    IReadOnlyList<OutboundEvent> Pushes);
