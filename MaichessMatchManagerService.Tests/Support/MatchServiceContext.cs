using Grpc.Core;
using Maichess.Engine.V1;
using Maichess.Events.V1;
using Maichess.MoveValidator.V1;
using Maichess.User.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;
using MaichessMatchManagerService.Kafka;
using MaichessMatchManagerService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

using SocketSvc = Socket.V1.Socket;

namespace MaichessMatchManagerService.Tests.Support;

internal sealed class MatchServiceContext
{
    internal IMatchRepository Repository { get; } = Substitute.For<IMatchRepository>();

    internal IMatchCache Cache { get; } = Substitute.For<IMatchCache>();

    internal ILiveMatchState LiveState { get; } = Substitute.For<ILiveMatchState>();

    internal Moves.MovesClient MoveValidator { get; } = Substitute.For<Moves.MovesClient>();

    internal Bots.BotsClient Engine { get; } = Substitute.For<Bots.BotsClient>();

    internal Users.UsersClient UserService { get; } = Substitute.For<Users.UsersClient>();

    internal IUserReplica UserReplica { get; } = Substitute.For<IUserReplica>();

    internal IMatchEventProducer EventProducer { get; } = Substitute.For<IMatchEventProducer>();

    // Captures every MatchEvent the command side produces to match.events.v1, in order.
    internal List<MatchEvent> ProducedEvents { get; } = [];

    internal List<RecordMatchResultRequest> RecordedResults { get; } = [];

    private readonly Dictionary<string, (double Rating, double Rd)> _userRatings = [];

    private readonly List<Bot> _bots = [];

    internal SocketNotifier SocketNotifier { get; } =
        new SocketNotifier(Substitute.For<SocketSvc.SocketClient>(), NullLogger<SocketNotifier>.Instance);

    internal MatchService MatchService { get; }

    internal MatchDocument? CurrentMatch { get; set; }

    internal Exception? LastException { get; set; }

    internal MatchDocument? LastMatchResult { get; set; }

    internal (string Fen, string Move, bool IsCurrent)? LastPosition { get; set; }

    internal bool? LastIsAnalyzable { get; set; }

    internal IReadOnlyList<string>? LastLegalMovesResult { get; set; }

    internal (IReadOnlyList<MatchDocument> Matches, int Total)? LastListResult { get; set; }

    internal MatchServiceContext()
    {
        MatchService = new MatchService(
            Repository,
            Cache,
            LiveState,
            UserReplica,
            MoveValidator,
            UserService,
            SocketNotifier,
            EventProducer);

        // Capture produced match-events so command-side tests can assert what was emitted.
        EventProducer.ProduceAsync(Arg.Any<MatchEvent>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ProducedEvents.Add(ci.Arg<MatchEvent>());
                return Task.CompletedTask;
            });

        // Default to a cold live-model miss so GetMatchForReadAsync falls back to the
        // durable document; the overlay path is exercised by SetupLiveState.
        LiveState.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((LiveMatchState?)null);

        // Default to a cold replica miss so resolution falls back to GetUser; the
        // replica-hit paths are exercised by SetupUserReplica.
        UserReplica.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UserReplicaRecord?)null);

        Repository.InsertAsync(Arg.Any<MatchDocument>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<MatchDocument>()));
        Repository.ReplaceAsync(Arg.Any<MatchDocument>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        UserService
            .RecordMatchResultAsync(
                Arg.Any<RecordMatchResultRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                RecordMatchResultRequest request = ci.Arg<RecordMatchResultRequest>();
                RecordedResults.Add(request);
                return GrpcHelper.GrpcCall(new RecordMatchResultResponse());
            });

        UserService
            .GetUserAsync(
                Arg.Any<GetUserRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                string id = ci.Arg<GetUserRequest>().UserId;
                (double rating, double rd) = _userRatings.TryGetValue(id, out (double Rating, double Rd) v)
                    ? v
                    : (1500.0, 200.0);
                return GrpcHelper.GrpcCall(new GetUserResponse
                {
                    User = new Maichess.User.V1.User
                    {
                        Id = id,
                        Username = $"rpc-{id}",
                        Rating = rating,
                        RatingDeviation = rd,
                    },
                });
            });

        Engine
            .ListBotsAsync(
                Arg.Any<ListBotsRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                ListBotsResponse response = new();
                response.Bots.AddRange(_bots);
                return GrpcHelper.GrpcCall(response);
            });
    }

    // Configures the rating/deviation returned by user-service for a given human
    // id. Unconfigured ids fall back to 1500/200.
    internal void SetupUserRating(string userId, double rating, double ratingDeviation)
    {
        _userRatings[userId] = (rating, ratingDeviation);
    }

    // Configures the Redis user replica to return a materialised row for an id, so
    // replica-first resolution serves it without touching the GetUser RPC.
    internal void SetupUserReplica(string userId, UserReplicaRecord record)
    {
        UserReplica.GetAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserReplicaRecord?>(record));
    }

    // Registers a bot in the engine's ListBots response so opponent-rating
    // resolution can read its elo.
    internal void SetupBot(string botId, int elo)
    {
        _bots.Add(new Bot { Id = botId, Elo = elo });
    }

    internal void SetupFindForUser(IEnumerable<MatchDocument> matches)
    {
        Repository.FindForUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MatchDocument>>([.. matches]));
    }

    internal void SetupMatch(MatchDocument match)
    {
        CurrentMatch = match;
        Repository.GetByIdAsync(match.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MatchDocument?>(match));
    }

    // Configures the live read model to return a projection for a match, so
    // GetMatchForReadAsync overlays its volatile fields onto the durable document.
    internal void SetupLiveState(LiveMatchState state)
    {
        LiveState.GetAsync(state.MatchId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LiveMatchState?>(state));
    }

    internal void SetupMoveValidatorAccepts(string move, string resultingFen)
    {
        ValidateMoveResponse response = new()
        {
            Valid = true,
            ResultingFen = resultingFen,
            GameResult = default,
        };

        MoveValidator
            .ValidateMoveAsync(
                Arg.Any<ValidateMoveRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(GrpcHelper.GrpcCall(response));
    }

    internal void SetupMoveValidatorRejects(string reason)
    {
        ValidateMoveResponse response = new()
        {
            Valid = false,
            Reason = reason,
        };

        MoveValidator
            .ValidateMoveAsync(
                Arg.Any<ValidateMoveRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(GrpcHelper.GrpcCall(response));
    }

    internal void SetupMoveValidatorAcceptsWithGameResult(
        string move, string resultingFen, Maichess.MoveValidator.V1.GameResult gameResult)
    {
        ValidateMoveResponse response = new()
        {
            Valid = true,
            ResultingFen = resultingFen,
            GameResult = gameResult,
        };

        MoveValidator
            .ValidateMoveAsync(
                Arg.Any<ValidateMoveRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(GrpcHelper.GrpcCall(response));
    }

    internal void SetupOngoingMatches(IEnumerable<MatchDocument> matches)
    {
        Repository.FindOngoingAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MatchDocument>>([.. matches]));
    }

    internal void SetupListMatches(IEnumerable<MatchDocument> matches, int total)
    {
        Repository.ListAsync(
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<MatchDocument> Matches, int Total)>(([.. matches], total)));
    }

    internal void SetupLegalMovesResponse(IEnumerable<string> moves)
    {
        GetLegalMovesResponse response = new();
        response.Moves.AddRange(moves);

        MoveValidator
            .GetLegalMovesAsync(
                Arg.Any<GetLegalMovesRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(GrpcHelper.GrpcCall(response));
    }

    internal static TimeFormatDocument TimeFormatForCategoryName(string name) => name switch
    {
        "bullet" => TimeFormatRegistry.Resolve("3+0"),
        "blitz" => TimeFormatRegistry.Resolve("5+0"),
        "rapid" => TimeFormatRegistry.Resolve("10+0"),
        "classical" => TimeFormatRegistry.Resolve("30+0"),
        _ when TimeFormatRegistry.IsKnown(name) => TimeFormatRegistry.Resolve(name),
        _ => TimeFormatRegistry.Default,
    };

    internal static MatchDocument BuildMatch(
        string matchId,
        PlayerDocument white,
        PlayerDocument black,
        string status = "ongoing",
        PlayerDocument? createdBy = null,
        long finishedAtMs = 0,
        string timeFormatCategory = "blitz")
    {
        const string initialFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
        TimeFormatDocument tf = TimeFormatForCategoryName(timeFormatCategory);

        return new MatchDocument
        {
            Id = matchId,
            White = white,
            Black = black,
            CurrentFen = initialFen,
            Status = status,
            TimeFormat = tf,
            WhiteTimeMs = tf.BaseMs,
            BlackTimeMs = tf.BaseMs,
            LastMoveAt = DateTimeOffset.UtcNow,
            FenHistory = [initialFen],
            CreatedBy = createdBy,
            FinishedAtMs = finishedAtMs,
        };
    }

    internal static MatchDocument BuildHumanMatch(
        string matchId,
        string whiteUserId,
        string blackUserId,
        string status = "ongoing",
        string timeFormatCategory = "blitz")
    {
        TimeFormatDocument tf = TimeFormatForCategoryName(timeFormatCategory);

        return new MatchDocument
        {
            Id = matchId,
            White = new PlayerDocument { UserId = whiteUserId },
            Black = new PlayerDocument { UserId = blackUserId },
            CurrentFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            Status = status,
            TimeFormat = tf,
            WhiteTimeMs = tf.BaseMs,
            BlackTimeMs = tf.BaseMs,
            LastMoveAt = DateTimeOffset.UtcNow,
            FenHistory = ["rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"],
        };
    }
}
