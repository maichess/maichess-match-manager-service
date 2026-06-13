using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record LeaderboardEntryResponse(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("username"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Username,
    [property: JsonPropertyName("elo")] int Elo,
    [property: JsonPropertyName("rating_deviation")] double RatingDeviation,
    [property: JsonPropertyName("provisional")] bool Provisional);
