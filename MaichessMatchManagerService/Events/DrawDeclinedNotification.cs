using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

internal sealed record DrawDeclinedNotification(PlayerDocument Player) : MatchNotification;
