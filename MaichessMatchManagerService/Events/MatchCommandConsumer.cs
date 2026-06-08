using System.Diagnostics.CodeAnalysis;
using Avro.Generic;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;

namespace MaichessMatchManagerService.Events;

// Consumes match.commands.v1 and applies CreateMatchCommand: creates the match
// with the caller-minted id, then pushes `matched` to each human participant.
// This replaces the inbound Matches.CreateMatch gRPC call from Match Maker.
[ExcludeFromCodeCoverage]
internal sealed class MatchCommandConsumer : BackgroundService
{
    private const string Topic = "match.commands.v1";
    private const string GroupId = "match-manager";

    private readonly MatchService matchService;
    private readonly ILogger<MatchCommandConsumer> logger;
    private readonly CachedSchemaRegistryClient registry;
    private readonly IConsumer<string, GenericRecord> consumer;

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

    private static PlayerDocument ReadPlayer(GenericRecord player) => new()
    {
        UserId = player.TryGetValue("user_id", out object? u) ? u as string : null,
        BotId = player.TryGetValue("bot_id", out object? b) ? b as string : null,
    };

    private static TimeFormatDocument ReadTimeFormat(GenericRecord tf) => new()
    {
        Id = Str(tf, "id"),
        BaseMs = (long)tf["base_ms"],
        IncrementMs = (long)tf["increment_ms"],
        Category = Str(tf, "category"),
    };

    private static string Str(GenericRecord record, string field) =>
        record.TryGetValue(field, out object? v) && v is string s ? s : string.Empty;

    private static string Enum(GenericRecord record, string field) =>
        record.TryGetValue(field, out object? v) && v is GenericEnum e ? e.Value : string.Empty;

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
                        Handle(envelope, ct).GetAwaiter().GetResult();
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

    private async Task Handle(GenericRecord envelope, CancellationToken ct)
    {
        if (!envelope.TryGetValue("payload", out object? payloadObj) ||
            payloadObj is not GenericRecord command ||
            command.Schema.Name != "CreateMatchCommand")
        {
            return;
        }

        string matchId = Str(envelope, "aggregate_id");
        PlayerDocument white = ReadPlayer((GenericRecord)command["white"]);
        PlayerDocument black = ReadPlayer((GenericRecord)command["black"]);
        TimeFormatDocument timeFormat = ReadTimeFormat((GenericRecord)command["time_format"]);
        PlayerDocument? createdBy =
            command.TryGetValue("created_by", out object? cb) && cb is GenericRecord cbr ? ReadPlayer(cbr) : null;
        string startFen = Str(command, "start_fen");
        string source = Enum(command, "source").Equals("EXTERNAL", StringComparison.OrdinalIgnoreCase)
            ? "external"
            : "native";

        // The match-maker emits `matched` (it minted the id); this consumer only
        // materializes the match document with that id.
        await matchService.CreateMatchAsync(
            white,
            black,
            timeFormat,
            createdBy,
            string.IsNullOrEmpty(startFen) ? null : startFen,
            source,
            Str(command, "external_provider"),
            Str(command, "external_ref"),
            id: string.IsNullOrEmpty(matchId) ? null : matchId,
            ct: ct);
    }
}
