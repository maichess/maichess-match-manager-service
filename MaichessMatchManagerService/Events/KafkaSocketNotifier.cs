using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Avro;
using Avro.Generic;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

// Publishes real-time events to the socket.outbound.v1 Kafka topic. The socket
// service consumes the topic and fans out to clients, replacing the direct
// Socket.BroadcastMatchEvent gRPC call. Payloads are JSON-encoded in payload_json
// so the shape delivered to clients is identical to the legacy gRPC path.
[ExcludeFromCodeCoverage]
internal sealed class KafkaSocketNotifier : ISocketBroadcaster, IDisposable
{
    private const string Topic = "socket.outbound.v1";
    private const string Producer = "match-manager-service";

    private readonly IProducer<string, GenericRecord> producer;
    private readonly CachedSchemaRegistryClient registry;
    private readonly RecordSchema envelopeSchema;
    private readonly RecordSchema pushSchema;
    private readonly ILogger<KafkaSocketNotifier> logger;

    public KafkaSocketNotifier(ILogger<KafkaSocketNotifier> logger)
    {
        this.logger = logger;

        string bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "kafka:9092";
        string registryUrl = Environment.GetEnvironmentVariable("SCHEMA_REGISTRY_URL")
            ?? "http://schema-registry:8081";

        envelopeSchema = (RecordSchema)Avro.Schema.Parse(LoadSchema());
        pushSchema = (RecordSchema)envelopeSchema.Fields.Single(f => f.Name == "payload").Schema;

        registry = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registryUrl });
        producer = new ProducerBuilder<string, GenericRecord>(
                new ProducerConfig { BootstrapServers = bootstrap })
            .SetValueSerializer(new AvroSerializer<GenericRecord>(registry))
            .Build();
    }

    public void BroadcastMoveMade(
        MatchDocument match,
        string move,
        string resultingFen,
        int index,
        PlayerDocument mover,
        long whiteTimeMs,
        long blackTimeMs)
    {
        Dictionary<string, object?> payload = new()
        {
            ["match_id"] = match.Id,
            ["move"] = move,
            ["resulting_fen"] = resultingFen,
            ["index"] = index,
            ["player"] = PlayerJson(mover),
            ["white_time_ms"] = whiteTimeMs,
            ["black_time_ms"] = blackTimeMs,
        };
        Publish(null, match.Id, match.Id, "move_made", payload);
    }

    public void BroadcastMatchEnded(MatchDocument match, string status, string reason)
    {
        Dictionary<string, object?> payload = new()
        {
            ["match_id"] = match.Id,
            ["status"] = status,
            ["reason"] = reason,
        };
        Publish(null, match.Id, match.Id, "match_ended", payload);
    }

    public void BroadcastDrawOffered(MatchDocument match, PlayerDocument offerer)
    {
        Dictionary<string, object?> payload = new()
        {
            ["match_id"] = match.Id,
            ["player"] = PlayerJson(offerer),
        };
        Publish(null, match.Id, match.Id, "draw_offered", payload);
    }

    public void BroadcastDrawDeclined(MatchDocument match, PlayerDocument decliner)
    {
        Dictionary<string, object?> payload = new()
        {
            ["match_id"] = match.Id,
            ["player"] = PlayerJson(decliner),
        };
        Publish(null, match.Id, match.Id, "draw_declined", payload);
    }

    public void Dispose()
    {
        producer.Flush(TimeSpan.FromSeconds(5));
        producer.Dispose();
        registry.Dispose();
    }

    private static Dictionary<string, string> PlayerJson(PlayerDocument player) =>
        player.UserId is not null ? new Dictionary<string, string> { ["user_id"] = player.UserId }
        : player.BotId is not null ? new Dictionary<string, string> { ["bot_id"] = player.BotId }
        : [];

    private static string LoadSchema()
    {
        Assembly asm = typeof(KafkaSocketNotifier).Assembly;
        string name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("socket.outbound.v1.avsc", StringComparison.Ordinal));
        using Stream stream = asm.GetManifestResourceStream(name)!;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private void Publish(
        string? targetUserId,
        string? targetMatchId,
        string key,
        string eventName,
        Dictionary<string, object?> payload)
    {
        string payloadJson = JsonSerializer.Serialize(payload);

        GenericRecord push = new(pushSchema);
        push.Add("target_user_id", targetUserId);
        push.Add("target_match_id", targetMatchId);
        push.Add("event_name", eventName);
        push.Add("payload_json", payloadJson);

        GenericRecord envelope = new(envelopeSchema);
        envelope.Add("event_id", Guid.NewGuid().ToString());
        envelope.Add("event_type", $"socket.{eventName}");
        envelope.Add("aggregate_id", key);
        envelope.Add("sequence", 0L);
        envelope.Add("occurred_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        envelope.Add("correlation_id", string.Empty);
        envelope.Add("causation_id", string.Empty);
        envelope.Add("producer", Producer);
        envelope.Add("payload", push);

        Message<string, GenericRecord> message = new() { Key = key, Value = envelope };
        _ = Task.Run(() => ProduceAsync(message, eventName, key));
    }

#pragma warning disable CA1031 // Fire-and-forget background publish: log and swallow all failures.
    private async Task ProduceAsync(Message<string, GenericRecord> message, string eventName, string key)
    {
        try
        {
            await producer.ProduceAsync(Topic, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish socket event {Event} for {Key}", eventName, key);
        }
    }
#pragma warning restore CA1031
}
