using System.Diagnostics.CodeAnalysis;
using Avro.Generic;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using MaichessMatchManagerService.Data;

namespace MaichessMatchManagerService.Kafka;

// Plain Kafka consumer that materialises the compacted user.events.v1 topic into the
// shared Redis user replica (user:{id}). Reads from the beginning on first deploy
// (AutoOffsetReset.Earliest) so the replica warms to the full compacted state;
// thereafter every record drives an idempotent partial upsert via
// UserReplicaProjection. The replica is rebuildable — reset this group's offsets (or
// flush user:* and restart) to replay the topic. Excluded from coverage: the pure
// projection it delegates to is unit-tested; this class is the live-Kafka shell,
// mirroring MatchCommandConsumer.
[ExcludeFromCodeCoverage]
internal sealed class UserReplicaConsumer : BackgroundService
{
    private const string Topic = "user.events.v1";
    private const string GroupId = "match-manager-user-replica";

    private readonly IUserReplica replica;
    private readonly ILogger<UserReplicaConsumer> logger;
    private readonly CachedSchemaRegistryClient registry;
    private readonly IConsumer<string, GenericRecord> consumer;

    public UserReplicaConsumer(IUserReplica replica, ILogger<UserReplicaConsumer> logger)
    {
        this.replica = replica;
        this.logger = logger;

        string bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "kafka:9092";
        string registryUrl = Environment.GetEnvironmentVariable("SCHEMA_REGISTRY_URL")
            ?? "http://schema-registry:8081";

        registry = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registryUrl });
        consumer = new ConsumerBuilder<string, GenericRecord>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        })
            .SetValueDeserializer(new AvroDeserializer<GenericRecord>(registry).AsSyncOverAsync())
            .Build();
    }

    public override void Dispose()
    {
        consumer.Dispose();
        registry.Dispose();
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
                    ConsumeResult<string, GenericRecord> result = consumer.Consume(ct);
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
                    logger.LogWarning(ex, "Error applying user event to replica");
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

    private void Apply(GenericRecord envelope, CancellationToken ct)
    {
        UserReplicaUpsert? upsert = UserReplicaProjection.Project(envelope);
        if (upsert is not null)
        {
            replica.UpsertAsync(upsert.UserId, upsert.Fields, ct).GetAwaiter().GetResult();
        }
    }
}
