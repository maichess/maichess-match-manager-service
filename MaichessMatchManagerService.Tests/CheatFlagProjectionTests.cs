using Maichess.Events.V1;
using MaichessMatchManagerService.Kafka;
using Xunit;

namespace MaichessMatchManagerService.Tests;

public sealed class CheatFlagProjectionTests
{
    private static CheatEvent Envelope(string userId = "u1") =>
        new()
        {
            EventId = "e1",
            EventType = "cheat.PlayerFlagged",
            AggregateId = userId,
            Sequence = 1,
            OccurredAt = 1000,
            Producer = "anticheat-service",
        };

    [Fact]
    public void PlayerFlaggedSetsFlaggedTrue()
    {
        CheatEvent evt = Envelope();
        evt.PlayerFlagged = new PlayerFlagged { UserId = "u1", CaseId = "c1", Score = 0.8 };

        UserReplicaUpsert? upsert = CheatFlagProjection.Project(evt);

        Assert.NotNull(upsert);
        Assert.Equal("u1", upsert.UserId);
        KeyValuePair<string, string> field = Assert.Single(upsert.Fields);
        Assert.Equal("flagged", field.Key);
        Assert.Equal("true", field.Value);
    }

    [Fact]
    public void PlayerUnflaggedSetsFlaggedFalse()
    {
        CheatEvent evt = Envelope();
        evt.PlayerUnflagged = new PlayerUnflagged { UserId = "u1", CaseId = "c1", UnflaggedBy = "dev" };

        UserReplicaUpsert? upsert = CheatFlagProjection.Project(evt);

        Assert.NotNull(upsert);
        KeyValuePair<string, string> field = Assert.Single(upsert.Fields);
        Assert.Equal("flagged", field.Key);
        Assert.Equal("false", field.Value);
    }

    [Fact]
    public void LiveSuspicionNeverTouchesTheFlag()
    {
        // Advisory in-game signal: the anticheat contract forbids setting the
        // persistent flag from it.
        CheatEvent evt = Envelope();
        evt.LiveSuspicionRaised = new LiveSuspicionRaised { UserId = "u1", MatchId = "m1", Ply = 20, Score = 0.9 };

        Assert.Null(CheatFlagProjection.Project(evt));
    }

    [Fact]
    public void PayloadlessAndKeylessEnvelopesProjectToNothing()
    {
        Assert.Null(CheatFlagProjection.Project(Envelope()));

        CheatEvent keyless = Envelope(userId: string.Empty);
        keyless.PlayerFlagged = new PlayerFlagged { UserId = "u1" };
        Assert.Null(CheatFlagProjection.Project(keyless));
    }
}
