using Grpc.Core;
using Maichess.Engine.V1;
using Maichess.MoveValidator.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;
using MaichessMatchManagerService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

using SocketSvc = Socket.V1.Socket;

namespace MaichessMatchManagerService.Tests.Support;

internal sealed class MatchServiceContext
{
    internal IMatchRepository Repository { get; } = Substitute.For<IMatchRepository>();

    internal Moves.MovesClient MoveValidator { get; } = Substitute.For<Moves.MovesClient>();

    internal Bots.BotsClient Engine { get; } = Substitute.For<Bots.BotsClient>();

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
            MoveValidator,
            Engine,
            SocketNotifier,
            NullLogger<MatchService>.Instance);

        Repository.InsertAsync(Arg.Any<MatchDocument>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<MatchDocument>()));
        Repository.ReplaceAsync(Arg.Any<MatchDocument>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    internal void SetupMatch(MatchDocument match)
    {
        CurrentMatch = match;
        Repository.GetByIdAsync(match.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MatchDocument?>(match));
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

    internal void SetupMoveValidatorAcceptsWithGameResult(string move, string resultingFen, GameResult gameResult)
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
