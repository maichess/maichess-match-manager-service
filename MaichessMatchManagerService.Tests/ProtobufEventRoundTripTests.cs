using Google.Protobuf;
using Maichess.Events.V1;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Round-trips the maichess.events.v1 generated proto types (encode -> decode) for
// the envelopes and every payload variant on the topics Match Manager handles:
// socket.outbound, match.commands, match.events, user.events. This proves the
// proto schemas (and the ScalaPB/ts-proto/csharp codegen behind them) faithfully
// carry the same field set the Avro .avsc did, before any producer/consumer is
// switched to the Protobuf serde.
public sealed class ProtobufEventRoundTripTests
{
    private static void AssertRoundTrips<T>(T original, MessageParser<T> parser)
        where T : IMessage<T>
    {
        byte[] bytes = original.ToByteArray();
        T parsed = parser.ParseFrom(bytes);
        Assert.Equal(original, parsed);
    }

    private static Player UserPlayer(string id) => new() { UserId = id };

    private static TimeFormat SampleTimeFormat() => new()
    {
        Id = "3+2",
        BaseMs = 180_000,
        IncrementMs = 2_000,
        Category = "blitz",
    };

    [Fact]
    public void SocketOutbound_BothTargets_RoundTrip()
    {
        OutboundEvent toUser = new()
        {
            EventId = "e1",
            EventType = "socket.matched",
            AggregateId = "user-1",
            Sequence = 0,
            OccurredAt = 1_700_000_000_000,
            CorrelationId = string.Empty,
            CausationId = string.Empty,
            Producer = "match-manager-service",
            Push = new SocketPush
            {
                TargetUserId = "user-1",
                EventName = "matched",
                PayloadJson = "{\"match_id\":\"m1\"}",
            },
        };
        OutboundEvent toMatch = new(toUser)
        {
            Push = new SocketPush
            {
                TargetMatchId = "m1",
                EventName = "move_made",
                PayloadJson = "{\"move\":\"e2e4\"}",
            },
        };

        AssertRoundTrips(toUser, OutboundEvent.Parser);
        AssertRoundTrips(toMatch, OutboundEvent.Parser);
        Assert.Equal(SocketPush.TargetOneofCase.TargetUserId, toUser.Push.TargetCase);
        Assert.Equal(SocketPush.TargetOneofCase.TargetMatchId, toMatch.Push.TargetCase);
    }

    public static IEnumerable<object[]> MatchCommandPayloads()
    {
        yield return [new MatchCommand
        {
            CreateMatch = new CreateMatchCommand
            {
                White = UserPlayer("w"),
                Black = new Player { BotId = "bot-1" },
                TimeFormat = SampleTimeFormat(),
                CreatedBy = UserPlayer("w"),
                StartFen = string.Empty,
                Source = MatchSource.Native,
                ExternalProvider = string.Empty,
                ExternalRef = string.Empty,
            },
        }];
        yield return [new MatchCommand { SubmitMove = new SubmitMoveCommand { ByUserId = "w", MoveUci = "e2e4" } }];
        yield return [new MatchCommand { Resign = new ResignCommand { ByUserId = "w" } }];
        yield return [new MatchCommand { OfferDraw = new OfferDrawCommand { ByUserId = "w" } }];
        yield return [new MatchCommand { AcceptDraw = new AcceptDrawCommand { ByUserId = "b" } }];
        yield return [new MatchCommand { DeclineDraw = new DeclineDrawCommand { ByUserId = "b" } }];
        yield return [new MatchCommand
        {
            SyncExternal = new SyncExternalCommand
            {
                CurrentFen = "fen",
                Moves = { "e2e4", "e7e5" },
                Status = MatchStatus.Ongoing,
                WhiteTimeMs = 1000,
                BlackTimeMs = 900,
                FinishedAtMs = 0,
                EndReason = EndReason.Unspecified,
            },
        }];
    }

    [Theory]
    [MemberData(nameof(MatchCommandPayloads))]
    public void MatchCommand_EveryPayload_RoundTrips(MatchCommand payload)
    {
        MatchCommand envelope = new(payload)
        {
            EventId = "c1",
            EventType = "match.command",
            AggregateId = "m1",
            Sequence = 1,
            OccurredAt = 1_700_000_000_000,
            Producer = "match-maker-service",
        };

        AssertRoundTrips(envelope, MatchCommand.Parser);
        Assert.NotEqual(MatchCommand.PayloadOneofCase.None, envelope.PayloadCase);
    }

    public static IEnumerable<object[]> MatchEventPayloads()
    {
        yield return [new MatchEvent
        {
            MatchCreated = new MatchCreated
            {
                White = UserPlayer("w"),
                Black = UserPlayer("b"),
                TimeFormat = SampleTimeFormat(),
                CreatedBy = UserPlayer("w"),
                StartFen = string.Empty,
                Source = MatchSource.Native,
            },
        }];
        yield return [new MatchEvent
        {
            MoveSubmitted = new MoveSubmitted
            {
                MoveUci = "e2e4",
                By = UserPlayer("w"),
                Fen = "fen",
                PositionHistory = { "fen0", "fen1" },
            },
        }];
        yield return [new MatchEvent
        {
            MoveValidated = new MoveValidated
            {
                ResultingFen = "fen2",
                GameResult = GameResult.Unspecified,
                PositionHistory = { "fen0", "fen1", "fen2" },
            },
        }];
        yield return [new MatchEvent { MoveRejected = new MoveRejected { MoveUci = "e2e5", Reason = "illegal" } }];
        yield return [new MatchEvent
        {
            MoveApplied = new MoveApplied
            {
                MoveUci = "e2e4",
                ResultingFen = "fen2",
                Index = 0,
                Player = UserPlayer("w"),
                WhiteTimeMs = 179_000,
                BlackTimeMs = 180_000,
                AppliedAtMs = 1_700_000_000_500,
            },
        }];
        // optional time_limit_ms set
        yield return [new MatchEvent
        {
            BotMoveRequested = new BotMoveRequested { Fen = "fen", BotId = "bot-1", TimeLimitMs = 1000, RequestId = "r1" },
        }];
        // optional time_limit_ms unset (presence must survive the round-trip as "not set")
        yield return [new MatchEvent
        {
            BotMoveRequested = new BotMoveRequested { Fen = "fen", BotId = "bot-1", RequestId = "r2" },
        }];
        yield return [new MatchEvent
        {
            BotMoveCalculated = new BotMoveCalculated { MoveUci = "e7e5", EvaluationCp = -15, RequestId = "r1" },
        }];
        yield return [new MatchEvent { DrawOffered = new DrawOffered { By = UserPlayer("w") } }];
        yield return [new MatchEvent { DrawDeclined = new DrawDeclined { By = UserPlayer("b") } }];
        yield return [new MatchEvent
        {
            MatchEnded = new MatchEnded
            {
                Status = MatchStatus.WhiteWon,
                EndReason = EndReason.Checkmate,
                FinishedAtMs = 1_700_000_001_000,
            },
        }];
    }

    [Theory]
    [MemberData(nameof(MatchEventPayloads))]
    public void MatchEvent_EveryPayload_RoundTrips(MatchEvent payload)
    {
        MatchEvent envelope = new(payload)
        {
            EventId = "ev1",
            EventType = "match.event",
            AggregateId = "m1",
            Sequence = 2,
            OccurredAt = 1_700_000_000_000,
            Producer = "match-manager-service",
        };

        AssertRoundTrips(envelope, MatchEvent.Parser);
        Assert.NotEqual(MatchEvent.PayloadOneofCase.None, envelope.PayloadCase);
    }

    [Fact]
    public void BotMoveRequested_OptionalPresence_IsDistinguished()
    {
        BotMoveRequested withLimit = new() { Fen = "f", BotId = "b", TimeLimitMs = 1000, RequestId = "r" };
        BotMoveRequested withoutLimit = new() { Fen = "f", BotId = "b", RequestId = "r" };

        BotMoveRequested parsedWith = BotMoveRequested.Parser.ParseFrom(withLimit.ToByteArray());
        BotMoveRequested parsedWithout = BotMoveRequested.Parser.ParseFrom(withoutLimit.ToByteArray());

        Assert.True(parsedWith.HasTimeLimitMs);
        Assert.False(parsedWithout.HasTimeLimitMs);
    }

    public static IEnumerable<object[]> UserEventPayloads()
    {
        yield return [new UserEvent { UserRegistered = new UserRegistered { UserId = "u", Username = "alice" } }];
        yield return [new UserEvent { ProfileUpdated = new ProfileUpdated { UserId = "u", Username = "alice", DevMode = true } }];
        yield return [new UserEvent
        {
            MatchResultRecorded = new MatchResultRecorded
            {
                UserId = "u",
                Outcome = MatchOutcome.Win,
                OpponentRating = 1450.5,
                OpponentRd = 60.0,
            },
        }];
        yield return [new UserEvent
        {
            RatingUpdated = new RatingUpdated
            {
                UserId = "u",
                Rating = 1500.25,
                RatingDeviation = 50.0,
                Volatility = 0.06,
                Elo = 1500,
                Wins = 10,
                Losses = 3,
                Draws = 2,
            },
        }];
    }

    [Theory]
    [MemberData(nameof(UserEventPayloads))]
    public void UserEvent_EveryPayload_RoundTrips(UserEvent payload)
    {
        UserEvent envelope = new(payload)
        {
            EventId = "u1",
            EventType = "user.event",
            AggregateId = "u",
            Sequence = 3,
            OccurredAt = 1_700_000_000_000,
            Producer = "user-service",
        };

        AssertRoundTrips(envelope, UserEvent.Parser);
        Assert.NotEqual(UserEvent.PayloadOneofCase.None, envelope.PayloadCase);
    }
}
