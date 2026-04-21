namespace MaichessMatchManagerService.Services;

internal sealed class NotParticipantException()
    : Exception("Not a participant in this match");
