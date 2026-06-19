using Maichess.Events.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// The match.commands.v1 consumer parses raw Protobuf (Kafka task 09 removed the
// Schema Registry and the transitional Avro dual-read arm). These tests exercise the
// pure MatchCommandReader that projects a MatchCommand envelope onto CreateMatchInput;
// the consumer's broker glue itself is [ExcludeFromCodeCoverage].
public sealed class MatchCommandReaderTests
{
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
    public void HumanVsBot_MapsAllFields()
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
        Assert.Null(input.White.ExternalName);
        Assert.Equal("bot-7", input.Black.BotId);
        Assert.Null(input.Black.UserId);
        Assert.Null(input.Black.ExternalName);
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
    public void External_MapsSourceCreatedByAndStartFen()
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
    public void UnsetCreatedBy_IsNull()
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
    public void EmptyAggregateId_MapsToNullId()
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
    public void MissingTimeFormat_CoalescesToEmptyDefaults()
    {
        // CreateMatch with no time_format set (proto3 null message): every field
        // falls back to its empty default rather than throwing.
        MatchCommand envelope = ProtoEnvelope(new CreateMatchCommand
        {
            White = new Player { UserId = "w" },
            Black = new Player { UserId = "b" },
            // TimeFormat left unset.
        });

        Assert.True(MatchCommandReader.TryReadCreateMatch(envelope, out CreateMatchInput input));
        Assert.Equal(string.Empty, input.TimeFormat.Id);
        Assert.Equal(0, input.TimeFormat.BaseMs);
        Assert.Equal(0, input.TimeFormat.IncrementMs);
        Assert.Equal(string.Empty, input.TimeFormat.Category);
    }

    [Fact]
    public void NonCreateMatchPayload_ReturnsFalse()
    {
        MatchCommand envelope = new()
        {
            AggregateId = "match-1",
            Resign = new ResignCommand { ByUserId = "u1" },
        };

        Assert.False(MatchCommandReader.TryReadCreateMatch(envelope, out CreateMatchInput input));
        Assert.Null(input);
    }
}
