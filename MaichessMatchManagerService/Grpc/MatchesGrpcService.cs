using Grpc.Core;
using Maichess.MatchManager.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;
using ProtoMatch = Maichess.MatchManager.V1.Match;

namespace MaichessMatchManagerService.Grpc;

internal sealed class MatchesGrpcService(MatchService matchService) : Matches.MatchesBase
{
    public override async Task<CreateMatchResponse> CreateMatch(
        CreateMatchRequest request, ServerCallContext context)
    {
        PlayerDocument white = ToPlayerDocument(request.White);
        PlayerDocument black = ToPlayerDocument(request.Black);
        string timeControl = ToTimeControlString(request.TimeControl);

        MatchDocument match = await matchService.CreateMatchAsync(
            white, black, timeControl, context.CancellationToken);

        return new CreateMatchResponse { Match = ToProtoMatch(match) };
    }

    public override async Task<GetMatchResponse> GetMatch(
        GetMatchRequest request, ServerCallContext context)
    {
        try
        {
            MatchDocument match = await matchService.GetMatchAsync(
                request.MatchId, context.CancellationToken);
            return new GetMatchResponse { Match = ToProtoMatch(match) };
        }
        catch (MatchNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
    }

    public override async Task<MakeMoveResponse> MakeMove(
        MakeMoveRequest request, ServerCallContext context)
    {
        try
        {
            MatchDocument match = await matchService.MakeMoveAsync(
                request.MatchId, request.UserId, request.Move, context.CancellationToken);
            return new MakeMoveResponse { Match = ToProtoMatch(match) };
        }
        catch (MatchNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (MatchAlreadyEndedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (Exception ex) when (ex is NotParticipantException or NotYourTurnException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Forbidden"));
        }
        catch (IllegalMoveException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Reason));
        }
    }

    public override async Task<ResignMatchResponse> ResignMatch(
        ResignMatchRequest request, ServerCallContext context)
    {
        try
        {
            MatchDocument match = await matchService.ResignMatchAsync(
                request.MatchId, request.UserId, context.CancellationToken);
            return new ResignMatchResponse { Match = ToProtoMatch(match) };
        }
        catch (MatchNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (MatchAlreadyEndedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (NotParticipantException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Forbidden"));
        }
    }

    public override async Task<GetMatchPositionResponse> GetMatchPosition(
        GetMatchPositionRequest request, ServerCallContext context)
    {
        try
        {
            (string fen, string move, bool isCurrent) = await matchService.GetPositionAsync(
                request.MatchId, request.Index, context.CancellationToken);

            return new GetMatchPositionResponse
            {
                Index = request.Index,
                Fen = fen,
                Move = move,
                IsCurrent = isCurrent,
            };
        }
        catch (MatchNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (AnalysisNotPermittedException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (PositionIndexOutOfRangeException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    private static PlayerDocument ToPlayerDocument(Player player) =>
        player.IdentityCase switch
        {
            Player.IdentityOneofCase.UserId => new PlayerDocument { UserId = player.UserId },
            Player.IdentityOneofCase.BotId => new PlayerDocument { BotId = player.BotId },
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "player identity is required")),
        };

    private static string ToTimeControlString(TimeControl tc) => tc switch
    {
        TimeControl.Bullet => "bullet",
        TimeControl.Blitz => "blitz",
        TimeControl.Rapid => "rapid",
        TimeControl.Classical => "classical",
        _ => "blitz",
    };

    private static ProtoMatch ToProtoMatch(MatchDocument match)
    {
        ProtoMatch proto = new()
        {
            Id = match.Id,
            White = ToProtoPlayer(match.White),
            Black = ToProtoPlayer(match.Black),
            CurrentFen = match.CurrentFen,
            Status = ToProtoStatus(match.Status),
            TimeControl = ToProtoTimeControl(match.TimeControl),
            WhiteTimeMs = match.WhiteTimeMs,
            BlackTimeMs = match.BlackTimeMs,
        };
        proto.Moves.AddRange(match.Moves);
        return proto;
    }

    private static Player ToProtoPlayer(PlayerDocument player) =>
        player.UserId is not null
            ? new Player { UserId = player.UserId }
            : new Player { BotId = player.BotId };

    private static MatchStatus ToProtoStatus(string status) => status switch
    {
        "white_won" => MatchStatus.WhiteWon,
        "black_won" => MatchStatus.BlackWon,
        "draw" => MatchStatus.Draw,
        _ => MatchStatus.Ongoing,
    };

    private static TimeControl ToProtoTimeControl(string tc) => tc switch
    {
        "bullet" => TimeControl.Bullet,
        "blitz" => TimeControl.Blitz,
        "rapid" => TimeControl.Rapid,
        "classical" => TimeControl.Classical,
        _ => TimeControl.Blitz,
    };
}
