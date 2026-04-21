using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

internal sealed record PlayerResponse(
    [property: JsonPropertyName("user_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? UserId,
    [property: JsonPropertyName("username"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Username,
    [property: JsonPropertyName("bot_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? BotId,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name);
