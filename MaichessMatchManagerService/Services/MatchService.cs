using System.Diagnostics.CodeAnalysis;
using Maichess.Engine.V1;
using Maichess.Events.V1;
using Maichess.MoveValidator.V1;
using Maichess.User.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;
using MaichessMatchManagerService.Kafka;

namespace MaichessMatchManagerService.Services;

// Command side of the match write path (Kafka task 06). Move/resign/draw intents are
// validated against the Redis live read model and emitted as facts to match.events.v1;
// the authoritative result is carried back to clients by the validator + projector +
// engine loop and the socket fan-out. These methods return as soon as the command is
// produced — the REST adapters answer 202. The pure intent->event decision logic lives
// in Kafka/MatchCommands; this class only loads state and produces.
//
// Reads (GetMatch/ListMatches/legal-moves/positions) and external-match sync stay
// synchronous. Match creation emits MatchCreated so the projector seeds the read model,
// inserts the durable document, and kicks the first bot move.
internal sealed class MatchService(
    IMatchRepository repository,
    IMatchCache cache,
    ILiveMatchState liveState,
    IUserReplica userReplica,
    Moves.MovesClient moveValidatorClient,
    Users.UsersClient userServiceClient,
    Bots.BotsClient engineClient,
    ISocketBroadcaster socketNotifier,
    IMatchEventProducer eventProducer)
{
    private const string InitialFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const string Producer = "match-manager-service";

    internal static bool IsAnalyzable(MatchDocument match) =>
        match.White.IsBot || match.Black.IsBot || match.Status != "ongoing";

    // Identity is a Guid everywhere it is minted, stored, or compared. Normalising
    // to the canonical lowercase hyphenated form makes ownership checks immune to a
    // pure case/format difference between the JWT `sub` and the Guid-normalised
    // id user-service returns. Non-Guid ids (e.g. legacy/test values) pass through
    // unchanged so equality still holds for them.
    internal static string? CanonicalizeUserId(string? userId) =>
        userId is not null && Guid.TryParse(userId, out Guid guid) ? guid.ToString() : userId;

    // Creates a match. Native matches are event-sourced: a MatchCreated fact is emitted
    // to match.events.v1 and the projector materialises the durable document, seeds the
    // live read model, and requests the first bot move when a bot is to move. The
    // returned document is built in-memory for the synchronous gRPC response (the caller
    // already minted everything in it); it is not read back from the store. External
    // matches are read-only mirrors that bypass the event loop, so they keep the direct
    // insert and are driven by SyncExternalMatch.
    internal async Task<MatchDocument> CreateMatchAsync(
        PlayerDocument white,
        PlayerDocument black,
        TimeFormatDocument timeFormat,
        PlayerDocument? createdBy,
        string? startFen,
        string source = "native",
        string externalProvider = "",
        string externalRef = "",
        string? id = null,
        CancellationToken ct = default)
    {
        string fen = NormalizeStartFen(startFen);
        string matchId = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString() : id;
        PlayerDocument? initiator = createdBy ?? DeriveInitiator(white, black);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        MatchDocument doc = new()
        {
            Id = matchId,
            White = white,
            Black = black,
            CurrentFen = fen,
            Status = "ongoing",
            TimeFormat = timeFormat,
            WhiteTimeMs = timeFormat.BaseMs,
            BlackTimeMs = timeFormat.BaseMs,
            LastMoveAt = now,
            FenHistory = [fen],
            CreatedBy = initiator,
            Source = source,
            ExternalProvider = externalProvider,
            ExternalRef = externalRef,
        };

        if (source == "external")
        {
            return await repository.InsertAsync(doc, ct);
        }

        MatchEvent created = Envelope(matchId, "match.MatchCreated", now.ToUnixTimeMilliseconds());
        MatchCreated payload = new()
        {
            White = ToEventPlayer(white),
            Black = ToEventPlayer(black),
            TimeFormat = ToEventTimeFormat(timeFormat),
            StartFen = fen,
            Source = MatchSource.Native,
            ExternalProvider = externalProvider,
            ExternalRef = externalRef,
        };
        if (initiator is not null)
        {
            payload.CreatedBy = ToEventPlayer(initiator);
        }

        // Snapshot the bot sides' engine-configured elo into the created fact so the
        // rating consumer can rate humans against the bot's strength at play time
        // without an engine lookup (kafka task 08). An unknown bot stays unset and
        // the consumer falls back to its unknown-bot rating.
        if (white.IsBot || black.IsBot)
        {
            ListBotsResponse bots = await engineClient.ListBotsAsync(
                new ListBotsRequest(), cancellationToken: ct);
            if (white.IsBot && bots.Bots.FirstOrDefault(b => b.Id == white.BotId) is { } whiteBot)
            {
                payload.WhiteBotElo = whiteBot.Elo;
            }

            if (black.IsBot && bots.Bots.FirstOrDefault(b => b.Id == black.BotId) is { } blackBot)
            {
                payload.BlackBotElo = blackBot.Elo;
            }
        }

        created.MatchCreated = payload;
        await eventProducer.ProduceAsync(created, ct);

        return doc;
    }

    internal async Task<MatchDocument> SyncExternalMatchAsync(
        string matchId,
        string currentFen,
        IReadOnlyList<string> moves,
        string status,
        long whiteTimeMs,
        long blackTimeMs,
        long finishedAtMs,
        string endReason,
        CancellationToken ct)
    {
        MatchDocument match = await GetMatchAsync(matchId, ct);

        if (match.Source != "external")
        {
            throw new InvalidOperationException("SyncExternalMatch is only valid for external matches");
        }

        int previousMoveCount = match.Moves.Count;
        string? lastNewMove = moves.Count > previousMoveCount ? moves[^1] : null;

        match.CurrentFen = currentFen;
        match.Moves = [.. moves];
        match.WhiteTimeMs = whiteTimeMs;
        match.BlackTimeMs = blackTimeMs;
        match.Status = status;
        match.LastMoveAt = DateTimeOffset.UtcNow;

        if (status != "ongoing" && finishedAtMs > 0)
        {
            match.FinishedAtMs = finishedAtMs;
        }

        await repository.ReplaceAsync(match, ct);

        if (lastNewMove is not null)
        {
            bool moverIsWhite = moves.Count % 2 == 1;
            PlayerDocument mover = moverIsWhite ? match.White : match.Black;
            socketNotifier.BroadcastMoveMade(match, lastNewMove, currentFen, moves.Count, mover, whiteTimeMs, blackTimeMs);
        }

        if (status != "ongoing")
        {
            socketNotifier.BroadcastMatchEnded(match, status, endReason);
            await OnMatchEndedAsync(match, ct);
        }

        return match;
    }

    internal async Task<MatchDocument> GetMatchAsync(string matchId, CancellationToken ct)
    {
        // Finished matches are immutable, so a cache hit is authoritative. Ongoing
        // matches are never cached (that is the live read model's job), so they
        // always fall through to match-db.
        MatchDocument? cached = await cache.GetMatchAsync(matchId, ct);
        if (cached is not null)
        {
            return cached;
        }

        MatchDocument match = await repository.GetByIdAsync(matchId, ct)
            ?? throw new MatchNotFoundException(matchId);

        if (IsEnded(match))
        {
            await cache.SetMatchAsync(match, ct);
        }

        return match;
    }

    // The read path for REST live reads: an ongoing match overlays the projector's
    // live read-model fields (current fen, authoritative clocks, last-move time,
    // status) onto the durable document, so a client sees the freshest state for a
    // game in progress. When nothing has been projected yet (cold model), it falls
    // back to the durable document unchanged. Finished matches are immutable and never
    // overlaid. Kept separate from GetMatchAsync so the internal write path never
    // mutates a document with read-model values before persisting it.
    internal async Task<MatchDocument> GetMatchForReadAsync(string matchId, CancellationToken ct)
    {
        MatchDocument match = await GetMatchAsync(matchId, ct);

        if (match.Status != "ongoing")
        {
            return match;
        }

        LiveMatchState? live = await liveState.GetAsync(matchId, ct);
        if (live is null)
        {
            return match;
        }

        match.CurrentFen = live.CurrentFen;
        match.WhiteTimeMs = live.WhiteTimeMs;
        match.BlackTimeMs = live.BlackTimeMs;
        match.LastMoveAt = DateTimeOffset.FromUnixTimeMilliseconds(live.LastMoveAtMs);
        match.Status = live.Status;
        return match;
    }

    internal async Task<(IReadOnlyList<MatchDocument> Matches, int Total)> ListMatchesAsync(
        string status,
        string? category,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        int normalizedPage = page < 1 ? 1 : page;
        int normalizedSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        string? normalizedCategory = string.IsNullOrEmpty(category) ? null : category;
        return await repository.ListAsync(status, normalizedCategory, normalizedPage, normalizedSize, ct);
    }

    internal async Task<(IReadOnlyList<MatchDocument> Matches, int Total)> ListUserMatchesAsync(
        string userId,
        string status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        int normalizedPage = page < 1 ? 1 : page;
        int normalizedSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        // Query and filter on the canonical id form so a user whose id differs
        // only in representation from the stored white/black/created_by values
        // (e.g. the JWT `sub` vs the Guid-normalised `me.id`) still matches.
        string canonicalUserId = CanonicalizeUserId(userId)!;

        // Only the ended page is immutable and therefore cacheable; the ongoing
        // page changes as games start and progress, so it always reads live. The
        // cache key uses the same canonical id as the DB filter (see prompt 08).
        bool isEndedQuery = status != "ongoing";
        const string statusFilter = "ended";

        if (isEndedQuery)
        {
            (IReadOnlyList<MatchDocument> Matches, int Total)? hit =
                await cache.GetUserPageAsync(canonicalUserId, statusFilter, normalizedPage, normalizedSize, ct);
            if (hit is not null)
            {
                return hit.Value;
            }
        }

        IReadOnlyList<MatchDocument> candidates = await repository.FindForUserAsync(canonicalUserId, ct);

        IEnumerable<MatchDocument> filtered = candidates.Where(m => IsForUser(m, canonicalUserId));
        filtered = FilterByStatus(filtered, status);

        (IReadOnlyList<MatchDocument> pageItems, int total) =
            OrderAndPage(filtered, ascending: false, normalizedPage, normalizedSize);

        if (isEndedQuery)
        {
            await cache.SetUserPageAsync(
                canonicalUserId, statusFilter, normalizedPage, normalizedSize, pageItems, total, ct);
        }

        return (pageItems, total);
    }

    // Global, filterable, chronological match browse behind the Dev "All games"
    // browser. The repository returns a candidate set (scoped to the participant
    // or initiator id when supplied, else the whole collection); this applies the
    // authoritative membership, status, source, and time-range filters, then orders
    // by finished_at_ms and pages. Player and initiator filters are ANDed. Reads the
    // durable store directly — a browse list never overlays the live read model
    // (rows link into the viewer, which does the live overlay). Not cached: it is a
    // low-volume cross-user dev query whose result space is too wide to key usefully.
    internal async Task<(IReadOnlyList<MatchDocument> Matches, int Total)> SearchMatchesAsync(
        string? playerId,
        string? initiatorId,
        string status,
        string source,
        long sinceMs,
        long untilMs,
        bool ascending,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        int normalizedPage = page < 1 ? 1 : page;
        int normalizedSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        // Filter on the canonical id form so an id that differs only in
        // representation from the stored white/black/created_by values still matches.
        string? canonicalPlayer = string.IsNullOrEmpty(playerId) ? null : CanonicalizeUserId(playerId);
        string? canonicalInitiator = string.IsNullOrEmpty(initiatorId) ? null : CanonicalizeUserId(initiatorId);

        IReadOnlyList<MatchDocument> candidates =
            await repository.SearchAsync(canonicalPlayer, canonicalInitiator, ct);

        IEnumerable<MatchDocument> filtered = candidates;
        if (canonicalPlayer is not null)
        {
            filtered = filtered.Where(m => IsParticipant(m, canonicalPlayer));
        }

        if (canonicalInitiator is not null)
        {
            filtered = filtered.Where(m => IsInitiator(m, canonicalInitiator));
        }

        filtered = FilterByStatus(filtered, status);

        if (source is "native" or "external")
        {
            filtered = filtered.Where(m => m.Source == source);
        }

        if (sinceMs > 0)
        {
            filtered = filtered.Where(m => m.FinishedAtMs >= sinceMs);
        }

        if (untilMs > 0)
        {
            filtered = filtered.Where(m => m.FinishedAtMs <= untilMs);
        }

        return OrderAndPage(filtered, ascending, normalizedPage, normalizedSize);
    }

    // POST /matches/{id}/moves: validate the move against the live read model and emit
    // MoveSubmitted. The validator (stream processor) decides legality; the projector
    // applies the result and pushes move_made / match_ended. Returns once produced.
    internal async Task MakeMoveAsync(string matchId, string userId, string move, CancellationToken ct)
    {
        LiveMatchState state = await LoadLiveAsync(matchId, ct);
        MatchEvent ev = MatchCommands.SubmitMove(state, userId, move, Now(), NewId);
        await eventProducer.ProduceAsync(ev, ct);
    }

    // POST /matches/{id}/draw-offer
    internal async Task OfferDrawAsync(string matchId, string userId, CancellationToken ct)
    {
        LiveMatchState state = await LoadLiveAsync(matchId, ct);
        MatchEvent ev = MatchCommands.OfferDraw(state, userId, Now(), NewId);
        await eventProducer.ProduceAsync(ev, ct);
    }

    // POST /matches/{id}/draw-offer/accept
    internal async Task AcceptDrawAsync(string matchId, string userId, CancellationToken ct)
    {
        LiveMatchState state = await LoadLiveAsync(matchId, ct);
        MatchEvent ev = MatchCommands.AcceptDraw(state, userId, Now(), NewId);
        await eventProducer.ProduceAsync(ev, ct);
    }

    // DELETE /matches/{id}/draw-offer
    internal async Task DeclineDrawAsync(string matchId, string userId, CancellationToken ct)
    {
        LiveMatchState state = await LoadLiveAsync(matchId, ct);
        MatchEvent ev = MatchCommands.DeclineDraw(state, userId, Now(), NewId);
        await eventProducer.ProduceAsync(ev, ct);
    }

    // POST /matches/{id}/resign
    internal async Task ResignMatchAsync(string matchId, string userId, CancellationToken ct)
    {
        LiveMatchState state = await LoadLiveAsync(matchId, ct);
        MatchEvent ev = MatchCommands.Resign(state, userId, Now(), NewId);
        await eventProducer.ProduceAsync(ev, ct);
    }

    internal async Task<IReadOnlyList<string>> GetLegalMovesAsync(
        string matchId,
        string? fromSquare,
        CancellationToken ct)
    {
        MatchDocument match = await GetMatchAsync(matchId, ct);

        GetLegalMovesResponse response = await moveValidatorClient.GetLegalMovesAsync(
            new GetLegalMovesRequest { Fen = match.CurrentFen },
            cancellationToken: ct);

        IEnumerable<string> moves = response.Moves;

        if (fromSquare is not null)
        {
            moves = moves.Where(m => m.StartsWith(fromSquare, StringComparison.Ordinal));
        }

        return [.. moves];
    }

    // Scans the ongoing matches and emits exactly one MatchEnded{TIMEOUT} for each whose
    // active side is past last_move_at + remaining, read from the authoritative live read
    // model. No direct broadcast — the projector applies the MatchEnded and pushes
    // match_ended. The durable store only supplies the set of candidate ids; the clock
    // decision uses the live state (the system of record for an in-progress clock). A
    // match with no live projection (cold model) is skipped.
    internal async Task EnforceTimeoutsAsync(CancellationToken ct)
    {
        IReadOnlyList<MatchDocument> ongoingMatches = await repository.FindOngoingAsync(ct);
        long now = Now();

        foreach (MatchDocument match in ongoingMatches)
        {
            LiveMatchState? live = await liveState.GetAsync(match.Id, ct);
            if (live is null || live.Status != "ongoing")
            {
                continue;
            }

            bool whiteToMove = GetActiveColor(live.CurrentFen) == 'w';
            long remainingMs = whiteToMove ? live.WhiteTimeMs : live.BlackTimeMs;
            long elapsed = now - live.LastMoveAtMs;

            if (elapsed < remainingMs)
            {
                continue;
            }

            MatchEvent ev = MatchCommands.Timeout(live, whiteToMove, now, NewId);
            await eventProducer.ProduceAsync(ev, ct);
        }
    }

    internal async Task<(string Fen, string Move, bool IsCurrent)> GetPositionAsync(
        string matchId,
        int index,
        CancellationToken ct)
    {
        MatchDocument match = await GetMatchAsync(matchId, ct);

        if (!IsAnalyzable(match))
        {
            throw new AnalysisNotPermittedException();
        }

        if (index < 0 || index > match.Moves.Count)
        {
            throw new PositionIndexOutOfRangeException();
        }

        string fen = match.FenHistory[index];
        string move = index == 0 ? string.Empty : match.Moves[index - 1];
        bool isCurrent = index == match.Moves.Count;

        return (fen, move, isCurrent);
    }

    // Resolves a player's display username, replica-first with a GetUser fallback for a
    // cold miss. Used by the REST player-response mapping (a thin, excluded adapter), so
    // the replica-vs-RPC orchestration lives here where it is unit-tested.
    internal async Task<string> ResolveUsernameAsync(string userId, CancellationToken ct)
    {
        UserReplicaRecord? replica = await userReplica.GetAsync(userId, ct);
        if (replica?.Username is { Length: > 0 } username)
        {
            return username;
        }

        GetUserResponse response = await userServiceClient.GetUserAsync(
            new GetUserRequest { UserId = userId },
            cancellationToken: ct);
        return response.User.Username;
    }

    private static char GetActiveColor(string fen)
    {
        string[] parts = fen.Split(' ');
        return parts.Length >= 2 ? parts[1][0] : 'w';
    }

    // Resolves the starting position for a new match. An omitted, empty, or
    // "standard" start_fen yields the standard initial position (the only
    // behaviour pre-existing callers trigger); a supplied FEN is validated and an
    // ill-formed one is rejected so it never reaches the board state.
    private static string NormalizeStartFen(string? startFen)
    {
        if (string.IsNullOrWhiteSpace(startFen) ||
            string.Equals(startFen, "standard", StringComparison.OrdinalIgnoreCase))
        {
            return InitialFen;
        }

        string trimmed = startFen.Trim();
        return FenValidator.IsValid(trimmed)
            ? trimmed
            : throw new InvalidStartPositionException(trimmed);
    }

    // When the caller does not supply an initiator, attribute the match to the
    // human side (white preferred); bot-vs-bot matches have no human initiator.
    private static PlayerDocument? DeriveInitiator(PlayerDocument white, PlayerDocument black) =>
        !white.IsBot ? white : black.IsBot ? null : black;

    // A match belongs to a user's history when they played either colour or
    // initiated it (created_by) — the latter covers bot-vs-bot games they spawned.
    private static bool IsForUser(MatchDocument match, string canonicalUserId) =>
        IsParticipant(match, canonicalUserId) || IsInitiator(match, canonicalUserId);

    // The user occupied a colour (white or black) in this match.
    private static bool IsParticipant(MatchDocument match, string canonicalUserId) =>
        CanonicalizeUserId(match.White.UserId) == canonicalUserId ||
        CanonicalizeUserId(match.Black.UserId) == canonicalUserId;

    // The user initiated this match (created_by) — covers bot-vs-bot games they
    // spawned, where they occupied neither colour.
    private static bool IsInitiator(MatchDocument match, string canonicalUserId) =>
        CanonicalizeUserId(match.CreatedBy?.UserId) == canonicalUserId;

    // Applies the ongoing/ended status filter; any other value (e.g. "all") leaves
    // the sequence untouched so both ongoing and ended matches pass through.
    private static IEnumerable<MatchDocument> FilterByStatus(
        IEnumerable<MatchDocument> matches, string status) => status switch
    {
        "ongoing" => matches.Where(m => m.Status == "ongoing"),
        "ended" => matches.Where(m => m.Status != "ongoing"),
        _ => matches,
    };

    // Shared chronological order + page step for the history/browse queries: orders
    // by finished_at_ms (descending unless ascending) and returns the requested
    // slice alongside the full pre-page total.
    private static (IReadOnlyList<MatchDocument> Matches, int Total) OrderAndPage(
        IEnumerable<MatchDocument> filtered, bool ascending, int page, int pageSize)
    {
        List<MatchDocument> ordered = [.. ascending
            ? filtered.OrderBy(m => m.FinishedAtMs)
            : filtered.OrderByDescending(m => m.FinishedAtMs)];
        int total = ordered.Count;
        IReadOnlyList<MatchDocument> pageItems =
            [.. ordered.Skip((page - 1) * pageSize).Take(pageSize)];
        return (pageItems, total);
    }

    // A match in any status other than "ongoing" has reached a terminal,
    // immutable state and is safe to cache with no expiry.
    private static bool IsEnded(MatchDocument match) => match.Status != "ongoing";

    // The distinct canonical ids of the humans whose Past Matches include this
    // match: either colour they played plus the initiator of a bot-vs-bot game.
    private static IEnumerable<string> ParticipantUserIds(MatchDocument match) =>
        new[] { match.White.UserId, match.Black.UserId, match.CreatedBy?.UserId }
            .Where(id => id is not null)
            .Select(id => CanonicalizeUserId(id)!)
            .Distinct();

    // Native matches (the only ones that ride the event loop — external matches return
    // before this) carry either a human or a bot on each side.
    private static Player ToEventPlayer(PlayerDocument player) =>
        player.UserId is not null
            ? new Player { UserId = player.UserId }
            : new Player { BotId = player.BotId };

    private static TimeFormat ToEventTimeFormat(TimeFormatDocument tf) => new()
    {
        Id = tf.Id,
        BaseMs = tf.BaseMs,
        IncrementMs = tf.IncrementMs,
        Category = tf.Category,
    };

    private static string NewId() => Guid.NewGuid().ToString();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static MatchEvent Envelope(string matchId, string eventType, long occurredAt) =>
        new()
        {
            EventId = NewId(),
            EventType = eventType,
            AggregateId = matchId,
            Sequence = 0L,
            OccurredAt = occurredAt,
            CorrelationId = NewId(),
            CausationId = string.Empty,
            Producer = Producer,
        };

    private async Task<LiveMatchState> LoadLiveAsync(string matchId, CancellationToken ct) =>
        await liveState.GetAsync(matchId, ct) ?? throw new MatchNotFoundException(matchId);

    // Maintains the immutable read model when a match reaches an ended status (the
    // external-match sync path): refreshes the finished-match document cache and evicts
    // the page cache for every human who can see the game in their history. The native
    // event-loop path performs the equivalent in the projector's write-through.
    private async Task OnMatchEndedAsync(MatchDocument match, CancellationToken ct)
    {
        await cache.SetMatchAsync(match, ct);

        foreach (string canonicalUserId in ParticipantUserIds(match))
        {
            await cache.InvalidateUserPagesAsync(canonicalUserId, ct);
        }
    }
}
