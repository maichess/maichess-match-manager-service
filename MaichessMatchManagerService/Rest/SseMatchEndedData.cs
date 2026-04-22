using System.Diagnostics.CodeAnalysis;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record SseMatchEndedData(string Status, string Reason);
