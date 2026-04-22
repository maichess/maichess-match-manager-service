using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record LegalMovesResponse(
    [property: JsonPropertyName("moves")] IReadOnlyList<string> Moves);
