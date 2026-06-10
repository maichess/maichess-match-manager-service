using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Maichess.Events.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Kafka;
using MaichessMatchManagerService.Services;

namespace MaichessMatchManagerService.Events;

// Live-Kafka shell for the match-manager projector. Excluded from coverage and
// mutation like the platform's other consumer/producer glue: the decisions live in
// the pure, fully-tested MatchProjector (events to emit + new read-model state) and
// MatchHistoryProjection (durable document fold); this class only moves bytes.
//
// It consumes match.events.v1, runs MatchProjector on each record, and produces the
// resulting match-events (MoveApplied / MatchEnded / BotMoveRequested / MoveSubmitted)
// back to match.events.v1 and the socket pushes to socket.outbound.v1 inside a single
// Kafka transaction (consume->produce, effectively-once — the same pattern the Scala
// move-validator stream uses). The Redis read model and the match-db write-through are
// rebuildable side-effects applied after the transaction commits; on a crash they are
// reconstructed by replaying the log (MatchProjection.Rebuild / MatchHistoryProjection).
[ExcludeFromCodeCoverage]
internal sealed class MatchEventProjectorConsumer : BackgroundService
{
    private const string Topic = "match.events.v1";
    private const string SocketTopic = "socket.outbound.v1";
    private const string GroupId = "match-manager-projector";

    private static readonly TimeSpan TxTimeout = TimeSpan.FromSeconds(30);

    private readonly ILiveMatchState liveState;
    private readonly IMatchRepository repository;
    private readonly IMatchCache cache;
    private readonly ILogger<MatchEventProjectorConsumer> logger;
    private readonly CachedSchemaRegistryClient registry;
    private readonly IConsumer<string, MatchEvent> consumer;
    private readonly IProducer<string, byte[]> producer;
    private readonly IAsyncSerializer<MatchEvent> eventSerializer;
    private readonly IAsyncSerializer<OutboundEvent> pushSerializer;

    public MatchEventProjectorConsumer(
        ILiveMatchState liveState,
        IMatchRepository repository,
        IMatchCache cache,
        ILogger<MatchEventProjectorConsumer> logger)
    {
        this.liveState = liveState;
        this.repository = repository;
        this.cache = cache;
        this.logger = logger;

        string bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "kafka:9092";
        string registryUrl = Environment.GetEnvironmentVariable("SCHEMA_REGISTRY_URL")
            ?? "http://schema-registry:8081";

        registry = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registryUrl });
        consumer = new ConsumerBuilder<string, MatchEvent>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        })
            .SetValueDeserializer(ProtobufEventSerdes.Deserializer<MatchEvent>())
            .Build();
        producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = bootstrap,
            TransactionalId = $"{GroupId}-{Guid.NewGuid()}",
            EnableIdempotence = true,
        }).Build();
        eventSerializer = ProtobufEventSerdes.Serializer<MatchEvent>(registry);
        pushSerializer = ProtobufEventSerdes.Serializer<OutboundEvent>(registry);
    }

    public override void Dispose()
    {
        consumer.Dispose();
        producer.Dispose();
        registry.Dispose();
        base.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);

    private static IEnumerable<string> ParticipantUserIds(MatchDocument match) =>
        new[] { match.White.UserId, match.Black.UserId, match.CreatedBy?.UserId }
            .Where(id => id is not null)
            .Select(id => MatchService.CanonicalizeUserId(id)!)
            .Distinct();

#pragma warning disable CA1031 // Resilient consumer loop: log and continue on per-message failures.
    private void ConsumeLoop(CancellationToken ct)
    {
        producer.InitTransactions(TxTimeout);
        consumer.Subscribe(Topic);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ConsumeResult<string, MatchEvent> result = consumer.Consume(ct);
                    if (result?.Message?.Value is { } ev)
                    {
                        Project(result, ev, ct).GetAwaiter().GetResult();
                    }
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning(ex, "Error consuming {Topic}", Topic);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error projecting match event");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        finally
        {
            consumer.Close();
        }
    }
#pragma warning restore CA1031

    private async Task Project(ConsumeResult<string, MatchEvent> result, MatchEvent ev, CancellationToken ct)
    {
        LiveMatchState? state = await liveState.GetAsync(ev.AggregateId, ct);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ProjectorOutcome outcome = MatchProjector.Decide(state, ev, now, () => Guid.NewGuid().ToString());

        await ProduceTransaction(result, outcome);

        if (outcome.State is not null)
        {
            await liveState.SetAsync(outcome.State, ct);
        }

        await WriteThrough(ev, ct);
    }

    private async Task ProduceTransaction(ConsumeResult<string, MatchEvent> result, ProjectorOutcome outcome)
    {
        producer.BeginTransaction();
        try
        {
#pragma warning disable CA1849 // Buffered produce is the transactional idiom — CommitTransaction flushes.
            foreach (MatchEvent emit in outcome.Events)
            {
                byte[] value = await eventSerializer.SerializeAsync(
                    emit, new SerializationContext(MessageComponentType.Value, Topic));
                producer.Produce(Topic, new Message<string, byte[]> { Key = emit.AggregateId, Value = value });
            }

            foreach (OutboundEvent push in outcome.Pushes)
            {
                byte[] value = await pushSerializer.SerializeAsync(
                    push, new SerializationContext(MessageComponentType.Value, SocketTopic));
                producer.Produce(SocketTopic, new Message<string, byte[]> { Key = push.AggregateId, Value = value });
            }
#pragma warning restore CA1849

            producer.SendOffsetsToTransaction(
                [new TopicPartitionOffset(result.TopicPartition, result.Offset + 1)],
                consumer.ConsumerGroupMetadata,
                TxTimeout);
            producer.CommitTransaction();
        }
        catch
        {
            producer.AbortTransaction();
            throw;
        }
    }

    // Materialises durable history into match-db. MatchCreated inserts the document;
    // MoveValidated/MoveApplied/MatchEnded load-modify-write it; MatchEnded also
    // refreshes the finished-match cache and evicts each participant's page cache,
    // mirroring MatchService.OnMatchEndedAsync on the synchronous path.
    private async Task WriteThrough(MatchEvent ev, CancellationToken ct)
    {
        if (ev.PayloadCase == MatchEvent.PayloadOneofCase.MatchCreated)
        {
            MatchDocument? created = MatchHistoryProjection.Apply(null, ev);
            if (created is not null)
            {
                await repository.InsertAsync(created, ct);
            }

            return;
        }

        if (ev.PayloadCase is not (MatchEvent.PayloadOneofCase.MoveValidated
            or MatchEvent.PayloadOneofCase.MoveApplied
            or MatchEvent.PayloadOneofCase.MatchEnded))
        {
            return;
        }

        MatchDocument? existing = await repository.GetByIdAsync(ev.AggregateId, ct);
        MatchDocument? updated = MatchHistoryProjection.Apply(existing, ev);
        if (updated is null)
        {
            return;
        }

        await repository.ReplaceAsync(updated, ct);

        if (ev.PayloadCase == MatchEvent.PayloadOneofCase.MatchEnded)
        {
            await cache.SetMatchAsync(updated, ct);
            foreach (string userId in ParticipantUserIds(updated))
            {
                await cache.InvalidateUserPagesAsync(userId, ct);
            }
        }
    }
}
