using System.Diagnostics.CodeAnalysis;
using Maichess.Engine.V1;
using Maichess.MoveValidator.V1;
using Maichess.User.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;

namespace MaichessMatchManagerService.Services;

internal sealed partial class MatchService(
    IMatchRepository repository,
    IMatchCache cache,
    IUserReplica userReplica,
    Moves.MovesClient moveValidatorClient,
    Bots.BotsClient engineClient,
    Users.UsersClient userServiceClient,
    ISocketBroadcaster socketNotifier,
    ILogger<MatchService> logger)
{
    private const string InitialFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    // Bots are treated as having an established rating, so their opponent update
    // uses a low, fixed deviation rather than the new-player default of 350.
    private const double BotRatingDeviation = 50.0;

    internal static bool IsAnalyzable(MatchDocument match) =>
        match.White.IsBot || match.Black.IsBot || match.Status != "ongoing";

    // Identity is a Guid everywhere it is minted, stored, or compared. Normalising
    // to the canonical lowercase hyphenated form makes ownership checks immune to a
    // pure case/format difference between the JWT `sub` and the Guid-normalised
    // id user-service returns. Non-Guid ids (e.g. legacy/test values) pass through
    // unchanged so equality still holds for them.
    internal static string? CanonicalizeUserId(string? userId) =>
        userId is not null && Guid.TryParse(userId, out Guid guid) ? guid.ToString() : userId;

    // The default arm is a defensive fallback for future GameResult values —
    // unreachable via the normal match flow, covered by a direct unit test.
    internal static string GameResultToEndReason(GameResult gameResult) => gameResult switch
    {
        GameResult.WhiteWon or GameResult.BlackWon => "checkmate",
        GameResult.Stalemate => "stalemate",
        GameResult.FiftyMoveRule => "fifty_move_rule",
        GameResult.ThreefoldRepetition => "threefold_repetition",
        GameResult.InsufficientMaterial => "insufficient_material",
        _ => "checkmate",
    };

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
        bool isExternal = source == "external";

        MatchDocument created = await repository.InsertAsync(
            new MatchDocument
            {
                Id = id ?? string.Empty,
                White = white,
                Black = black,
                CurrentFen = fen,
                Status = "ongoing",
                TimeFormat = timeFormat,
                WhiteTimeMs = timeFormat.BaseMs,
                BlackTimeMs = timeFormat.BaseMs,
                LastMoveAt = DateTimeOffset.UtcNow,
                FenHistory = [fen],
                CreatedBy = createdBy ?? DeriveInitiator(white, black),
                Source = source,
                ExternalProvider = externalProvider,
                ExternalRef = externalRef,
            },
            ct);

        if (!isExternal)
        {
            TriggerBotMoveIfNeeded(created);
        }

        return created;
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
        filtered = status == "ongoing"
            ? filtered.Where(m => m.Status == "ongoing")
            : filtered.Where(m => m.Status != "ongoing");

        List<MatchDocument> ordered = [.. filtered.OrderByDescending(m => m.FinishedAtMs)];
        int total = ordered.Count;
        IReadOnlyList<MatchDocument> pageItems =
            [.. ordered.Skip((normalizedPage - 1) * normalizedSize).Take(normalizedSize)];

        if (isEndedQuery)
        {
            await cache.SetUserPageAsync(
                canonicalUserId, statusFilter, normalizedPage, normalizedSize, pageItems, total, ct);
        }

        return (pageItems, total);
    }

    internal async Task<MatchDocument> MakeMoveAsync(
        string matchId,
        string userId,
        string move,
        CancellationToken ct)
    {
        MatchDocument match = await GetMatchAsync(matchId, ct);

        if (match.Status != "ongoing")
        {
            throw new MatchAlreadyEndedException();
        }

        bool isWhiteTurn = GetActiveColor(match.CurrentFen) == 'w';
        bool isWhite = match.White.UserId == userId;
        bool isBlack = match.Black.UserId == userId;

        if (!isWhite && !isBlack)
        {
            throw new NotParticipantException();
        }

        if ((isWhite && !isWhiteTurn) || (isBlack && isWhiteTurn))
        {
            throw new NotYourTurnException();
        }

        ValidateMoveRequest validateRequest = new() { Fen = match.CurrentFen, Move = move };
        validateRequest.PositionHistory.AddRange(match.PositionHistory);
        ValidateMoveResponse validation = await moveValidatorClient.ValidateMoveAsync(validateRequest, cancellationToken: ct);

        if (!validation.Valid)
        {
            throw new IllegalMoveException(validation.Reason);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        long elapsed = (long)(now - match.LastMoveAt).TotalMilliseconds;

        if (isWhite)
        {
            match.WhiteTimeMs = Math.Max(0, match.WhiteTimeMs - elapsed);
        }
        else
        {
            match.BlackTimeMs = Math.Max(0, match.BlackTimeMs - elapsed);
        }

        match.CurrentFen = validation.ResultingFen;
        match.Moves.Add(move);
        match.FenHistory.Add(validation.ResultingFen);
        match.PositionHistory = [.. validation.PositionHistory];
        match.LastMoveAt = now;

        ApplyGameResult(match, validation.GameResult);
        if (match.Status != "ongoing")
        {
            match.PositionHistory = [];
            match.FinishedAtMs = now.ToUnixTimeMilliseconds();
        }
        else
        {
            ApplyIncrement(match, isWhite);
        }

        await repository.ReplaceAsync(match, ct);

        PlayerDocument mover = isWhite ? match.White : match.Black;
        int moveIndex = match.Moves.Count;

        socketNotifier.BroadcastMoveMade(match, move, validation.ResultingFen, moveIndex, mover, match.WhiteTimeMs, match.BlackTimeMs);

        if (match.Status != "ongoing")
        {
            bool isTimeout = match.WhiteTimeMs <= 0 || match.BlackTimeMs <= 0;
            string endReason = isTimeout ? "timeout" : GameResultToEndReason(validation.GameResult);
            socketNotifier.BroadcastMatchEnded(match, match.Status, endReason);
            await OnMatchEndedAsync(match, ct);
            await RecordMatchResultsAsync(match, ct);
            return match;
        }

        TriggerBotMoveIfNeeded(match);

        return match;
    }

    internal async Task OfferDrawAsync(string matchId, string userId, CancellationToken ct)
    {
        MatchDocument match = await GetMatchAsync(matchId, ct);

        if (match.Status != "ongoing")
        {
            throw new MatchAlreadyEndedException();
        }

        bool isWhite = match.White.UserId == userId;
        bool isBlack = match.Black.UserId == userId;

        if (!isWhite && !isBlack)
        {
            throw new NotParticipantException();
        }

        PlayerDocument opponent = isWhite ? match.Black : match.White;
        if (opponent.IsBot)
        {
            throw new NotParticipantException();
        }

        if (match.PendingDrawOffererUserId is not null)
        {
            throw new DrawOfferAlreadyPendingException();
        }

        match.PendingDrawOffererUserId = userId;
        await repository.ReplaceAsync(match, ct);

        PlayerDocument offerer = isWhite ? match.White : match.Black;
        socketNotifier.BroadcastDrawOffered(match, offerer);
    }

    internal async Task<MatchDocument> AcceptDrawAsync(string matchId, string userId, CancellationToken ct)
    {
        MatchDocument match = await GetMatchAsync(matchId, ct);

        if (match.Status != "ongoing")
        {
            throw new MatchAlreadyEndedException();
        }

        bool isWhite = match.White.UserId == userId;
        bool isBlack = match.Black.UserId == userId;

        if (!isWhite && !isBlack)
        {
            throw new NotParticipantException();
        }

        if (match.PendingDrawOffererUserId is null)
        {
            throw new NoDrawOfferPendingException();
        }

        if (match.PendingDrawOffererUserId == userId)
        {
            throw new NotDrawRecipientException();
        }

        match.Status = "draw";
        match.PendingDrawOffererUserId = null;
        match.FinishedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await repository.ReplaceAsync(match, ct);

        socketNotifier.BroadcastMatchEnded(match, "draw", "draw_agreement");
        await OnMatchEndedAsync(match, ct);
        await RecordMatchResultsAsync(match, ct);

        return match;
    }

    internal async Task DeclineDrawAsync(string matchId, string userId, CancellationToken ct)
    {
        MatchDocument match = await GetMatchAsync(matchId, ct);

        if (match.Status != "ongoing")
        {
            throw new MatchAlreadyEndedException();
        }

        bool isWhite = match.White.UserId == userId;
        bool isBlack = match.Black.UserId == userId;

        if (!isWhite && !isBlack)
        {
            throw new NotParticipantException();
        }

        if (match.PendingDrawOffererUserId is null)
        {
            throw new NoDrawOfferPendingException();
        }

        match.PendingDrawOffererUserId = null;
        await repository.ReplaceAsync(match, ct);

        PlayerDocument decliner = isWhite ? match.White : match.Black;
        socketNotifier.BroadcastDrawDeclined(match, decliner);
    }

    internal async Task<MatchDocument> ResignMatchAsync(
        string matchId,
        string userId,
        CancellationToken ct)
    {
        MatchDocument match = await GetMatchAsync(matchId, ct);

        if (match.Status != "ongoing")
        {
            throw new MatchAlreadyEndedException();
        }

        bool isWhite = match.White.UserId == userId;
        bool isBlack = match.Black.UserId == userId;

        if (!isWhite && !isBlack)
        {
            throw new NotParticipantException();
        }

        match.Status = isWhite ? "black_won" : "white_won";
        match.FinishedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await repository.ReplaceAsync(match, ct);

        socketNotifier.BroadcastMatchEnded(match, match.Status, "resignation");
        await OnMatchEndedAsync(match, ct);
        await RecordMatchResultsAsync(match, ct);

        return match;
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

    internal async Task EnforceTimeoutsAsync(CancellationToken ct)
    {
        IReadOnlyList<MatchDocument> ongoingMatches = await repository.FindOngoingAsync(ct);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (MatchDocument match in ongoingMatches)
        {
            char activeColor = GetActiveColor(match.CurrentFen);
            long remainingMs = activeColor == 'w' ? match.WhiteTimeMs : match.BlackTimeMs;
            long elapsed = (long)(now - match.LastMoveAt).TotalMilliseconds;

            if (elapsed < remainingMs)
            {
                continue;
            }

            if (activeColor == 'w')
            {
                match.WhiteTimeMs = 0;
                match.Status = "black_won";
            }
            else
            {
                match.BlackTimeMs = 0;
                match.Status = "white_won";
            }

            match.PositionHistory = [];
            match.FinishedAtMs = now.ToUnixTimeMilliseconds();
            await repository.ReplaceAsync(match, ct);
            socketNotifier.BroadcastMatchEnded(match, match.Status, "timeout");
            await OnMatchEndedAsync(match, ct);
            await RecordMatchResultsAsync(match, ct);
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

    [ExcludeFromCodeCoverage]
    [LoggerMessage(Level = LogLevel.Error, Message = "Bot move failed for match {matchId}")]
    private static partial void LogBotMoveFailed(ILogger logger, Exception ex, string matchId);

    [ExcludeFromCodeCoverage]
    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine returned invalid move {move} for match {matchId}: {reason}")]
    private static partial void LogEngineInvalidMove(ILogger logger, string move, string matchId, string reason);

    private static void ApplyGameResult(MatchDocument match, GameResult gameResult)
    {
        if (match.WhiteTimeMs <= 0)
        {
            match.Status = "black_won";
            return;
        }

        if (match.BlackTimeMs <= 0)
        {
            match.Status = "white_won";
            return;
        }

        match.Status = gameResult switch
        {
            GameResult.WhiteWon => "white_won",
            GameResult.BlackWon => "black_won",
            GameResult.Stalemate
                or GameResult.FiftyMoveRule
                or GameResult.ThreefoldRepetition
                or GameResult.InsufficientMaterial => "draw",
            _ => "ongoing",
        };
    }

    // Increment is credited to the side that just moved, only when the match is
    // still ongoing — a move that ends the game (mate, time forfeit, etc.)
    // does not earn the bonus.
    private static void ApplyIncrement(MatchDocument match, bool isWhite)
    {
        long increment = match.TimeFormat.IncrementMs;
        if (increment <= 0)
        {
            return;
        }

        if (isWhite)
        {
            match.WhiteTimeMs += increment;
        }
        else
        {
            match.BlackTimeMs += increment;
        }
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
    // The caller supplies an already-canonical id; stored ids are canonicalised
    // here so a pure case/format difference cannot drop a legitimate match.
    private static bool IsForUser(MatchDocument match, string canonicalUserId) =>
        CanonicalizeUserId(match.White.UserId) == canonicalUserId ||
        CanonicalizeUserId(match.Black.UserId) == canonicalUserId ||
        CanonicalizeUserId(match.CreatedBy?.UserId) == canonicalUserId;

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

    // Maintains the immutable read model when a match reaches an ended status:
    // refreshes the finished-match document cache and evicts the page cache for
    // every human who can see the game in their history (white, black, and the
    // created_by initiator), so the newly-finished game appears on the next read.
    // This is the only path that writes these caches outside a cache-miss reload.
    private async Task OnMatchEndedAsync(MatchDocument match, CancellationToken ct)
    {
        await cache.SetMatchAsync(match, ct);

        foreach (string canonicalUserId in ParticipantUserIds(match))
        {
            await cache.InvalidateUserPagesAsync(canonicalUserId, ct);
        }
    }

    // Fans the final result out to user-service for each human participant. The
    // single mutation point for player stats and Glicko-2 ratings. Bot-vs-bot
    // games record nothing, so they never affect any player's W/L/D or rating.
    private async Task RecordMatchResultsAsync(MatchDocument match, CancellationToken ct)
    {
        (MatchOutcome white, MatchOutcome black) = match.Status switch
        {
            "white_won" => (MatchOutcome.Win, MatchOutcome.Loss),
            "black_won" => (MatchOutcome.Loss, MatchOutcome.Win),
            _ => (MatchOutcome.Draw, MatchOutcome.Draw),
        };

        // Snapshot both opponents' ratings before recording either result, so a
        // human-vs-human pair is each rated against the other's pre-match rating
        // rather than a value already updated by this same fan-out.
        OpponentRating? whiteOpponent =
            match.White.UserId is null ? null : await ResolveOpponentRatingAsync(match.Black, ct);
        OpponentRating? blackOpponent =
            match.Black.UserId is null ? null : await ResolveOpponentRatingAsync(match.White, ct);

        await RecordOutcomeAsync(match.White, white, whiteOpponent, ct);
        await RecordOutcomeAsync(match.Black, black, blackOpponent, ct);
    }

    private async Task RecordOutcomeAsync(
        PlayerDocument player, MatchOutcome outcome, OpponentRating? opponent, CancellationToken ct)
    {
        if (opponent is null)
        {
            return;
        }

        await userServiceClient.RecordMatchResultAsync(
            new RecordMatchResultRequest
            {
                UserId = player.UserId!,
                Outcome = outcome,
                OpponentRating = opponent.Value.Rating,
                OpponentRd = opponent.Value.Rd,
            },
            cancellationToken: ct);
    }

    // Resolves the opponent's display-scale rating and deviation used for the
    // human's Glicko-2 update. Bots are treated as having an established rating:
    // their engine-configured elo with a fixed low deviation.
    private async Task<OpponentRating> ResolveOpponentRatingAsync(PlayerDocument opponent, CancellationToken ct)
    {
        if (opponent.IsBot)
        {
            ListBotsResponse bots = await engineClient.ListBotsAsync(new ListBotsRequest(), cancellationToken: ct);
            Bot? bot = bots.Bots.FirstOrDefault(b => b.Id == opponent.BotId);
            return new OpponentRating(bot?.Elo ?? 0, BotRatingDeviation);
        }

        // Replica-first: the rating enrichment reads the Redis user replica and only
        // falls back to the hot GetUser RPC on a cold miss (key or rating field not yet
        // materialised). The replica's fields are nullable, so a partially-warmed row
        // still defers to GetUser rather than rating an opponent against a default.
        UserReplicaRecord? replica = await userReplica.GetAsync(opponent.UserId!, ct);
        if (replica is { Rating: { } rating, RatingDeviation: { } rd })
        {
            return new OpponentRating(rating, rd);
        }

        GetUserResponse response = await userServiceClient.GetUserAsync(
            new GetUserRequest { UserId = opponent.UserId! },
            cancellationToken: ct);
        return new OpponentRating(response.User.Rating, response.User.RatingDeviation);
    }

    [ExcludeFromCodeCoverage]
    private void TriggerBotMoveIfNeeded(MatchDocument match)
    {
        bool newTurnIsWhite = GetActiveColor(match.CurrentFen) == 'w';
        PlayerDocument nextPlayer = newTurnIsWhite ? match.White : match.Black;

        if (!nextPlayer.IsBot)
        {
            return;
        }

        string matchId = match.Id;
        string botId = nextPlayer.BotId!;

        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessBotMoveAsync(matchId, botId, CancellationToken.None);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogBotMoveFailed(logger, ex, matchId);
            }
        });
    }

    [ExcludeFromCodeCoverage]
    private async Task ProcessBotMoveAsync(string matchId, string botId, CancellationToken ct)
    {
        MatchDocument? match = await repository.GetByIdAsync(matchId, ct);

        if (match is null || match.Status != "ongoing")
        {
            return;
        }

        bool botIsWhite = GetActiveColor(match.CurrentFen) == 'w';

        long remainingMs = botIsWhite ? match.WhiteTimeMs : match.BlackTimeMs;
        GetBestMoveResponse bestMove = await engineClient.GetBestMoveAsync(
            new GetBestMoveRequest { Fen = match.CurrentFen, BotId = botId, TimeLimitMs = (uint)remainingMs },
            cancellationToken: ct);

        ValidateMoveRequest validateRequest = new() { Fen = match.CurrentFen, Move = bestMove.Move };
        validateRequest.PositionHistory.AddRange(match.PositionHistory);
        ValidateMoveResponse validation = await moveValidatorClient.ValidateMoveAsync(validateRequest, cancellationToken: ct);

        if (!validation.Valid)
        {
            LogEngineInvalidMove(logger, bestMove.Move, matchId, validation.Reason);
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        long elapsed = (long)(now - match.LastMoveAt).TotalMilliseconds;

        if (botIsWhite)
        {
            match.WhiteTimeMs = Math.Max(0, match.WhiteTimeMs - elapsed);
        }
        else
        {
            match.BlackTimeMs = Math.Max(0, match.BlackTimeMs - elapsed);
        }

        match.CurrentFen = validation.ResultingFen;
        match.Moves.Add(bestMove.Move);
        match.FenHistory.Add(validation.ResultingFen);
        match.PositionHistory = [.. validation.PositionHistory];
        match.LastMoveAt = now;

        ApplyGameResult(match, validation.GameResult);
        if (match.Status != "ongoing")
        {
            match.PositionHistory = [];
            match.FinishedAtMs = now.ToUnixTimeMilliseconds();
        }
        else
        {
            ApplyIncrement(match, botIsWhite);
        }

        await repository.ReplaceAsync(match, ct);

        PlayerDocument botPlayer = botIsWhite ? match.White : match.Black;
        int moveIndex = match.Moves.Count;

        socketNotifier.BroadcastMoveMade(match, bestMove.Move, validation.ResultingFen, moveIndex, botPlayer, match.WhiteTimeMs, match.BlackTimeMs);

        if (match.Status != "ongoing")
        {
            bool isTimeout = match.WhiteTimeMs <= 0 || match.BlackTimeMs <= 0;
            string endReason = isTimeout ? "timeout" : GameResultToEndReason(validation.GameResult);
            socketNotifier.BroadcastMatchEnded(match, match.Status, endReason);
            await OnMatchEndedAsync(match, ct);
            await RecordMatchResultsAsync(match, ct);
            return;
        }

        // Continue the chain when the opponent is also a bot (bot-vs-bot games).
        TriggerBotMoveIfNeeded(match);
    }

    // The opponent rating/deviation pair supplied to user-service for a Glicko-2
    // update.
    private readonly record struct OpponentRating(double Rating, double Rd);
}
