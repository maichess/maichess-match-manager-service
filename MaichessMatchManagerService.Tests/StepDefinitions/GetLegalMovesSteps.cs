using MaichessMatchManagerService.Services;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class GetLegalMovesSteps(MatchServiceContext context)
{
    [Given(@"the move validator returns legal moves ""([^""]*)""")]
    public void GivenTheMoveValidatorReturnsLegalMoves(string movesCommaSeparated)
    {
        string[] moves = movesCommaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries);
        context.SetupLegalMovesResponse(moves);
    }

    [When(@"any user requests legal moves on match ""([^""]*)""")]
    public async Task WhenAnyUserRequestsLegalMovesOnMatch(string matchId)
    {
        context.LastException = null;
        try
        {
            context.LastLegalMovesResult = await context.MatchService.GetLegalMovesAsync(
                matchId, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.LastException = ex;
        }
    }

    [When(@"any user requests legal moves from ""([^""]*)"" on match ""([^""]*)""")]
    public async Task WhenAnyUserRequestsLegalMovesFromSquareOnMatch(string fromSquare, string matchId)
    {
        context.LastException = null;
        try
        {
            context.LastLegalMovesResult = await context.MatchService.GetLegalMovesAsync(
                matchId, fromSquare, CancellationToken.None);
        }
        catch (Exception ex) when (ex is MatchNotFoundException)
        {
            context.LastException = ex;
        }
    }

    [Then(@"the legal moves result contains ""([^""]*)""")]
    public void ThenTheLegalMovesResultContains(string move) =>
        Assert.Contains(move, context.LastLegalMovesResult!);

    [Then(@"the legal moves result does not contain ""([^""]*)""")]
    public void ThenTheLegalMovesResultDoesNotContain(string move) =>
        Assert.DoesNotContain(move, context.LastLegalMovesResult!);
}
