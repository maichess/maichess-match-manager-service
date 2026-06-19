using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record ClockSnapshotResponse(
    [property: JsonPropertyName("white_time_ms")] long WhiteTimeMs,
    [property: JsonPropertyName("black_time_ms")] long BlackTimeMs);
