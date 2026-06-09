using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Avro.Generic;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Maichess.Events.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;

namespace MaichessMatchManagerService.Events;

// Consumes match.commands.v1 and applies CreateMatchCommand: creates the match
// with the caller-minted id, then pushes `matched` to each human participant.
// This replaces the inbound Matches.CreateMatch gRPC call from Match Maker.
//
// Dual-read (Kafka task 02): the topic is mid-migration from Avro to Protobuf, so
// each message is decoded with the arm its Confluent schema id resolves to in the
// registry. The producer (match-maker KafkaMatchCreator) now emits Protobuf; the
// Avro arm is kept so already-enqueued Avro commands still decode and the cutover
// stays reversible (it is removed in task 09 with the registry).
[ExcludeFromCodeCoverage]
internal sealed class MatchCommandConsumer : BackgroundService
{
    private const string Topic = "match.commands.v1";
    private const string GroupId = "match-manager";

    private readonly MatchService matchService;
    private readonly ILogger<MatchCommandConsumer> logger;
    private readonly CachedSchemaRegistryClient registry;
    private readonly IConsumer<string, byte[]> consumer;
    private readonly AvroDeserializer<GenericRecord> avroDeserializer;
    private readonly ProtobufDeserializer<MatchCommand> protoDeserializer;
    private readonly ConcurrentDictionary<int, bool> isProtobuf = new();

    public MatchCommandConsumer(
        MatchService matchService,
        ILogger<MatchCommandConsumer> logger)
    {
        this.matchService = matchService;
        this.logger = logger;

        string bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "kafka:9092";
        string registryUrl = Environment.GetEnvironmentVariable("SCHEMA_REGISTRY_URL")
            ?? "http://schema-registry:8081";

        registry = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registryUrl });
        avroDeserializer = new AvroDeserializer<GenericRecord>(registry);
        protoDeserializer = new ProtobufDeserializer<MatchCommand>();
        consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        })
            .SetKeyDeserializer(Deserializers.Utf8)
            .SetValueDeserializer(Deserializers.ByteArray)
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
                    ConsumeResult<string, byte[]> result = consumer.Consume(ct);
                    if (result?.Message?.Value is { } value)
                    {
                        Handle(value, ct).GetAwaiter().GetResult();
                    }
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning(ex, "Error consuming {Topic}", Topic);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error handling match command");
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

    private async Task Handle(byte[] value, CancellationToken ct)
    {
        int? schemaId = ConfluentFraming.TryReadSchemaId(value);
        if (schemaId is null)
        {
            logger.LogWarning("Dropping non-Confluent-framed message on {Topic}", Topic);
            return;
        }

        var context = new SerializationContext(MessageComponentType.Value, Topic);
        CreateMatchInput? input;
        if (await IsProtobuf(schemaId.Value).ConfigureAwait(false))
        {
            MatchCommand envelope = await protoDeserializer
                .DeserializeAsync(value, false, context).ConfigureAwait(false);
            input = MatchCommandReader.TryReadCreateMatch(envelope, out CreateMatchInput proto) ? proto : null;
        }
        else
        {
            GenericRecord envelope = await avroDeserializer
                .DeserializeAsync(value, false, context).ConfigureAwait(false);
            input = MatchCommandAvroReader.TryReadCreateMatch(envelope, out CreateMatchInput avro) ? avro : null;
        }

        if (input is not null)
        {
            await Apply(input, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsProtobuf(int schemaId)
    {
        if (isProtobuf.TryGetValue(schemaId, out bool cached))
        {
            return cached;
        }

        Schema schema = await registry.GetSchemaAsync(schemaId).ConfigureAwait(false);
        bool proto = schema.SchemaType == SchemaType.Protobuf;
        isProtobuf[schemaId] = proto;
        return proto;
    }

    // The match-maker emits `matched` (it minted the id); this consumer only
    // materializes the match document with that id.
    private Task<MatchDocument> Apply(CreateMatchInput input, CancellationToken ct) =>
        matchService.CreateMatchAsync(
            input.White,
            input.Black,
            input.TimeFormat,
            input.CreatedBy,
            input.StartFen,
            input.Source,
            input.ExternalProvider,
            input.ExternalRef,
            id: input.Id,
            ct: ct);
}
