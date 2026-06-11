using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Maichess.Engine.V1;
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
        group.MapGet("/search", SearchMatches);
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
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out string? authUserId))
        {
            return Results.Unauthorized();
        }

        if (MatchService.CanonicalizeUserId(userId) != MatchService.CanonicalizeUserId(authUserId))
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
            summaries.Add(await ToMatchSummaryAsync(match, matchService, botsClient, ct));
        }

        return Results.Ok(new MatchListResponse(summaries, total, normalizedPage, normalizedSize));
    }

    // Global, filterable, chronological browse over every match (Dev "All games").
    // dev_mode gating lives at the client proxy; here a valid bearer token suffices,
    // consistent with the existing public-match access model.
    private static async Task<IResult> SearchMatches(
        [FromQuery] string? player_id,
        [FromQuery] string? initiator_id,
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] long since_ms,
        [FromQuery] long until_ms,
        [FromQuery] bool ascending,
        [FromQuery] int page,
        [FromQuery] int page_size,
        ClaimsPrincipal principal,
        MatchService matchService,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out _))
        {
            return Results.Unauthorized();
        }

        string normalizedStatus = status ?? "all";
        if (normalizedStatus is not ("all" or "ongoing" or "ended"))
        {
            return Results.BadRequest(new ErrorResponse("unsupported status"));
        }

        string normalizedSource = source ?? "all";
        if (normalizedSource is not ("all" or "native" or "external"))
        {
            return Results.BadRequest(new ErrorResponse("unsupported source"));
        }

        (IReadOnlyList<MatchDocument> matches, int total) = await matchService.SearchMatchesAsync(
            player_id,
            initiator_id,
            normalizedStatus,
            normalizedSource,
            since_ms,
            until_ms,
            ascending,
            page,
            page_size,
            ct);

        int normalizedPage = page < 1 ? 1 : page;
        int normalizedSize = page_size <= 0 ? 20 : Math.Min(page_size, 100);

        List<MatchSummaryResponse> summaries = [];
        foreach (MatchDocument match in matches)
        {
            summaries.Add(await ToMatchSummaryAsync(match, matchService, botsClient, ct));
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
            summaries.Add(await ToMatchSummaryAsync(match, matchService, botsClient, ct));
        }

        return Results.Ok(new MatchListResponse(summaries, total, normalizedPage, normalizedSize));
    }

    private static async Task<IResult> GetMatch(
        string id,
        ClaimsPrincipal principal,
        MatchService matchService,
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
            match = await matchService.GetMatchForReadAsync(id, ct);
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

        MatchResponse response = await ToMatchResponseAsync(match, matchService, botsClient, ct);
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

    // Submits a move command and returns 202; the authoritative result (move_made /
    // match_ended, or a rejection) arrives over the socket. Move legality is decided
    // asynchronously by the validator, so an illegal move is accepted here and rejected
    // over the socket rather than returning 400.
    private static async Task<IResult> PostMove(
        string id,
        [FromBody] SubmitMoveRequest body,
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
            await matchService.MakeMoveAsync(id, userId, body.Move, ct);
            return Results.Accepted();
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
    }

    private static async Task<IResult> PostResign(
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
            await matchService.ResignMatchAsync(id, userId, ct);
            return Results.Accepted();
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
            return Results.Accepted();
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
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out string? userId))
        {
            return Results.Unauthorized();
        }

        try
        {
            await matchService.AcceptDrawAsync(id, userId, ct);
            return Results.Accepted();
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
            return Results.Accepted();
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
        MatchService matchService,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        PlayerResponse white = await ToPlayerResponseAsync(match.White, matchService, botsClient, ct);
        PlayerResponse black = await ToPlayerResponseAsync(match.Black, matchService, botsClient, ct);
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
        MatchService matchService,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        PlayerResponse white = await ToPlayerResponseAsync(match.White, matchService, botsClient, ct);
        PlayerResponse black = await ToPlayerResponseAsync(match.Black, matchService, botsClient, ct);

        PlayerResponse? createdBy = match.CreatedBy is null
            ? null
            : await ToPlayerResponseAsync(match.CreatedBy, matchService, botsClient, ct);

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
        MatchService matchService,
        Bots.BotsClient botsClient,
        CancellationToken ct)
    {
        if (player.UserId is not null)
        {
            string username = await matchService.ResolveUsernameAsync(player.UserId, ct);
            return new PlayerResponse(player.UserId, username, null, null);
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
