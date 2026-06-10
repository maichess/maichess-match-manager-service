using Maichess.Events.V1;

namespace MaichessMatchManagerService.Kafka;

// Builds the MatchEnded payload for every end path (projector natural end,
// resign, draw agreement, timeout), stamping the participant/source snapshot
// the rating consumer needs to be stateless (kafka task 08): white/black
// identities, the match source, and the bot sides' engine elo captured at
// creation.
internal static class MatchEndedFactory
{
    internal static MatchEnded Create(LiveMatchState state, MatchStatus status, EndReason reason, long finishedAtMs)
    {
        MatchEnded ended = new()
        {
            Status = status,
            EndReason = reason,
            FinishedAtMs = finishedAtMs,
            White = ToPlayer(state.White),
            Black = ToPlayer(state.Black),
            Source = state.Source == "external" ? MatchSource.External : MatchSource.Native,
        };

        if (state.WhiteBotElo is { } whiteElo)
        {
            ended.WhiteBotElo = whiteElo;
        }

        if (state.BlackBotElo is { } blackElo)
        {
            ended.BlackBotElo = blackElo;
        }

        return ended;
    }

    private static Player ToPlayer(PlayerRef player) =>
        player.UserId is not null ? new Player { UserId = player.UserId }
        : player.BotId is not null ? new Player { BotId = player.BotId }
        : new Player();
}
