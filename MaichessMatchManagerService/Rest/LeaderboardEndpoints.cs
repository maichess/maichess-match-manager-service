using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using MaichessMatchManagerService.Services;
using Microsoft.AspNetCore.Mvc;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal static class LeaderboardEndpoints
{
    internal static IEndpointRouteBuilder MapLeaderboardEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/leaderboard").RequireAuthorization();
        group.MapGet(string.Empty, GetTop);
        group.MapGet("/rank/{userId}", GetRank);
        return routes;
    }

    private static async Task<IResult> GetTop(
        [FromQuery] int limit,
        ClaimsPrincipal principal,
        LeaderboardService service,
        CancellationToken ct)
    {
        if (!IsAuthenticated(principal))
        {
            return Results.Unauthorized();
        }

        LeaderboardPage page = await service.GetTopAsync(limit, ct);
        IReadOnlyList<LeaderboardEntryResponse> entries = [.. page.Rows.Select(ToEntryResponse)];
        return Results.Ok(new LeaderboardResponse(entries, page.Total));
    }

    private static async Task<IResult> GetRank(
        string userId,
        ClaimsPrincipal principal,
        LeaderboardService service,
        CancellationToken ct)
    {
        if (!IsAuthenticated(principal))
        {
            return Results.Unauthorized();
        }

        (LeaderboardRow Row, long Total)? ranking = await service.GetRankAsync(userId, ct);
        return ranking is null
            ? Results.NotFound()
            : Results.Ok(new LeaderboardRankResponse(ToEntryResponse(ranking.Value.Row), ranking.Value.Total));
    }

    private static LeaderboardEntryResponse ToEntryResponse(LeaderboardRow row) =>
        new(row.Rank, row.UserId, row.Username, row.Elo, row.RatingDeviation, row.Provisional);

    private static bool IsAuthenticated(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier) is not null;
}
