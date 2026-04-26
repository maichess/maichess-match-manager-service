using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record SsePlayerRef(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BotId);
