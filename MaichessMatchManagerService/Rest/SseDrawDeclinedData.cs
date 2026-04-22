using System.Diagnostics.CodeAnalysis;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record SseDrawDeclinedData(SsePlayerRef Player);
