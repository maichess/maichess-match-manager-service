using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record MatchSummaryResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("white")] PlayerResponse White,
    [property: JsonPropertyName("black")] PlayerResponse Black,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("time_format")] TimeFormatResponse TimeFormat,
    [property: JsonPropertyName("white_time_ms")] long WhiteTimeMs,
    [property: JsonPropertyName("black_time_ms")] long BlackTimeMs,
    [property: JsonPropertyName("last_move_at_ms")] long LastMoveAtMs,
    [property: JsonPropertyName("finished_at_ms")] long FinishedAtMs,
    [property: JsonPropertyName("move_count")] int MoveCount,
    [property: JsonPropertyName("created_by")] PlayerResponse? CreatedBy,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("external_provider")] string ExternalProvider);
