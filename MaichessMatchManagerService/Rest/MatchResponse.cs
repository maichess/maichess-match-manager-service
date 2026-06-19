using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MaichessMatchManagerService.Rest;

[ExcludeFromCodeCoverage]
internal sealed record MatchResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("white")] PlayerResponse White,
    [property: JsonPropertyName("black")] PlayerResponse Black,
    [property: JsonPropertyName("current_fen")] string CurrentFen,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("moves")] IReadOnlyList<string> Moves,
    [property: JsonPropertyName("time_format")] TimeFormatResponse TimeFormat,
    [property: JsonPropertyName("white_time_ms")] long WhiteTimeMs,
    [property: JsonPropertyName("black_time_ms")] long BlackTimeMs,
    [property: JsonPropertyName("last_move_at_ms")] long LastMoveAtMs,
    [property: JsonPropertyName("analyzable")] bool Analyzable,
    [property: JsonPropertyName("clock_history")] IReadOnlyList<ClockSnapshotResponse> ClockHistory);
