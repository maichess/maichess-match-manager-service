using Avro;
using Avro.Generic;
using Maichess.Events.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// The match.commands.v1 consumer dual-reads during the Avro→Protobuf migration
// (Kafka task 02). These tests exercise both decode arms at the mapping level — the
// Protobuf reader and the retained Avro reader both project onto the same
// CreateMatchInput — plus the Confluent schema-id discriminator that routes between
// them. The consumer's broker/registry glue itself is [ExcludeFromCodeCoverage].
public sealed class MatchCommandDualReadTests
{
    // ---- Protobuf arm: MatchCommandReader ----

    private static MatchCommand ProtoEnvelope(CreateMatchCommand command, string aggregateId = "match-1") => new()
    {
        EventId = "e1",
        EventType = "match.CreateMatch",
        AggregateId = aggregateId,
        OccurredAt = 1_700_000_000_000,
        Producer = "match-maker-service",
        CreateMatch = command,
    };

    [Fact]
    public void Proto_HumanVsBot_MapsAllFields()
    {
        MatchCommand envelope = ProtoEnvelope(new CreateMatchCommand
        {
            White = new Player { UserId = "white-user" },
            Black = new Player { BotId = "bot-7" },
            TimeFormat = new TimeFormat { Id = "3+2", BaseMs = 180_000, IncrementMs = 2_000, Category = "blitz" },
            Source = MatchSource.Native,
        });

        Assert.True(MatchCommandReader.TryReadCreateMatch(envelope, out CreateMatchInput input));
        Assert.Equal("match-1", input.Id);
        Assert.Equal("white-user", input.White.UserId);
        Assert.Null(input.White.BotId);
        Assert.Equal("bot-7", input.Black.BotId);
        Assert.Null(input.Black.UserId);
        Assert.Equal("3+2", input.TimeFormat.Id);
        Assert.Equal(180_000, input.TimeFormat.BaseMs);
        Assert.Equal(2_000, input.TimeFormat.IncrementMs);
        Assert.Equal("blitz", input.TimeFormat.Category);
        Assert.Null(input.CreatedBy);
        Assert.Null(input.StartFen);
        Assert.Equal("native", input.Source);
        Assert.Equal(string.Empty, input.ExternalProvider);
        Assert.Equal(string.Empty, input.ExternalRef);
    }

    [Fact]
    public void Proto_External_MapsSourceCreatedByAndStartFen()
    {
        MatchCommand envelope = ProtoEnvelope(new CreateMatchCommand
        {
            White = new Player { ExternalName = "Magnus" },
            Black = new Player { UserId = "u2" },
            TimeFormat = new TimeFormat { Id = "10+0", BaseMs = 600_000, Category = "rapid" },
            CreatedBy = new Player { UserId = "u2" },
            StartFen = "8/8/8/8/8/8/8/8 w - - 0 1",
            Source = MatchSource.External,
            ExternalProvider = "lichess",
            ExternalRef = "abc123",
        });

        Assert.True(MatchCommandReader.TryReadCreateMatch(envelope, out CreateMatchInput input));
        Assert.Equal("Magnus", input.White.ExternalName);
        Assert.Equal("external", input.Source);
        Assert.Equal("u2", input.CreatedBy!.UserId);
        Assert.Equal("8/8/8/8/8/8/8/8 w - - 0 1", input.StartFen);
        Assert.Equal("lichess", input.ExternalProvider);
        Assert.Equal("abc123", input.ExternalRef);
    }

    [Fact]
    public void Proto_UnsetCreatedBy_IsNull()
    {
        MatchCommand envelope = ProtoEnvelope(new CreateMatchCommand
        {
            White = new Player { UserId = "w" },
            Black = new Player { UserId = "b" },
            TimeFormat = new TimeFormat { Id = "5+0", BaseMs = 300_000, Category = "blitz" },
            // CreatedBy left unset (proto3 null)
        });

        Assert.True(MatchCommandReader.TryReadCreateMatch(envelope, out CreateMatchInput input));
        Assert.Null(input.CreatedBy);
    }

    [Fact]
    public void Proto_EmptyAggregateId_MapsToNullId()
    {
        MatchCommand envelope = ProtoEnvelope(
            new CreateMatchCommand
            {
                White = new Player { UserId = "w" },
                Black = new Player { UserId = "b" },
                TimeFormat = new TimeFormat { Id = "5+0", BaseMs = 300_000, Category = "blitz" },
            },
            aggregateId: string.Empty);

        Assert.True(MatchCommandReader.TryReadCreateMatch(envelope, out CreateMatchInput input));
        Assert.Null(input.Id);
    }

    [Fact]
    public void Proto_NonCreateMatchPayload_ReturnsFalse()
    {
        MatchCommand envelope = new()
        {
            AggregateId = "match-1",
            Resign = new ResignCommand { ByUserId = "u1" },
        };

        Assert.False(MatchCommandReader.TryReadCreateMatch(envelope, out CreateMatchInput input));
        Assert.Null(input);
    }

    // ---- Avro arm: MatchCommandAvroReader.TryReadCreateMatch ----

    private const string CommandSchemaJson = """
    {
      "type": "record", "name": "MatchCommand", "namespace": "maichess.events.match",
      "fields": [
        { "name": "aggregate_id", "type": "string" },
        { "name": "payload", "type": {
          "type": "record", "name": "CreateMatchCommand",
          "fields": [
            { "name": "white", "type": {
              "type": "record", "name": "Player",
              "fields": [
                { "name": "user_id", "type": ["null", "string"], "default": null },
                { "name": "bot_id", "type": ["null", "string"], "default": null },
                { "name": "external_name", "type": ["null", "string"], "default": null }
              ] } },
            { "name": "black", "type": "Player" },
            { "name": "time_format", "type": {
              "type": "record", "name": "TimeFormat",
              "fields": [
                { "name": "id", "type": "string" },
                { "name": "base_ms", "type": "long" },
                { "name": "increment_ms", "type": "long" },
                { "name": "category", "type": "string" }
              ] } },
            { "name": "created_by", "type": ["null", "Player"], "default": null },
            { "name": "start_fen", "type": "string", "default": "" },
            { "name": "source", "type": {
              "type": "enum", "name": "MatchSource", "symbols": ["NATIVE", "EXTERNAL"] } },
            { "name": "external_provider", "type": "string", "default": "" },
            { "name": "external_ref", "type": "string", "default": "" }
          ] } }
      ]
    }
    """;

    private static readonly RecordSchema EnvelopeSchema = (RecordSchema)Schema.Parse(CommandSchemaJson);
    private static readonly RecordSchema CommandSchema =
        (RecordSchema)EnvelopeSchema.Fields.Single(f => f.Name == "payload").Schema;
    private static readonly RecordSchema PlayerSchema =
        (RecordSchema)CommandSchema.Fields.Single(f => f.Name == "white").Schema;
    private static readonly RecordSchema TimeFormatSchema =
        (RecordSchema)CommandSchema.Fields.Single(f => f.Name == "time_format").Schema;
    private static readonly EnumSchema SourceSchema =
        (EnumSchema)CommandSchema.Fields.Single(f => f.Name == "source").Schema;

    private static GenericRecord AvroPlayer(string? userId, string? botId)
    {
        GenericRecord p = new(PlayerSchema);
        p.Add("user_id", userId);
        p.Add("bot_id", botId);
        p.Add("external_name", null);
        return p;
    }

    [Fact]
    public void Avro_HumanVsBot_MapsAllFields()
    {
        GenericRecord tf = new(TimeFormatSchema);
        tf.Add("id", "3+2");
        tf.Add("base_ms", 180_000L);
        tf.Add("increment_ms", 2_000L);
        tf.Add("category", "blitz");

        GenericRecord command = new(CommandSchema);
        command.Add("white", AvroPlayer("white-user", null));
        command.Add("black", AvroPlayer(null, "bot-7"));
        command.Add("time_format", tf);
        command.Add("created_by", null);
        command.Add("start_fen", string.Empty);
        command.Add("source", new GenericEnum(SourceSchema, "NATIVE"));
        command.Add("external_provider", string.Empty);
        command.Add("external_ref", string.Empty);

        GenericRecord envelope = new(EnvelopeSchema);
        envelope.Add("aggregate_id", "match-1");
        envelope.Add("payload", command);

        Assert.True(MatchCommandAvroReader.TryReadCreateMatch(envelope, out CreateMatchInput input));
        Assert.Equal("match-1", input.Id);
        Assert.Equal("white-user", input.White.UserId);
        Assert.Equal("bot-7", input.Black.BotId);
        Assert.Equal(180_000, input.TimeFormat.BaseMs);
        Assert.Null(input.CreatedBy);
        Assert.Null(input.StartFen);
        Assert.Equal("native", input.Source);
    }

    [Fact]
    public void Avro_External_MapsSourceAndCreatedBy()
    {
        GenericRecord tf = new(TimeFormatSchema);
        tf.Add("id", "10+0");
        tf.Add("base_ms", 600_000L);
        tf.Add("increment_ms", 0L);
        tf.Add("category", "rapid");

        GenericRecord command = new(CommandSchema);
        command.Add("white", AvroPlayer("u1", null));
        command.Add("black", AvroPlayer("u2", null));
        command.Add("time_format", tf);
        command.Add("created_by", AvroPlayer("u1", null));
        command.Add("start_fen", "8/8/8/8/8/8/8/8 w - - 0 1");
        command.Add("source", new GenericEnum(SourceSchema, "EXTERNAL"));
        command.Add("external_provider", "lichess");
        command.Add("external_ref", "abc123");

        GenericRecord envelope = new(EnvelopeSchema);
        envelope.Add("aggregate_id", "match-9");
        envelope.Add("payload", command);

        Assert.True(MatchCommandAvroReader.TryReadCreateMatch(envelope, out CreateMatchInput input));
        Assert.Equal("external", input.Source);
        Assert.Equal("u1", input.CreatedBy!.UserId);
        Assert.Equal("8/8/8/8/8/8/8/8 w - - 0 1", input.StartFen);
        Assert.Equal("lichess", input.ExternalProvider);
        Assert.Equal("abc123", input.ExternalRef);
    }

    [Fact]
    public void Avro_NonCreateMatchPayload_ReturnsFalse()
    {
        // An envelope whose payload record is not named CreateMatchCommand.
        RecordSchema otherSchema = (RecordSchema)Schema.Parse("""
            { "type": "record", "name": "MatchCommand", "namespace": "x",
              "fields": [
                { "name": "aggregate_id", "type": "string" },
                { "name": "payload", "type": {
                  "type": "record", "name": "ResignCommand",
                  "fields": [{ "name": "by_user_id", "type": "string" }] } } ] }
            """);
        GenericRecord payload = new((RecordSchema)otherSchema.Fields.Single(f => f.Name == "payload").Schema);
        payload.Add("by_user_id", "u1");
        GenericRecord envelope = new(otherSchema);
        envelope.Add("aggregate_id", "m1");
        envelope.Add("payload", payload);

        Assert.False(MatchCommandAvroReader.TryReadCreateMatch(envelope, out CreateMatchInput input));
        Assert.Null(input);
    }

    // ---- Discriminator: ConfluentFraming ----

    [Fact]
    public void ConfluentFraming_ReadsBigEndianSchemaId()
    {
        byte[] framed = [0x00, 0x00, 0x00, 0x01, 0x2C, 0xDE, 0xAD];
        Assert.Equal(300, ConfluentFraming.TryReadSchemaId(framed));
    }

    [Fact]
    public void ConfluentFraming_RejectsWrongMagicByte()
    {
        byte[] notFramed = [0x01, 0x00, 0x00, 0x00, 0x05];
        Assert.Null(ConfluentFraming.TryReadSchemaId(notFramed));
    }

    [Fact]
    public void ConfluentFraming_RejectsTooShort()
    {
        byte[] tooShort = [0x00, 0x00, 0x00];
        Assert.Null(ConfluentFraming.TryReadSchemaId(tooShort));
    }
}
