using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record LeaderboardRankResponse(
    [property: JsonPropertyName("entry")] LeaderboardEntryResponse Entry,
    [property: JsonPropertyName("total")] long Total);
