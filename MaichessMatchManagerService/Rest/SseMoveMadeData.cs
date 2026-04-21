namespace MaichessMatchManagerService.Rest;

internal sealed record SseMoveMadeData(
    string Move,
    string ResultingFen,
    int Index,
    SsePlayerRef Player,
    long WhiteTimeMs,
    long BlackTimeMs);
