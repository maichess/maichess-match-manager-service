namespace MaichessMatchManagerService.Events;

internal sealed record MatchEndedNotification(
    string Status,
    string Reason) : MatchNotification;
