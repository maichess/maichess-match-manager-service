using Maichess.Events.V1;
using MaichessMatchManagerService.Kafka;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Unit tests for the pure user.events.v1 -> user:{id} hash projection. Each payload
// type contributes only the fields it carries (a partial Redis upsert); non-state
// payloads and empty envelopes project to nothing.
public sealed class UserReplicaProjectionTests
{
    [Fact]
    public void UserRegistered_ProjectsUsernameOnly()
    {
        UserEvent env = Envelope("user.UserRegistered");
        env.UserRegistered = new UserRegistered { UserId = "u1", Username = "alice" };

        UserReplicaUpsert? upsert = UserReplicaProjection.Project(env);

        Assert.NotNull(upsert);
        Assert.Equal("u1", upsert!.UserId);
        Assert.Equal(new[] { Pair("username", "alice") }, upsert.Fields);
    }

    [Fact]
    public void ProfileUpdated_ProjectsUsernameAndDevMode()
    {
        UserEvent env = Envelope("user.ProfileUpdated");
        env.ProfileUpdated = new ProfileUpdated { UserId = "u1", Username = "bob", DevMode = true };

        UserReplicaUpsert? upsert = UserReplicaProjection.Project(env);

        Assert.Equal(new[] { Pair("username", "bob"), Pair("dev_mode", "true") }, upsert!.Fields);
    }

    [Fact]
    public void ProfileUpdated_DevModeFalse_SerialisesFalse()
    {
        UserEvent env = Envelope("user.ProfileUpdated");
        env.ProfileUpdated = new ProfileUpdated { UserId = "u1", Username = "bob", DevMode = false };

        Assert.Contains(Pair("dev_mode", "false"), UserReplicaProjection.Project(env)!.Fields);
    }

    [Fact]
    public void RatingUpdated_ProjectsAllRatingAndStatFields()
    {
        UserEvent env = Envelope("user.RatingUpdated");
        env.RatingUpdated = new RatingUpdated
        {
            UserId = "u1",
            Rating = 412.5,
            RatingDeviation = 290.0,
            Volatility = 0.06,
            Elo = 412,
            Wins = 3,
            Losses = 1,
            Draws = 2,
        };

        UserReplicaUpsert? upsert = UserReplicaProjection.Project(env);

        Assert.Equal(
            new[]
            {
                Pair("rating", "412.5"),
                Pair("rating_deviation", "290"),
                Pair("volatility", "0.06"),
                Pair("elo", "412"),
                Pair("wins", "3"),
                Pair("losses", "1"),
                Pair("draws", "2"),
            },
            upsert!.Fields);
    }

    [Fact]
    public void RatingUpdated_SerialisesDoublesAtFullPrecision()
    {
        // A double that needs all 17 significant digits round-trips without loss: the
        // replica stores the exact value the rating consumer reads back.
        UserEvent env = Envelope("user.RatingUpdated");
        env.RatingUpdated = new RatingUpdated { UserId = "u1", Volatility = 0.1 + 0.2 };

        Assert.Contains(
            Pair("volatility", "0.30000000000000004"), UserReplicaProjection.Project(env)!.Fields);
    }

    [Fact]
    public void UserRegistered_MissingUsername_ProjectsEmptyString()
    {
        // username left unset on the payload — the projection coalesces it to "".
        UserEvent env = Envelope("user.UserRegistered");
        env.UserRegistered = new UserRegistered { UserId = "u1" };

        Assert.Equal(new[] { Pair("username", string.Empty) }, UserReplicaProjection.Project(env)!.Fields);
    }

    [Fact]
    public void MatchResultRecorded_ProjectsNothing()
    {
        UserEvent env = Envelope("user.MatchResultRecorded");
        env.MatchResultRecorded = new MatchResultRecorded { UserId = "u1", OpponentRating = 1500.0 };

        Assert.Null(UserReplicaProjection.Project(env));
    }

    [Fact]
    public void EmptyAggregateId_ProjectsNothing()
    {
        UserEvent env = Envelope("user.UserRegistered");
        env.AggregateId = string.Empty;
        env.UserRegistered = new UserRegistered { UserId = string.Empty, Username = "alice" };

        Assert.Null(UserReplicaProjection.Project(env));
    }

    [Fact]
    public void NoPayload_ProjectsNothing()
    {
        // A tombstone / payload-less envelope (PayloadCase = None) is not a replica fact.
        Assert.Null(UserReplicaProjection.Project(Envelope("user.Unknown")));
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

    private static KeyValuePair<string, string> Pair(string k, string v) => new(k, v);
}
