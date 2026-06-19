using MaichessMatchManagerService.Entities;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// The document model carries behaviour in its field defaults (a legacy/native match
// deserialised without these fields must read back as native, no external metadata)
// and in the identity predicates the player-mapping code branches on.
public sealed class EntityDefaultsTests
{
    private const string Fen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    [Fact]
    public void MatchDocument_OmittedSourceFields_DefaultToNativeAndEmpty()
    {
        MatchDocument doc = new()
        {
            Id = "m1",
            White = new PlayerDocument { UserId = "w" },
            Black = new PlayerDocument { UserId = "b" },
            CurrentFen = Fen,
            Status = "ongoing",
            TimeFormat = new TimeFormatDocument { Id = "5+0", BaseMs = 300_000, IncrementMs = 0, Category = "blitz" },
        };

        Assert.Equal("native", doc.Source);
        Assert.Equal(string.Empty, doc.ExternalProvider);
        Assert.Equal(string.Empty, doc.ExternalRef);
    }

    [Theory]
    [InlineData("bot-1", false)]
    [InlineData(null, true)]
    public void PlayerDocument_IsBot_TracksBotId(string? botId, bool expectedNotBot)
    {
        PlayerDocument player = new() { BotId = botId };

        Assert.Equal(!expectedNotBot, player.IsBot);
    }

    [Fact]
    public void PlayerDocument_IsExternal_IsTrueOnlyWhenExternalNameSet()
    {
        Assert.True(new PlayerDocument { ExternalName = "Magnus" }.IsExternal);
        Assert.False(new PlayerDocument { UserId = "u1" }.IsExternal);
        Assert.False(new PlayerDocument { BotId = "bot-1" }.IsExternal);
    }
}
