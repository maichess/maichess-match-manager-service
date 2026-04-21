using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

internal sealed record PositionResponse(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("fen")] string Fen,
    [property: JsonPropertyName("move")] string Move,
    [property: JsonPropertyName("is_current")] bool IsCurrent);
