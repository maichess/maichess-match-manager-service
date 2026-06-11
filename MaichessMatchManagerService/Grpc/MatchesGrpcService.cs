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
        PlayerDocument? createdBy = request.CreatedBy is null ? null : ToCreatedByDocument(request.CreatedBy);

        string source = request.Source == MatchSource.External ? "external" : "native";
        string externalProvider = request.ExternalProvider;
        string externalRef = request.ExternalRef;

        try
        {
            MatchDocument match = await matchService.CreateMatchAsync(
                white,
                black,
                timeFormat,
                createdBy,
                request.StartFen,
                source,
                externalProvider,
                externalRef,
                ct: context.CancellationToken);

            return new CreateMatchResponse { Match = ToProtoMatch(match) };
        }
        catch (InvalidStartPositionException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<SyncExternalMatchResponse> SyncExternalMatch(
        SyncExternalMatchRequest request, ServerCallContext context)
    {
        try
        {
            string status = ToStatusString(request.Status);
            string endReason = ToEndReasonString(request.EndReason);

            MatchDocument match = await matchService.SyncExternalMatchAsync(
                request.MatchId,
                request.CurrentFen,
                request.Moves,
                status,
                request.WhiteTimeMs,
                request.BlackTimeMs,
                request.FinishedAtMs,
                endReason,
                context.CancellationToken);

            return new SyncExternalMatchResponse { Match = ToProtoMatch(match) };
        }
        catch (MatchNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
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

    public override async Task<ListUserMatchesResponse> ListUserMatches(
        ListUserMatchesRequest request, ServerCallContext context)
    {
        string status = ToUserStatusString(request.Status);
        (IReadOnlyList<MatchDocument> matches, int total) = await matchService.ListUserMatchesAsync(
            request.UserId,
            status,
            request.Page,
            request.PageSize,
            context.CancellationToken);

        ListUserMatchesResponse response = new()
        {
            Total = total,
            Page = request.Page < 1 ? 1 : request.Page,
            PageSize = request.PageSize <= 0 ? 20 : Math.Min(request.PageSize, 100),
        };
        response.Matches.AddRange(matches.Select(ToProtoMatch));
        return response;
    }

    public override async Task<SearchMatchesResponse> SearchMatches(
        SearchMatchesRequest request, ServerCallContext context)
    {
        string status = ToSearchStatusString(request.Status);
        string source = ToSourceFilterString(request.Source);

        (IReadOnlyList<MatchDocument> matches, int total) = await matchService.SearchMatchesAsync(
            request.PlayerId,
            request.InitiatorId,
            status,
            source,
            request.SinceMs,
            request.UntilMs,
            request.Ascending,
            request.Page,
            request.PageSize,
            context.CancellationToken);

        SearchMatchesResponse response = new()
        {
            Total = total,
            Page = request.Page < 1 ? 1 : request.Page,
            PageSize = request.PageSize <= 0 ? 20 : Math.Min(request.PageSize, 100),
        };
        response.Matches.AddRange(matches.Select(ToProtoMatch));
        return response;
    }

    // Matches.MakeMove / Matches.ResignMatch are retired with the synchronous move path
    // (Kafka task 06): moves are submitted over REST and ride match.events.v1. The RPC
    // definitions are removed from the proto in task 09; until then the base
    // implementations return UNIMPLEMENTED (no in-cluster caller remains).
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
            Player.IdentityOneofCase.ExternalName => new PlayerDocument { ExternalName = player.ExternalName },
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "player identity is required")),
        };

    // created_by is optional; an unset or identity-less value yields no attribution.
    private static PlayerDocument? ToCreatedByDocument(Player player) =>
        player.IdentityCase switch
        {
            Player.IdentityOneofCase.UserId => new PlayerDocument { UserId = player.UserId },
            Player.IdentityOneofCase.BotId => new PlayerDocument { BotId = player.BotId },
            Player.IdentityOneofCase.ExternalName => new PlayerDocument { ExternalName = player.ExternalName },
            _ => null,
        };

    private static string ToUserStatusString(MatchStatusFilter filter) => filter switch
    {
        MatchStatusFilter.Ongoing => "ongoing",
        _ => "ended",
    };

    // Unlike the user-history filter (which defaults UNSPECIFIED to ended), the
    // global browse treats UNSPECIFIED as "any status".
    private static string ToSearchStatusString(MatchStatusFilter filter) => filter switch
    {
        MatchStatusFilter.Ongoing => "ongoing",
        MatchStatusFilter.Ended => "ended",
        _ => "all",
    };

    private static string ToSourceFilterString(MatchSource source) => source switch
    {
        MatchSource.Native => "native",
        MatchSource.External => "external",
        _ => "all",
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

    private static string ToStatusString(MatchStatus status) => status switch
    {
        MatchStatus.Ongoing => "ongoing",
        MatchStatus.WhiteWon => "white_won",
        MatchStatus.BlackWon => "black_won",
        MatchStatus.Draw => "draw",
        _ => "ongoing",
    };

    private static string ToEndReasonString(EndReason reason) => reason switch
    {
        EndReason.Checkmate => "checkmate",
        EndReason.Resignation => "resignation",
        EndReason.Stalemate => "stalemate",
        EndReason.Timeout => "timeout",
        EndReason.DrawAgreement => "draw_agreement",
        EndReason.FiftyMoveRule => "fifty_move_rule",
        EndReason.ThreefoldRepetition => "threefold_repetition",
        EndReason.InsufficientMaterial => "insufficient_material",
        _ => "checkmate",
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
            Source = ToProtoSource(match.Source),
            ExternalProvider = match.ExternalProvider,
            FinishedAtMs = match.FinishedAtMs,
            ExternalRef = match.ExternalRef,
        };
        if (match.CreatedBy is not null)
        {
            proto.CreatedBy = ToProtoPlayer(match.CreatedBy);
        }

        proto.Moves.AddRange(match.Moves);
        return proto;
    }

    private static MatchSource ToProtoSource(string source) => source switch
    {
        "external" => MatchSource.External,
        _ => MatchSource.Native,
    };

    private static Player ToProtoPlayer(PlayerDocument player) =>
        player.UserId is not null
            ? new Player { UserId = player.UserId }
            : player.ExternalName is not null
                ? new Player { ExternalName = player.ExternalName }
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
