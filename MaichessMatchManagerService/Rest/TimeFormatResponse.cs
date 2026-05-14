using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record TimeFormatResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("base_ms")] long BaseMs,
    [property: JsonPropertyName("increment_ms")] long IncrementMs,
    [property: JsonPropertyName("category")] string Category);
