using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Maichess.Events.V1;
using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

// Publishes real-time events to the socket.outbound.v1 Kafka topic. The socket
// service consumes the topic and fans out to clients, replacing the direct
// Socket.BroadcastMatchEvent gRPC call. Payloads are JSON-encoded in payload_json
// so the shape delivered to clients is identical to the legacy gRPC path.
//
// Serialized with Protobuf via the Confluent Protobuf serde (Kafka task 02 —
// socket.outbound.v1 migrated off Avro). The socket consumer dual-reads, so the
// cutover is reversible.
[ExcludeFromCodeCoverage]
internal sealed class KafkaSocketNotifier : ISocketBroadcaster, IDisposable
{
    private const string Topic = "socket.outbound.v1";
    private const string ProducerName = "match-manager-service";

    private readonly IProducer<string, OutboundEvent> producer;
    private readonly CachedSchemaRegistryClient registry;
    private readonly ILogger<KafkaSocketNotifier> logger;

    public KafkaSocketNotifier(ILogger<KafkaSocketNotifier> logger)
    {
        this.logger = logger;

        string bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "kafka:9092";
        string registryUrl = Environment.GetEnvironmentVariable("SCHEMA_REGISTRY_URL")
            ?? "http://schema-registry:8081";

        registry = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registryUrl });
        producer = new ProducerBuilder<string, OutboundEvent>(
                new ProducerConfig { BootstrapServers = bootstrap })
            .SetValueSerializer(ProtobufEventSerdes.Serializer<OutboundEvent>(registry))
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
        PublishToMatch(match.Id, "move_made", payload);
    }

    public void BroadcastMatchEnded(MatchDocument match, string status, string reason)
    {
        Dictionary<string, object?> payload = new()
        {
            ["match_id"] = match.Id,
            ["status"] = status,
            ["reason"] = reason,
        };
        PublishToMatch(match.Id, "match_ended", payload);
    }

    public void BroadcastDrawOffered(MatchDocument match, PlayerDocument offerer)
    {
        Dictionary<string, object?> payload = new()
        {
            ["match_id"] = match.Id,
            ["player"] = PlayerJson(offerer),
        };
        PublishToMatch(match.Id, "draw_offered", payload);
    }

    public void BroadcastDrawDeclined(MatchDocument match, PlayerDocument decliner)
    {
        Dictionary<string, object?> payload = new()
        {
            ["match_id"] = match.Id,
            ["player"] = PlayerJson(decliner),
        };
        PublishToMatch(match.Id, "draw_declined", payload);
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

    private void PublishToMatch(string matchId, string eventName, Dictionary<string, object?> payload)
    {
        OutboundEvent envelope = new()
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = $"socket.{eventName}",
            AggregateId = matchId,
            Sequence = 0L,
            OccurredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Producer = ProducerName,
            Push = new SocketPush
            {
                TargetMatchId = matchId,
                EventName = eventName,
                PayloadJson = JsonSerializer.Serialize(payload),
            },
        };

        Message<string, OutboundEvent> message = new() { Key = matchId, Value = envelope };
        _ = Task.Run(() => ProduceAsync(message, eventName, matchId));
    }

#pragma warning disable CA1031 // Fire-and-forget background publish: log and swallow all failures.
    private async Task ProduceAsync(Message<string, OutboundEvent> message, string eventName, string key)
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
