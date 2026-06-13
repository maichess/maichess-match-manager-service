using Maichess.Events.V1;
using MaichessMatchManagerService.Kafka;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Every end path stamps the participant/source snapshot the rating consumer
// needs (kafka task 08) onto MatchEnded via this factory: identities, source,
// and the bot sides' creation-time elo.
public sealed class MatchEndedFactoryTests
{
    private static LiveMatchState State(
        PlayerRef? white = null,
        PlayerRef? black = null,
        string source = "native",
        double? whiteBotElo = null,
        double? blackBotElo = null) =>
        new(
            MatchId: "m1",
            CurrentFen: "fen",
            Status: "ongoing",
            WhiteTimeMs: 1,
            BlackTimeMs: 1,
            MoveIndex: 0,
            LastMoveAtMs: 0,
            IncrementMs: 0,
            PositionHistory: [],
            White: white ?? new PlayerRef("alice", null),
            Black: black ?? new PlayerRef("bob", null),
            Sequence: 1,
            Source: source,
            WhiteBotElo: whiteBotElo,
            BlackBotElo: blackBotElo);

    [Fact]
    public void HumanSides_StampIdentitiesAndNativeSourceWithoutElos()
    {
        MatchEnded ended = MatchEndedFactory.Create(State(), MatchStatus.WhiteWon, EndReason.Checkmate, 42);

        Assert.Equal(MatchStatus.WhiteWon, ended.Status);
        Assert.Equal(EndReason.Checkmate, ended.EndReason);
        Assert.Equal(42, ended.FinishedAtMs);
        Assert.Equal("alice", ended.White.UserId);
        Assert.Equal("bob", ended.Black.UserId);
        Assert.Equal(MatchSource.Native, ended.Source);
        Assert.False(ended.HasWhiteBotElo);
        Assert.False(ended.HasBlackBotElo);

        // Final clocks/FEN carried from the live read model (contracts 0.11.0).
        Assert.Equal(1, ended.WhiteTimeMs);
        Assert.Equal(1, ended.BlackTimeMs);
        Assert.Equal("fen", ended.FinalFen);
    }

    [Fact]
    public void BotSides_CarryTheirEloSnapshotsAndExternalSource()
    {
        MatchEnded ended = MatchEndedFactory.Create(
            State(
                white: new PlayerRef(null, "bot-a"),
                black: new PlayerRef(null, "bot-b"),
                source: "external",
                whiteBotElo: 1200,
                blackBotElo: 1500),
            MatchStatus.Draw,
            EndReason.Stalemate,
            7);

        Assert.Equal("bot-a", ended.White.BotId);
        Assert.Equal("bot-b", ended.Black.BotId);
        Assert.Equal(MatchSource.External, ended.Source);
        Assert.Equal(1200, ended.WhiteBotElo);
        Assert.Equal(1500, ended.BlackBotElo);
    }

    [Fact]
    public void MixedSides_OnlyTheBotSideCarriesAnElo()
    {
        MatchEnded ended = MatchEndedFactory.Create(
            State(black: new PlayerRef(null, "bot-b"), blackBotElo: 800),
            MatchStatus.BlackWon,
            EndReason.Resignation,
            7);

        Assert.Equal("alice", ended.White.UserId);
        Assert.Equal("bot-b", ended.Black.BotId);
        Assert.False(ended.HasWhiteBotElo);
        Assert.Equal(800, ended.BlackBotElo);
    }

    [Fact]
    public void ExternalParticipant_MapsToAnEmptyPlayer()
    {
        MatchEnded ended = MatchEndedFactory.Create(
            State(white: new PlayerRef(null, null)),
            MatchStatus.WhiteWon,
            EndReason.Timeout,
            7);

        Assert.Equal(Player.IdentityOneofCase.None, ended.White.IdentityCase);
    }
}
