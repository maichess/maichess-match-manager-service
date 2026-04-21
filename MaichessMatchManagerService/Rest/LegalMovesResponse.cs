using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

internal sealed record LegalMovesResponse(
    [property: JsonPropertyName("moves")] IReadOnlyList<string> Moves);
