using Grpc.Core;
using Maichess.MatchManager.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;
using MaichessMatchManagerService.Services;
using ProtoMatch = Maichess.MatchManager.V1.Match;
using ProtoMatchEndedEvent = Maichess.MatchManager.V1.MatchEndedEvent;
using ProtoMatchEvent = Maichess.MatchManager.V1.MatchEvent;
using ProtoMoveMadeEvent = Maichess.MatchManager.V1.MoveMadeEvent;

namespace MaichessMatchManagerService.Grpc;

internal sealed class MatchesGrpcService(
    MatchService matchService,
    MatchEventBroadcaster broadcaster) : Matches.MatchesBase
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

    public override async Task StreamMatch(
        StreamMatchRequest request,
        IServerStreamWriter<ProtoMatchEvent> responseStream,
        ServerCallContext context)
    {
        try
        {
            _ = await matchService.GetMatchAsync(request.MatchId, context.CancellationToken);
        }
        catch (MatchNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }

        (Guid subscriptionId, System.Threading.Channels.Channel<MatchNotification> channel) =
            broadcaster.Subscribe(request.MatchId);

        try
        {
            await foreach (MatchNotification notification in
                channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                ProtoMatchEvent protoEvent = ToProtoEvent(notification);
                await responseStream.WriteAsync(protoEvent, context.CancellationToken);
            }
        }
        finally
        {
            broadcaster.Unsubscribe(request.MatchId, subscriptionId);
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

    private static ProtoMatchEvent ToProtoEvent(MatchNotification notification) =>
        notification switch
        {
            MoveMadeNotification m => new ProtoMatchEvent
            {
                MoveMade = new ProtoMoveMadeEvent
                {
                    Move = m.Move,
                    ResultingFen = m.ResultingFen,
                    Index = m.Index,
                    Player = ToProtoPlayer(m.Player),
                    WhiteTimeMs = m.WhiteTimeMs,
                    BlackTimeMs = m.BlackTimeMs,
                },
            },
            MatchEndedNotification e => new ProtoMatchEvent
            {
                MatchEnded = new ProtoMatchEndedEvent
                {
                    Status = ToProtoStatus(e.Status),
                    Reason = ToProtoEndReason(e.Reason),
                },
            },
            _ => throw new InvalidOperationException($"Unknown notification type: {notification.GetType().Name}"),
        };

    private static EndReason ToProtoEndReason(string reason) => reason switch
    {
        "checkmate" => EndReason.Checkmate,
        "resignation" => EndReason.Resignation,
        "stalemate" => EndReason.Stalemate,
        "timeout" => EndReason.Timeout,
        "draw_agreement" => EndReason.DrawAgreement,
        _ => EndReason.Unspecified,
    };
}
