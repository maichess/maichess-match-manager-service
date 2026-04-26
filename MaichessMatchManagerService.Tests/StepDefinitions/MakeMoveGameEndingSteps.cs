using Maichess.MoveValidator.V1;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class MakeMoveGameEndingSteps(MatchServiceContext context)
{
    private const string BlackToMoveFen =
        "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";

    [Given(@"the move validator accepts move ""([^""]*)"" resulting in FEN ""([^""]*)"" with game result ""([^""]*)""")]
    public void GivenTheMoveValidatorAcceptsWithGameResult(string move, string fen, string gameResultStr)
    {
        GameResult gameResult = Enum.Parse<GameResult>(gameResultStr);
        context.SetupMoveValidatorAcceptsWithGameResult(move, fen, gameResult);
    }

    [Given(@"the match is at a black-to-move FEN")]
    public void GivenTheMatchIsAtABlackToMoveFen()
    {
        context.CurrentMatch!.CurrentFen = BlackToMoveFen;
    }

    [Given(@"the white player has (.*) ms remaining and moved (.*) seconds ago")]
    public void GivenTheWhitePlayerHasMsRemainingAndMovedSecondsAgo(long ms, int seconds)
    {
        context.CurrentMatch!.WhiteTimeMs = ms;
        context.CurrentMatch.LastMoveAt = DateTimeOffset.UtcNow.AddSeconds(-seconds);
    }

    [Given(@"the black player has (.*) ms remaining and moved (.*) seconds ago")]
    public void GivenTheBlackPlayerHasMsRemainingAndMovedSecondsAgo(long ms, int seconds)
    {
        context.CurrentMatch!.BlackTimeMs = ms;
        context.CurrentMatch.LastMoveAt = DateTimeOffset.UtcNow.AddSeconds(-seconds);
    }

    [Then(@"the returned match BlackTimeMs is less than (.*)")]
    public void ThenTheReturnedMatchBlackTimeMsIsLessThan(long expectedMax) =>
        Assert.True(context.LastMatchResult!.BlackTimeMs < expectedMax);
}
