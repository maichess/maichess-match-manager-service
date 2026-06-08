using Avro;
using Avro.Generic;
using MaichessMatchManagerService.Kafka;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Unit tests for the pure user.events.v1 -> user:{id} hash projection. Each payload
// type contributes only the fields it carries (a partial Redis upsert); non-state
// payloads and malformed envelopes project to nothing.
public sealed class UserReplicaProjectionTests
{
    // Mirrors maichess-api-contracts/events/v1/user.events.v1.avsc (the fields the
    // projection reads). Kept inline so the test pins the contract shape it expects.
    private const string Avsc = """
    {
      "type": "record",
      "name": "UserEvent",
      "namespace": "maichess.events.user",
      "fields": [
        { "name": "event_id", "type": "string" },
        { "name": "event_type", "type": "string" },
        { "name": "aggregate_id", "type": "string" },
        { "name": "sequence", "type": "long", "default": 0 },
        { "name": "occurred_at", "type": "long" },
        { "name": "producer", "type": "string", "default": "" },
        {
          "name": "payload",
          "type": [
            { "type": "record", "name": "UserRegistered",
              "fields": [
                { "name": "user_id", "type": "string" },
                { "name": "username", "type": "string" }
              ] },
            { "type": "record", "name": "ProfileUpdated",
              "fields": [
                { "name": "user_id", "type": "string" },
                { "name": "username", "type": "string" },
                { "name": "dev_mode", "type": "boolean" }
              ] },
            { "type": "record", "name": "MatchResultRecorded",
              "fields": [
                { "name": "user_id", "type": "string" },
                { "name": "opponent_rating", "type": "double" }
              ] },
            { "type": "record", "name": "RatingUpdated",
              "fields": [
                { "name": "user_id", "type": "string" },
                { "name": "rating", "type": "double" },
                { "name": "rating_deviation", "type": "double" },
                { "name": "volatility", "type": "double" },
                { "name": "elo", "type": "int" },
                { "name": "wins", "type": "int", "default": 0 },
                { "name": "losses", "type": "int", "default": 0 },
                { "name": "draws", "type": "int", "default": 0 }
              ] }
          ]
        }
      ]
    }
    """;

    private static readonly RecordSchema EnvelopeSchema = (RecordSchema)Schema.Parse(Avsc);

    [Fact]
    public void UserRegistered_ProjectsUsernameOnly()
    {
        GenericRecord env = Envelope(Payload("UserRegistered", p =>
        {
            p.Add("user_id", "u1");
            p.Add("username", "alice");
        }));

        UserReplicaUpsert? upsert = UserReplicaProjection.Project(env);

        Assert.NotNull(upsert);
        Assert.Equal("u1", upsert!.UserId);
        Assert.Equal(new[] { Pair("username", "alice") }, upsert.Fields);
    }

    [Fact]
    public void ProfileUpdated_ProjectsUsernameAndDevMode()
    {
        GenericRecord env = Envelope(Payload("ProfileUpdated", p =>
        {
            p.Add("user_id", "u1");
            p.Add("username", "bob");
            p.Add("dev_mode", true);
        }));

        UserReplicaUpsert? upsert = UserReplicaProjection.Project(env);

        Assert.Equal(new[] { Pair("username", "bob"), Pair("dev_mode", "true") }, upsert!.Fields);
    }

    [Fact]
    public void ProfileUpdated_DevModeFalse_SerialisesFalse()
    {
        GenericRecord env = Envelope(Payload("ProfileUpdated", p =>
        {
            p.Add("user_id", "u1");
            p.Add("username", "bob");
            p.Add("dev_mode", false);
        }));

        Assert.Contains(Pair("dev_mode", "false"), UserReplicaProjection.Project(env)!.Fields);
    }

    [Fact]
    public void RatingUpdated_ProjectsAllRatingAndStatFields()
    {
        GenericRecord env = Envelope(Payload("RatingUpdated", p =>
        {
            p.Add("user_id", "u1");
            p.Add("rating", 412.5);
            p.Add("rating_deviation", 290.0);
            p.Add("volatility", 0.06);
            p.Add("elo", 412);
            p.Add("wins", 3);
            p.Add("losses", 1);
            p.Add("draws", 2);
        }));

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
    public void UserRegistered_MissingUsername_ProjectsEmptyString()
    {
        // username left unset on the payload — the projection coalesces null to "".
        GenericRecord env = Envelope(Payload("UserRegistered", p => p.Add("user_id", "u1")));

        Assert.Equal(new[] { Pair("username", string.Empty) }, UserReplicaProjection.Project(env)!.Fields);
    }

    [Fact]
    public void MatchResultRecorded_ProjectsNothing()
    {
        GenericRecord env = Envelope(Payload("MatchResultRecorded", p =>
        {
            p.Add("user_id", "u1");
            p.Add("opponent_rating", 1500.0);
        }));

        Assert.Null(UserReplicaProjection.Project(env));
    }

    [Fact]
    public void EmptyAggregateId_ProjectsNothing()
    {
        GenericRecord env = Envelope(Payload("UserRegistered", p =>
        {
            p.Add("user_id", string.Empty);
            p.Add("username", "alice");
        }));
        env.Add("aggregate_id", string.Empty);

        Assert.Null(UserReplicaProjection.Project(env));
    }

    [Fact]
    public void NonStringAggregateId_ProjectsNothing()
    {
        GenericRecord env = Defensive(aggregateId: null, payload: "x");
        Assert.Null(UserReplicaProjection.Project(env));
    }

    [Fact]
    public void NonRecordPayload_ProjectsNothing()
    {
        GenericRecord env = Defensive(aggregateId: "u1", payload: null);
        Assert.Null(UserReplicaProjection.Project(env));
    }

    // ── Builders ──────────────────────────────────────────────────────────────

    private static GenericRecord Payload(string name, Action<GenericRecord> fill)
    {
        var union = (UnionSchema)EnvelopeSchema.Fields.Single(f => f.Name == "payload").Schema;
        var schema = (RecordSchema)union.Schemas.Single(s => s.Name == name);
        GenericRecord payload = new(schema);
        fill(payload);
        return payload;
    }

    private static GenericRecord Envelope(GenericRecord payload)
    {
        GenericRecord env = new(EnvelopeSchema);
        env.Add("event_id", "e1");
        env.Add("event_type", "user." + payload.Schema.Name);
        env.Add("aggregate_id", "u1");
        env.Add("sequence", 1L);
        env.Add("occurred_at", 1L);
        env.Add("producer", "user-cdc-relay");
        env.Add("payload", payload);
        return env;
    }

    // A minimal record whose aggregate_id/payload can be null or non-record, to drive
    // the projection's defensive guards (impossible to express against the real union).
    private static GenericRecord Defensive(string? aggregateId, object? payload)
    {
        const string avsc = """
        {
          "type": "record", "name": "Loose", "namespace": "test",
          "fields": [
            { "name": "aggregate_id", "type": ["null", "string"], "default": null },
            { "name": "payload", "type": ["null", "string"], "default": null }
          ]
        }
        """;
        GenericRecord r = new((RecordSchema)Schema.Parse(avsc));
        r.Add("aggregate_id", aggregateId);
        r.Add("payload", payload);
        return r;
    }

    private static KeyValuePair<string, string> Pair(string k, string v) => new(k, v);
}
