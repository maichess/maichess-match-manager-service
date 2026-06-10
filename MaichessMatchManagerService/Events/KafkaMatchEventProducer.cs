using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Maichess.Events.V1;

namespace MaichessMatchManagerService.Events;

// Live-Kafka glue for emitting command-side facts to match.events.v1: a single,
// idempotent, non-transactional produce keyed by aggregate_id (matchId) so per-match
// order is preserved. The validator and projector own the effectively-once transaction
// for the consume->produce steps; the command side only seeds the log (MoveSubmitted /
// MatchEnded / DrawOffered / DrawDeclined / MatchCreated), so at-least-once + the
// projector's (aggregate_id, sequence) dedupe is sufficient here.
//
// Excluded from coverage like the platform's other producer/consumer glue; the events
// it emits are built by the pure, fully-tested MatchCommands / MatchService.
[ExcludeFromCodeCoverage]
internal sealed class KafkaMatchEventProducer : IMatchEventProducer, IDisposable
{
    private const string Topic = "match.events.v1";

    private readonly IProducer<string, MatchEvent> producer;
    private readonly CachedSchemaRegistryClient registry;
    private readonly ILogger<KafkaMatchEventProducer> logger;

    public KafkaMatchEventProducer(ILogger<KafkaMatchEventProducer> logger)
    {
        this.logger = logger;

        string bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "kafka:9092";
        string registryUrl = Environment.GetEnvironmentVariable("SCHEMA_REGISTRY_URL")
            ?? "http://schema-registry:8081";

        registry = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registryUrl });
        producer = new ProducerBuilder<string, MatchEvent>(
                new ProducerConfig { BootstrapServers = bootstrap, EnableIdempotence = true })
            .SetValueSerializer(ProtobufEventSerdes.Serializer<MatchEvent>(registry))
            .Build();
    }

    public async Task ProduceAsync(MatchEvent ev, CancellationToken ct)
    {
        Message<string, MatchEvent> message = new() { Key = ev.AggregateId, Value = ev };
        try
        {
            await producer.ProduceAsync(Topic, message, ct);
        }
        catch (ProduceException<string, MatchEvent> ex)
        {
            logger.LogWarning(ex, "Failed to produce {EventType} for {MatchId}", ev.EventType, ev.AggregateId);
            throw;
        }
    }

    public void Dispose()
    {
        producer.Flush(TimeSpan.FromSeconds(5));
        producer.Dispose();
        registry.Dispose();
    }
}
