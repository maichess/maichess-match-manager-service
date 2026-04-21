using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

internal sealed record SubmitMoveRequest(
    [property: JsonPropertyName("move")] string Move);
