using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using Maichess.Events.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Events;

namespace MaichessMatchManagerService.Kafka;

// Plain Kafka consumer that folds the compacted cheat.events.v1 topic into the
// Redis user replica's flagged bit (user:{id} hash). Reads from the beginning
// on first deploy so the replica warms to the full compacted flag state;
// thereafter every record drives an idempotent upsert via CheatFlagProjection.
// Rebuildable — reset this group's offsets to replay. Protobuf on the wire
// (the topic is born post-Avro). Excluded from coverage: the pure projection
// it delegates to is unit-tested; this class is the live-Kafka shell,
// mirroring UserReplicaConsumer.
[ExcludeFromCodeCoverage]
internal sealed class CheatFlagConsumer : BackgroundService
{
    private const string Topic = "cheat.events.v1";
    private const string GroupId = "match-manager-cheat-flags";

    private readonly IUserReplica replica;
    private readonly ILogger<CheatFlagConsumer> logger;
    private readonly IConsumer<string, CheatEvent> consumer;

    public CheatFlagConsumer(IUserReplica replica, ILogger<CheatFlagConsumer> logger)
    {
        this.replica = replica;
        this.logger = logger;

        string bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "kafka:9092";
        consumer = new ConsumerBuilder<string, CheatEvent>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        })
            .SetValueDeserializer(ProtobufEventSerdes.Deserializer<CheatEvent>())
            .Build();
    }

    public override void Dispose()
    {
        consumer.Dispose();
        base.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);

#pragma warning disable CA1031 // Resilient consumer loop: log and continue on per-message failures.
    private void ConsumeLoop(CancellationToken ct)
    {
        consumer.Subscribe(Topic);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ConsumeResult<string, CheatEvent> result = consumer.Consume(ct);
                    if (result?.Message?.Value is { } envelope)
                    {
                        Apply(envelope, ct);
                    }
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning(ex, "Error consuming {Topic}", Topic);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error applying cheat event to replica");
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

    private void Apply(CheatEvent envelope, CancellationToken ct)
    {
        UserReplicaUpsert? upsert = CheatFlagProjection.Project(envelope);
        if (upsert is not null)
        {
            replica.UpsertAsync(upsert.UserId, upsert.Fields, ct).GetAwaiter().GetResult();
        }
    }
}
