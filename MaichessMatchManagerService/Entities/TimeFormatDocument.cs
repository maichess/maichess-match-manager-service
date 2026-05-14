namespace MaichessMatchManagerService.Entities;

internal sealed class TimeFormatDocument
{
    public required string Id { get; set; }

    public required long BaseMs { get; set; }

    public required long IncrementMs { get; set; }

    public required string Category { get; set; }
}
