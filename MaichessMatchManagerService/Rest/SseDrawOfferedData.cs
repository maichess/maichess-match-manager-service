using System.Diagnostics.CodeAnalysis;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record SseDrawOfferedData(SsePlayerRef Player);
