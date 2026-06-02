using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Maichess.Engine.V1;
using Maichess.User.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;
using Microsoft.AspNetCore.Mvc;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal static class MatchesEndpoints
{
    private static readonly string[] KnownCategories = ["bullet", "blitz", "rapid", "classical"];

    internal static IEndpointRouteBuilder MapMatchesEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/matches").RequireAuthorization();

        group.MapGet(string.Empty, ListMatches);
        group.MapGet("/{id}", GetMatch);
        group.MapGet("/{id}/positions/{index}", GetPosition);
        group.MapGet("/{id}/legal-moves", GetLegalMoves);
        group.MapPost("/{id}/moves", PostMove);
        group.MapPost("/{id}/resign", PostResign);
        group.MapPost("/{id}/draw-offer", OfferDraw);
        group.MapDelete("/{id}/draw-offer", DeclineDraw);
        group.MapPost("/{id}/draw-offer/accept", AcceptDraw);

        routes.MapGet("/users/{userId}/matches", ListUserMatches).RequireAuthorization();

        return routes;
    }

    private static async Task<IResult> ListUserMatches(
        string userId,
        [FromQuery] string? status,
        [FromQuery] int page,
        [FromQuery] int page_size,
        ClaimsPrincipal principal,
        MatchService matchService,
        Users.UsersClient usersClient,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out string? authUserId))
        {
            return Results.Unauthorized();
        }

        if (userId != authUserId)
        {
            return Results.Forbid();
        }

        string normalizedStatus = status ?? "ended";
        if (normalizedStatus != "ended" && normalizedStatus != "ongoing")
        {
            return Results.BadRequest(new ErrorResponse("unsupported status"));
        }

        (IReadOnlyList<MatchDocument> matches, int total) = await matchService.ListUserMatchesAsync(
            userId, normalizedStatus, page, page_size, ct);

        int normalizedPage = page < 1 ? 1 : page;
        int normalizedSize = page_size <= 0 ? 20 : Math.Min(page_size, 100);

        List<MatchSummaryResponse> summaries = [];
        foreach (MatchDocument match in matches)
        {
            summaries.Add(await ToMatchSummaryAsync(match, usersClient, botsClient, ct));
        }

        return Results.Ok(new MatchListResponse(summaries, total, normalizedPage, normalizedSize));
    }

    private static async Task<IResult> ListMatches(
        [FromQuery] string? status,
        [FromQuery] string? category,
        [FromQuery] int page,
        [FromQuery] int page_size,
        ClaimsPrincipal principal,
        MatchService matchService,
        Users.UsersClient usersClient,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out _))
        {
            return Results.Unauthorized();
        }

        string normalizedStatus = status ?? "ongoing";
        if (normalizedStatus != "ongoing")
        {
            return Results.BadRequest(new ErrorResponse("unsupported status"));
        }

        if (!string.IsNullOrEmpty(category) && !KnownCategories.Contains(category))
        {
            return Results.BadRequest(new ErrorResponse("unsupported category"));
        }

        (IReadOnlyList<MatchDocument> matches, int total) = await matchService.ListMatchesAsync(
            normalizedStatus, category, page, page_size, ct);

        int normalizedPage = page < 1 ? 1 : page;
        int normalizedSize = page_size <= 0 ? 20 : Math.Min(page_size, 100);

        List<MatchSummaryResponse> summaries = [];
        foreach (MatchDocument match in matches)
        {
            summaries.Add(await ToMatchSummaryAsync(match, usersClient, botsClient, ct));
        }

        return Results.Ok(new MatchListResponse(summaries, total, normalizedPage, normalizedSize));
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

        // Watch mode: anyone authenticated may view an ongoing match.
        // Finished matches between two humans remain private to the participants.
        bool isParticipant = match.White.UserId == userId || match.Black.UserId == userId;
        bool involvesBot = match.White.IsBot || match.Black.IsBot;
        if (!isParticipant && match.Status != "ongoing" && !involvesBot)
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

    private static async Task<IResult> OfferDraw(
        string id,
        ClaimsPrincipal principal,
        MatchService matchService,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out string? userId))
        {
            return Results.Unauthorized();
        }

        try
        {
            await matchService.OfferDrawAsync(id, userId, ct);
            return Results.Ok();
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
        catch (DrawOfferAlreadyPendingException)
        {
            return Results.Conflict();
        }
    }

    private static async Task<IResult> AcceptDraw(
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
            MatchDocument match = await matchService.AcceptDrawAsync(id, userId, ct);
            MatchResponse response = await ToMatchResponseAsync(match, usersClient, botsClient, ct);
            return Results.Ok(response);
        }
        catch (MatchNotFoundException)
        {
            return Results.NotFound();
        }
        catch (Exception ex) when (ex is MatchAlreadyEndedException or NoDrawOfferPendingException)
        {
            return Results.Conflict();
        }
        catch (Exception ex) when (ex is NotParticipantException or NotDrawRecipientException)
        {
            return Results.Forbid();
        }
    }

    private static async Task<IResult> DeclineDraw(
        string id,
        ClaimsPrincipal principal,
        MatchService matchService,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out string? userId))
        {
            return Results.Unauthorized();
        }

        try
        {
            await matchService.DeclineDrawAsync(id, userId, ct);
            return Results.Ok();
        }
        catch (MatchNotFoundException)
        {
            return Results.NotFound();
        }
        catch (Exception ex) when (ex is MatchAlreadyEndedException or NoDrawOfferPendingException)
        {
            return Results.Conflict();
        }
        catch (NotParticipantException)
        {
            return Results.Forbid();
        }
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
            ToTimeFormatResponse(match.TimeFormat),
            match.WhiteTimeMs,
            match.BlackTimeMs,
            match.LastMoveAt.ToUnixTimeMilliseconds(),
            analyzable);
    }

    private static async Task<MatchSummaryResponse> ToMatchSummaryAsync(
        MatchDocument match,
        Users.UsersClient usersClient,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        PlayerResponse white = await ToPlayerResponseAsync(match.White, usersClient, botsClient, ct);
        PlayerResponse black = await ToPlayerResponseAsync(match.Black, usersClient, botsClient, ct);

        PlayerResponse? createdBy = match.CreatedBy is null
            ? null
            : await ToPlayerResponseAsync(match.CreatedBy, usersClient, botsClient, ct);

        return new MatchSummaryResponse(
            match.Id,
            white,
            black,
            match.Status,
            ToTimeFormatResponse(match.TimeFormat),
            match.WhiteTimeMs,
            match.BlackTimeMs,
            match.LastMoveAt.ToUnixTimeMilliseconds(),
            match.FinishedAtMs,
            match.Moves.Count,
            createdBy,
            match.Source,
            match.ExternalProvider,
            match.ExternalRef);
    }

    private static TimeFormatResponse ToTimeFormatResponse(TimeFormatDocument tf) =>
        new(tf.Id, tf.BaseMs, tf.IncrementMs, tf.Category);

    private static async Task<PlayerResponse> ToPlayerResponseAsync(
        PlayerDocument player,
        Users.UsersClient usersClient,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        if (player.UserId is not null)
        {
            Maichess.User.V1.GetUserResponse userResponse = await usersClient.GetUserAsync(
                new Maichess.User.V1.GetUserRequest { UserId = player.UserId },
                cancellationToken: ct);
            return new PlayerResponse(player.UserId, userResponse.User.Username, null, null);
        }

        if (player.ExternalName is not null)
        {
            return new PlayerResponse(null, null, null, null, player.ExternalName);
        }

        ListBotsResponse bots = await botsClient.ListBotsAsync(new ListBotsRequest(), cancellationToken: ct);
        Bot? bot = bots.Bots.FirstOrDefault(b => b.Id == player.BotId);
        string botName = bot?.Name ?? player.BotId ?? string.Empty;
        return new PlayerResponse(null, null, player.BotId, botName);
    }

    private static bool TryGetUserId(
        ClaimsPrincipal principal,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? userId)
    {
        userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return userId is not null;
    }
}
