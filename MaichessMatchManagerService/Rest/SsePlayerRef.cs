using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

internal sealed record SsePlayerRef(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BotId);
