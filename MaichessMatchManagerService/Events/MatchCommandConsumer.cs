using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using Maichess.Events.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;

namespace MaichessMatchManagerService.Events;

// Consumes match.commands.v1 and applies CreateMatchCommand: creates the match
// with the caller-minted id, then pushes `matched` to each human participant.
// This replaces the inbound Matches.CreateMatch gRPC call from Match Maker.
//
// Values are raw Protobuf bytes (Kafka task 09 removed the Schema Registry); the
// MatchCommand envelope is parsed directly.
[ExcludeFromCodeCoverage]
internal sealed class MatchCommandConsumer : BackgroundService
{
    private const string Topic = "match.commands.v1";
    private const string GroupId = "match-manager";

    private readonly MatchService matchService;
    private readonly ILogger<MatchCommandConsumer> logger;
    private readonly IConsumer<string, byte[]> consumer;

    public MatchCommandConsumer(
        MatchService matchService,
        ILogger<MatchCommandConsumer> logger)
    {
        this.matchService = matchService;
        this.logger = logger;

        string bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "kafka:9092";

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
        MatchCommand envelope = MatchCommand.Parser.ParseFrom(value);
        if (MatchCommandReader.TryReadCreateMatch(envelope, out CreateMatchInput input))
        {
            await Apply(input, ct).ConfigureAwait(false);
        }
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
