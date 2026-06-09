using System.Diagnostics.CodeAnalysis;

namespace MaichessMatchManagerService.Kafka;

// The minimal identity a projected match needs for each side: a human user, a bot,
// or neither (an external participant the read model does not drive). Drives the
// "side to move is a bot" decision and the player stamped on emitted events.
[ExcludeFromCodeCoverage]
internal sealed record PlayerRef(string? UserId, string? BotId);
