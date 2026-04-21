namespace MaichessMatchManagerService.Services;

internal sealed class MatchAlreadyEndedException()
    : Exception("Match has already ended");
