using Maichess.Engine.V1;
using Maichess.MoveValidator.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;

namespace MaichessMatchManagerService.Services;

internal sealed partial class MatchService(
    MatchRepository repository,
    Moves.MovesClient moveValidatorClient,
    Bots.BotsClient engineClient,
    MatchEventBroadcaster broadcaster,
    ILogger<MatchService> logger)
{
    private const string InitialFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    internal static bool IsAnalyzable(MatchDocument match) =>
        match.White.IsBot || match.Black.IsBot || match.Status != "ongoing";

    internal async Task<MatchDocument> CreateMatchAsync(
        PlayerDocument white,
        PlayerDocument black,
        string timeControl,
        CancellationToken ct)
    {
        long initialTimeMs = TimeControlToMs(timeControl);
        MatchDocument match = new()
        {
            Id = Guid.NewGuid().ToString(),
            White = white,
            Black = black,
            CurrentFen = InitialFen,
            Status = "ongoing",
            TimeControl = timeControl,
            WhiteTimeMs = initialTimeMs,
            BlackTimeMs = initialTimeMs,
            LastMoveAt = DateTimeOffset.UtcNow,
            FenHistory = [InitialFen],
        };

        await repository.InsertAsync(match, ct);
        return match;
    }

    internal async Task<MatchDocument> GetMatchAsync(string matchId, CancellationToken ct)
    {
        MatchDocument? match = await repository.GetByIdAsync(matchId, ct);
        return match ?? throw new MatchNotFoundException(matchId);
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
        }

        await repository.ReplaceAsync(match, ct);

        PlayerDocument mover = isWhite ? match.White : match.Black;
        int moveIndex = match.Moves.Count;

        broadcaster.Broadcast(matchId, new MoveMadeNotification(
            move, validation.ResultingFen, moveIndex, mover, match.WhiteTimeMs, match.BlackTimeMs));

        if (match.Status != "ongoing")
        {
            bool isTimeout = match.WhiteTimeMs <= 0 || match.BlackTimeMs <= 0;
            string endReason = isTimeout ? "timeout" : GameResultToEndReason(validation.GameResult);
            BroadcastMatchEnded(matchId, match.Status, endReason);
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
        broadcaster.Broadcast(matchId, new DrawOfferedNotification(offerer));
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

        await repository.ReplaceAsync(match, ct);

        BroadcastMatchEnded(matchId, "draw", "draw_agreement");

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
        broadcaster.Broadcast(matchId, new DrawDeclinedNotification(decliner));
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

        await repository.ReplaceAsync(match, ct);

        BroadcastMatchEnded(matchId, match.Status, "resignation");

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

    [LoggerMessage(Level = LogLevel.Error, Message = "Bot move failed for match {matchId}")]
    private static partial void LogBotMoveFailed(ILogger logger, Exception ex, string matchId);

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

    private static string GameResultToEndReason(GameResult gameResult) => gameResult switch
    {
        GameResult.WhiteWon or GameResult.BlackWon => "checkmate",
        GameResult.Stalemate => "stalemate",
        GameResult.FiftyMoveRule => "fifty_move_rule",
        GameResult.ThreefoldRepetition => "threefold_repetition",
        GameResult.InsufficientMaterial => "insufficient_material",
        _ => "checkmate",
    };

    private static char GetActiveColor(string fen)
    {
        string[] parts = fen.Split(' ');
        return parts.Length >= 2 ? parts[1][0] : 'w';
    }

    private static long TimeControlToMs(string timeControl) => timeControl switch
    {
        "bullet" => 180_000L,
        "blitz" => 300_000L,
        "rapid" => 600_000L,
        "classical" => 1_800_000L,
        _ => 300_000L,
    };

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
        }

        await repository.ReplaceAsync(match, ct);

        PlayerDocument botPlayer = botIsWhite ? match.White : match.Black;
        int moveIndex = match.Moves.Count;

        broadcaster.Broadcast(
            matchId,
            new MoveMadeNotification(
                bestMove.Move,
                validation.ResultingFen,
                moveIndex,
                botPlayer,
                match.WhiteTimeMs,
                match.BlackTimeMs));

        if (match.Status != "ongoing")
        {
            bool isTimeout = match.WhiteTimeMs <= 0 || match.BlackTimeMs <= 0;
            string endReason = isTimeout ? "timeout" : GameResultToEndReason(validation.GameResult);
            BroadcastMatchEnded(matchId, match.Status, endReason);
        }
    }

    private void BroadcastMatchEnded(string matchId, string status, string reason)
    {
        broadcaster.Broadcast(matchId, new MatchEndedNotification(status, reason));
        broadcaster.Complete(matchId);
    }
}
