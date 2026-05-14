using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record MatchListResponse(
    [property: JsonPropertyName("matches")] IReadOnlyList<MatchSummaryResponse> Matches,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("page_size")] int PageSize);
