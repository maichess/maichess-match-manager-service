using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Maichess.Engine.V1;
using Maichess.User.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;
using MaichessMatchManagerService.Services;
using Microsoft.AspNetCore.Mvc;

namespace MaichessMatchManagerService.Rest;

internal static class MatchesEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static IEndpointRouteBuilder MapMatchesEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/matches").RequireAuthorization();

        group.MapGet("/{id}", GetMatch);
        group.MapGet("/{id}/positions/{index}", GetPosition);
        group.MapGet("/{id}/legal-moves", GetLegalMoves);
        group.MapPost("/{id}/moves", PostMove);
        group.MapPost("/{id}/resign", PostResign);
        group.MapGet("/{id}/events", GetEvents);

        return routes;
    }

    private static async Task<IResult> GetMatch(
        string id,
        ClaimsPrincipal principal,
        MatchService matchService,
        Users.UsersClient usersClient,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out string? userId))
        {
            return Results.Unauthorized();
        }

        MatchDocument match;
        try
        {
            match = await matchService.GetMatchAsync(id, ct);
        }
        catch (MatchNotFoundException)
        {
            return Results.NotFound();
        }

        bool isParticipant = match.White.UserId == userId || match.Black.UserId == userId;
        if (!isParticipant && match.Status == "ongoing")
        {
            return Results.Forbid();
        }

        MatchResponse response = await ToMatchResponseAsync(match, usersClient, botsClient, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetPosition(
        string id,
        int index,
        ClaimsPrincipal principal,
        MatchService matchService,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out _))
        {
            return Results.Unauthorized();
        }

        try
        {
            (string fen, string move, bool isCurrent) =
                await matchService.GetPositionAsync(id, index, ct);

            return Results.Ok(new PositionResponse(index, fen, move, isCurrent));
        }
        catch (MatchNotFoundException)
        {
            return Results.NotFound();
        }
        catch (AnalysisNotPermittedException)
        {
            return Results.Forbid();
        }
        catch (PositionIndexOutOfRangeException)
        {
            return Results.BadRequest();
        }
    }

    private static async Task<IResult> GetLegalMoves(
        string id,
        [FromQuery] string? from,
        ClaimsPrincipal principal,
        MatchService matchService,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out _))
        {
            return Results.Unauthorized();
        }

        try
        {
            IReadOnlyList<string> moves = await matchService.GetLegalMovesAsync(id, from, ct);
            return Results.Ok(new LegalMovesResponse(moves));
        }
        catch (MatchNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> PostMove(
        string id,
        [FromBody] SubmitMoveRequest body,
        ClaimsPrincipal principal,
        MatchService matchService,
        Users.UsersClient usersClient,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out string? userId))
        {
            return Results.Unauthorized();
        }

        try
        {
            MatchDocument match = await matchService.MakeMoveAsync(id, userId, body.Move, ct);
            MatchResponse response = await ToMatchResponseAsync(match, usersClient, botsClient, ct);
            return Results.Ok(response);
        }
        catch (MatchNotFoundException)
        {
            return Results.NotFound();
        }
        catch (MatchAlreadyEndedException)
        {
            return Results.Conflict();
        }
        catch (Exception ex) when (ex is NotParticipantException or NotYourTurnException)
        {
            return Results.Forbid();
        }
        catch (IllegalMoveException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Reason));
        }
    }

    private static async Task<IResult> PostResign(
        string id,
        ClaimsPrincipal principal,
        MatchService matchService,
        Users.UsersClient usersClient,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out string? userId))
        {
            return Results.Unauthorized();
        }

        try
        {
            MatchDocument match = await matchService.ResignMatchAsync(id, userId, ct);
            MatchResponse response = await ToMatchResponseAsync(match, usersClient, botsClient, ct);
            return Results.Ok(response);
        }
        catch (MatchNotFoundException)
        {
            return Results.NotFound();
        }
        catch (MatchAlreadyEndedException)
        {
            return Results.Conflict();
        }
        catch (NotParticipantException)
        {
            return Results.Forbid();
        }
    }

    private static async Task GetEvents(
        string id,
        ClaimsPrincipal principal,
        MatchService matchService,
        MatchEventBroadcaster broadcaster,
        HttpResponse response,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out _))
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        try
        {
            await matchService.GetMatchAsync(id, ct);
        }
        catch (MatchNotFoundException)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        response.Headers.Append("Content-Type", "text/event-stream");
        response.Headers.Append("Cache-Control", "no-cache");
        response.Headers.Append("Connection", "keep-alive");

        (Guid subscriptionId, Channel<MatchNotification> channel) = broadcaster.Subscribe(id);

        try
        {
            await foreach (MatchNotification notification in channel.Reader.ReadAllAsync(ct))
            {
                (string eventType, string data) = SerializeNotification(notification);
                await response.WriteAsync($"event: {eventType}\ndata: {data}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal SSE teardown.
        }
        finally
        {
            broadcaster.Unsubscribe(id, subscriptionId);
        }
    }

    private static (string EventType, string Data) SerializeNotification(MatchNotification notification) =>
        notification switch
        {
            MoveMadeNotification m => SerializeMoveMade(m),
            MatchEndedNotification e => ("match_ended", JsonSerializer.Serialize(
                new SseMatchEndedData(e.Status, e.Reason), SseJsonOptions)),
            _ => throw new InvalidOperationException(
                $"Unknown notification type: {notification.GetType().Name}"),
        };

    private static (string EventType, string Data) SerializeMoveMade(MoveMadeNotification m)
    {
        SsePlayerRef player = m.Player.UserId is not null
            ? new SsePlayerRef(m.Player.UserId, null)
            : new SsePlayerRef(null, m.Player.BotId);
        SseMoveMadeData data = new(m.Move, m.ResultingFen, m.Index, player, m.WhiteTimeMs, m.BlackTimeMs);
        return ("move_made", JsonSerializer.Serialize(data, SseJsonOptions));
    }

    private static async Task<MatchResponse> ToMatchResponseAsync(
        MatchDocument match,
        Users.UsersClient usersClient,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        PlayerResponse white = await ToPlayerResponseAsync(match.White, usersClient, botsClient, ct);
        PlayerResponse black = await ToPlayerResponseAsync(match.Black, usersClient, botsClient, ct);
        bool analyzable = MatchService.IsAnalyzable(match);

        return new MatchResponse(
            match.Id,
            white,
            black,
            match.CurrentFen,
            match.Status,
            match.Moves,
            match.TimeControl,
            match.WhiteTimeMs,
            match.BlackTimeMs,
            analyzable);
    }

    private static async Task<PlayerResponse> ToPlayerResponseAsync(
        PlayerDocument player,
        Users.UsersClient usersClient,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        if (player.UserId is not null)
        {
            GetUserResponse userResponse = await usersClient.GetUserAsync(
                new GetUserRequest { UserId = player.UserId },
                cancellationToken: ct);
            return new PlayerResponse(player.UserId, userResponse.User.Username, null, null);
        }

        ListBotsResponse bots = await botsClient.ListBotsAsync(new ListBotsRequest(), cancellationToken: ct);
        Bot? bot = bots.Bots.FirstOrDefault(b => b.Id == player.BotId);
        string botName = bot?.Name ?? player.BotId ?? string.Empty;
        return new PlayerResponse(null, null, player.BotId, botName);
    }

    private static bool TryGetUserId(
        ClaimsPrincipal principal,
        [NotNullWhen(true)] out string? userId)
    {
        userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return userId is not null;
    }
}
