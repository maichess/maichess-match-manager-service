namespace MaichessMatchManagerService.Entities;

// A per-move clock snapshot: the remaining time for each side immediately after a
// move was applied. ClockHistory[i] is the snapshot after Moves[i], so the array
// runs parallel to Moves (one entry per applied move, no starting-position entry).
internal sealed record ClockSnapshot(long WhiteTimeMs, long BlackTimeMs);
