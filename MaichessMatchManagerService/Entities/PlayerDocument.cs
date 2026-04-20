namespace MaichessMatchManagerService.Entities;

internal sealed class PlayerDocument
{
    public string? UserId { get; set; }

    public string? BotId { get; set; }

    internal bool IsBot => BotId is not null;
}
