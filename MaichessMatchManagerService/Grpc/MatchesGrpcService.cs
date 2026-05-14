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
        TimeFormatDocument timeFormat = ToTimeFormatDocument(request.TimeFormat);

        MatchDocument match = await matchService.CreateMatchAsync(
            white, black, timeFormat, context.CancellationToken);

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

    public override async Task<ListMatchesResponse> ListMatches(
        ListMatchesRequest request, ServerCallContext context)
    {
        string status = ToStatusString(request.Status);
        (IReadOnlyList<MatchDocument> matches, int total) = await matchService.ListMatchesAsync(
            status,
            request.Category,
            request.Page,
            request.PageSize,
            context.CancellationToken);

        ListMatchesResponse response = new()
        {
            Total = total,
            Page = request.Page < 1 ? 1 : request.Page,
            PageSize = request.PageSize <= 0 ? 20 : Math.Min(request.PageSize, 100),
        };
        response.Matches.AddRange(matches.Select(ToProtoMatch));
        return response;
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

    private static TimeFormatDocument ToTimeFormatDocument(TimeFormat tf)
    {
        if (tf is null || string.IsNullOrEmpty(tf.Id))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "time_format is required"));
        }

        // The registry is the source of truth for known presets — callers can't
        // tune base/increment by passing a known id with custom values. For an
        // unknown id we trust whatever the caller supplied (with safe defaults).
        return TimeFormatRegistry.IsKnown(tf.Id)
            ? TimeFormatRegistry.Resolve(tf.Id)
            : new TimeFormatDocument
            {
                Id = tf.Id,
                BaseMs = tf.BaseMs > 0 ? tf.BaseMs : TimeFormatRegistry.Default.BaseMs,
                IncrementMs = tf.IncrementMs,
                Category = string.IsNullOrEmpty(tf.Category) ? TimeFormatRegistry.Default.Category : tf.Category,
            };
    }

    private static string ToStatusString(MatchStatusFilter filter) => filter switch
    {
        MatchStatusFilter.Ongoing => "ongoing",
        _ => "ongoing",
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
            TimeFormat = ToProtoTimeFormat(match.TimeFormat),
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

    private static TimeFormat ToProtoTimeFormat(TimeFormatDocument tf) => new()
    {
        Id = tf.Id,
        BaseMs = tf.BaseMs,
        IncrementMs = tf.IncrementMs,
        Category = tf.Category,
    };
}
