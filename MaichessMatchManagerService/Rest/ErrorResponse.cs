using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

internal sealed record ErrorResponse(
    [property: JsonPropertyName("error")] string Error);
