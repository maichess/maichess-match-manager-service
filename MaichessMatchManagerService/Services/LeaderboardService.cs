using MaichessMatchManagerService.Data;

namespace MaichessMatchManagerService.Services;

// Read side of the rating leaderboard. Ranked positions come from the Redis ZSET
// (ILeaderboard); each is enriched from the shared user replica (username, elo,
// deviation, flagged) — the same replica the rating consumer maintains. Flagged
// players are hidden from the public list (read at query time, never a separate
// source of truth), and a provisional flag is annotated for still-volatile ratings.
// See caching-and-read-models.md (Part B).
internal sealed class LeaderboardService(ILeaderboard leaderboard, IUserReplica userReplica)
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    // Mirrors the client's PROVISIONAL_RD_THRESHOLD: a rating stays provisional while
    // its Glicko-2 deviation is high (a new or inactive account).
    private const double ProvisionalRdThreshold = 110.0;

    // Over-fetch so a handful of flagged players near the top do not shrink the page
    // below the requested size; flagged players are rare, so a small buffer suffices.
    private const int FlaggedOverFetch = 10;

    internal async Task<LeaderboardPage> GetTopAsync(int limit, CancellationToken ct)
    {
        int size = NormalizeLimit(limit);
        long total = await leaderboard.CountAsync(ct);

        IReadOnlyList<LeaderboardEntry> entries = await leaderboard.TopAsync(size + FlaggedOverFetch, ct);

        List<LeaderboardRow> rows = [];
        int rank = 0;
        foreach (LeaderboardEntry entry in entries)
        {
            rank++;
            UserReplicaRecord? record = await userReplica.GetAsync(entry.UserId, ct);
            if (record?.Flagged == true)
            {
                continue;
            }

            rows.Add(BuildRow(rank, entry, record));
            if (rows.Count >= size)
            {
                break;
            }
        }

        return new LeaderboardPage(rows, total);
    }

    // "Your rank" for a single user: the raw ZSET rank (ZREVRANK + 1) and the user's
    // enriched row, or null if the user is not on the board (no rated game yet).
    internal async Task<(LeaderboardRow Row, long Total)?> GetRankAsync(
        string userId, CancellationToken ct)
    {
        long? rank = await leaderboard.RankAsync(userId, ct);
        double? score = await leaderboard.ScoreAsync(userId, ct);
        if (rank is null || score is null)
        {
            return null;
        }

        UserReplicaRecord? record = await userReplica.GetAsync(userId, ct);
        long total = await leaderboard.CountAsync(ct);
        return (BuildRow((int)(rank.Value + 1), new LeaderboardEntry(userId, score.Value), record), total);
    }

    private static int NormalizeLimit(int limit) =>
        limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

    private static LeaderboardRow BuildRow(int rank, LeaderboardEntry entry, UserReplicaRecord? record)
    {
        int elo = record?.Elo ?? (int)Math.Round(entry.Rating, MidpointRounding.AwayFromZero);

        // An unknown deviation (replica not yet warmed for this user) is treated as
        // provisional rather than asserting a settled rating.
        double deviation = record?.RatingDeviation ?? ProvisionalRdThreshold + 1;
        bool provisional = deviation > ProvisionalRdThreshold;

        return new LeaderboardRow(
            rank,
            entry.UserId,
            record?.Username,
            elo,
            record?.RatingDeviation ?? 0,
            provisional);
    }
}
