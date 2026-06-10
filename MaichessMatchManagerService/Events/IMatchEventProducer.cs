using Maichess.Events.V1;

namespace MaichessMatchManagerService.Events;

// Seam over producing a single MatchEvent to match.events.v1. The command side
// (MatchService) and the timeout watchdog emit through this; the Kafka glue lives in
// KafkaMatchEventProducer (excluded from coverage). Keeping the seam lets the
// command-side orchestration be unit-tested with a substitute.
internal interface IMatchEventProducer
{
    Task ProduceAsync(MatchEvent ev, CancellationToken ct);
}
