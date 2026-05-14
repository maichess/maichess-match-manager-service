namespace MaichessMatchManagerService.Entities;

internal sealed class MatchDocument
{
    public required string Id { get; set; }

    public required PlayerDocument White { get; set; }

    public required PlayerDocument Black { get; set; }

    public required string CurrentFen { get; set; }

    public required string Status { get; set; }

    public List<string> Moves { get; set; } = [];

    // FenHistory[0] is the starting position; FenHistory[N] is the position after move N.
    // Always has Moves.Count + 1 entries.
    public List<string> FenHistory { get; set; } = [];

    // Opaque list owned by the move validator. Passed to ValidateMoveRequest and
    // replaced with ValidateMoveResponse.PositionHistory on each valid move.
    // Cleared when the game ends. Never modified by match manager logic.
    public List<string> PositionHistory { get; set; } = [];

    public required TimeFormatDocument TimeFormat { get; set; }

    public long WhiteTimeMs { get; set; }

    public long BlackTimeMs { get; set; }

    // Timestamp of when the last move was made; used to compute elapsed clock time.
    public DateTimeOffset LastMoveAt { get; set; }

    // UserId of the player who offered a draw, or null when no offer is pending.
    public string? PendingDrawOffererUserId { get; set; }
}
