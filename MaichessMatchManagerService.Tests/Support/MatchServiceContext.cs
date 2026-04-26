using Grpc.Core;
using Maichess.Engine.V1;
using Maichess.MoveValidator.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;
using MaichessMatchManagerService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MaichessMatchManagerService.Tests.Support;

internal sealed class MatchServiceContext
{
    internal IMatchRepository Repository { get; } = Substitute.For<IMatchRepository>();

    internal Moves.MovesClient MoveValidator { get; } = Substitute.For<Moves.MovesClient>();

    internal Bots.BotsClient Engine { get; } = Substitute.For<Bots.BotsClient>();

    internal MatchEventBroadcaster Broadcaster { get; } = new MatchEventBroadcaster();

    internal MatchService MatchService { get; }

    internal MatchDocument? CurrentMatch { get; set; }

    internal Exception? LastException { get; set; }

    internal MatchDocument? LastMatchResult { get; set; }

    internal (string Fen, string Move, bool IsCurrent)? LastPosition { get; set; }

    internal bool? LastIsAnalyzable { get; set; }

    internal IReadOnlyList<string>? LastLegalMovesResult { get; set; }

    internal MatchServiceContext()
    {
        MatchService = new MatchService(
            Repository,
            MoveValidator,
            Engine,
            Broadcaster,
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

    internal static MatchDocument BuildHumanMatch(
        string matchId,
        string whiteUserId,
        string blackUserId,
        string status = "ongoing",
        string timeControl = "blitz")
    {
        long timeMs = timeControl switch
        {
            "bullet" => 180_000L,
            "blitz" => 300_000L,
            "rapid" => 600_000L,
            "classical" => 1_800_000L,
            _ => 300_000L,
        };

        return new MatchDocument
        {
            Id = matchId,
            White = new PlayerDocument { UserId = whiteUserId },
            Black = new PlayerDocument { UserId = blackUserId },
            CurrentFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            Status = status,
            TimeControl = timeControl,
            WhiteTimeMs = timeMs,
            BlackTimeMs = timeMs,
            LastMoveAt = DateTimeOffset.UtcNow,
            FenHistory = ["rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"],
        };
    }
}
