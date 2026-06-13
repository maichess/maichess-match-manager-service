using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record LeaderboardResponse(
    [property: JsonPropertyName("entries")] IReadOnlyList<LeaderboardEntryResponse> Entries,
    [property: JsonPropertyName("total")] long Total);
