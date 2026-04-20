namespace MaichessMatchManagerService.Services;

internal sealed class MatchNotFoundException(string matchId)
    : Exception($"Match {matchId} not found");

internal sealed class MatchAlreadyEndedException()
    : Exception("Match has already ended");

internal sealed class NotParticipantException()
    : Exception("Not a participant in this match");

internal sealed class NotYourTurnException()
    : Exception("Not your turn");

internal sealed class IllegalMoveException(string reason)
    : Exception(reason)
{
    internal string Reason { get; } = reason;
}

internal sealed class AnalysisNotPermittedException()
    : Exception("Match is not analyzable");

internal sealed class PositionIndexOutOfRangeException()
    : Exception("Position index is out of range");
