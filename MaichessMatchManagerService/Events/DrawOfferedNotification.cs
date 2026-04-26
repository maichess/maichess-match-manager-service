using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

internal sealed record DrawOfferedNotification(PlayerDocument Player) : MatchNotification;
