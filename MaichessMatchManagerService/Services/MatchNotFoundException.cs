namespace MaichessMatchManagerService.Services;

internal sealed class MatchNotFoundException(string matchId)
    : Exception($"Match {matchId} not found");
