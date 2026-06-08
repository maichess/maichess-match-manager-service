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
        }

        return match;
    }

    internal async Task<MatchDocument> GetMatchAsync(string matchId, CancellationToken ct)
    {
        MatchDocument? match = await repository.GetByIdAsync(matchId, ct);
        return match ?? throw new MatchNotFoundException(matchId);
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

        IReadOnlyList<MatchDocument> candidates = await repository.FindForUserAsync(userId, ct);

        IEnumerable<MatchDocument> filtered = candidates.Where(m => IsForUser(m, userId));
        filtered = status == "ongoing"
            ? filtered.Where(m => m.Status == "ongoing")
            : filtered.Where(m => m.Status != "ongoing");

        List<MatchDocument> ordered = [.. filtered.OrderByDescending(m => m.FinishedAtMs)];
        int total = ordered.Count;
        IReadOnlyList<MatchDocument> pageItems =
            [.. ordered.Skip((normalizedPage - 1) * normalizedSize).Take(normalizedSize)];

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
    private static bool IsForUser(MatchDocument match, string userId) =>
        match.White.UserId == userId ||
        match.Black.UserId == userId ||
        match.CreatedBy?.UserId == userId;

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
