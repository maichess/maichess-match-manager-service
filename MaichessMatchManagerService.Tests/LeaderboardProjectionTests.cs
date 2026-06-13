using Maichess.Events.V1;
using MaichessMatchManagerService.Kafka;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Unit tests for the pure user.events.v1 -> leaderboard:rating ZSET projection. Only a
// RatingUpdated carries a rating, so every other payload (and the empty envelope)
// projects to nothing.
public sealed class LeaderboardProjectionTests
{
    [Fact]
    public void RatingUpdated_ProjectsUserIdAndRating()
    {
        UserEvent env = Envelope("user.RatingUpdated");
        env.RatingUpdated = new RatingUpdated { UserId = "u1", Rating = 1623.5, Elo = 1624 };

        LeaderboardUpsert? upsert = LeaderboardProjection.Project(env);

        Assert.NotNull(upsert);
        Assert.Equal("u1", upsert!.UserId);
        Assert.Equal(1623.5, upsert.Rating);
    }

    [Fact]
    public void UserRegistered_ProjectsNothing()
    {
        UserEvent env = Envelope("user.UserRegistered");
        env.UserRegistered = new UserRegistered { UserId = "u1", Username = "alice" };

        Assert.Null(LeaderboardProjection.Project(env));
    }

    [Fact]
    public void ProfileUpdated_ProjectsNothing()
    {
        UserEvent env = Envelope("user.ProfileUpdated");
        env.ProfileUpdated = new ProfileUpdated { UserId = "u1", Username = "bob" };

        Assert.Null(LeaderboardProjection.Project(env));
    }

    [Fact]
    public void MatchResultRecorded_ProjectsNothing()
    {
        UserEvent env = Envelope("user.MatchResultRecorded");
        env.MatchResultRecorded = new MatchResultRecorded { UserId = "u1", OpponentRating = 1500.0 };

        Assert.Null(LeaderboardProjection.Project(env));
    }

    [Fact]
    public void EmptyAggregateId_ProjectsNothing()
    {
        UserEvent env = Envelope("user.RatingUpdated");
        env.AggregateId = string.Empty;
        env.RatingUpdated = new RatingUpdated { UserId = string.Empty, Rating = 1500.0 };

        Assert.Null(LeaderboardProjection.Project(env));
    }

    [Fact]
    public void NoPayload_ProjectsNothing()
    {
        Assert.Null(LeaderboardProjection.Project(Envelope("user.Unknown")));
    }

    private static UserEvent Envelope(string eventType) => new()
    {
        EventId = "e1",
        EventType = eventType,
        AggregateId = "u1",
        Sequence = 1L,
        OccurredAt = 1L,
        Producer = "user-cdc-relay",
    };
}
