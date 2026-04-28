using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Tests.Support;
using NSubstitute;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class TimeoutEnforcementSteps(MatchServiceContext context)
{
    private const string BlackToMoveFen =
        "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";

    private readonly Dictionary<string, MatchDocument> _matches = [];

    [Given(@"no ongoing matches exist for timeout enforcement")]
    public void GivenNoOngoingMatchesExist()
    {
        context.SetupOngoingMatches([]);
    }

    [Given(@"timeout enforcement match ""([^""]*)"" with white ""([^""]*)"" and black ""([^""]*)""")]
    public void GivenTimeoutEnforcementMatch(string matchId, string whiteId, string blackId)
    {
        MatchDocument match = MatchServiceContext.BuildHumanMatch(matchId, whiteId, blackId);
        _matches[matchId] = match;
        context.SetupMatch(match);
    }

    [Given(@"enforcement match ""([^""]*)"" white clock is (.*)ms and last move was (.*) seconds ago")]
    public void GivenEnforcementMatchWhiteClock(string matchId, long ms, int seconds)
    {
        _matches[matchId].WhiteTimeMs = ms;
        _matches[matchId].LastMoveAt = DateTimeOffset.UtcNow.AddSeconds(-seconds);
    }

    [Given(@"enforcement match ""([^""]*)"" black clock is (.*)ms and last move was (.*) seconds ago")]
    public void GivenEnforcementMatchBlackClock(string matchId, long ms, int seconds)
    {
        _matches[matchId].BlackTimeMs = ms;
        _matches[matchId].LastMoveAt = DateTimeOffset.UtcNow.AddSeconds(-seconds);
    }

    [Given(@"enforcement match ""([^""]*)"" is at a black-to-move position")]
    public void GivenEnforcementMatchIsBlackToMove(string matchId)
    {
        _matches[matchId].CurrentFen = BlackToMoveFen;
    }

    [Given(@"the enforcement ongoing list is ""([^""]*)""$")]
    public void GivenEnforcementOngoingListSingle(string matchId)
    {
        context.SetupOngoingMatches([_matches[matchId]]);
    }

    [Given(@"the enforcement ongoing list is ""([^""]*)"" and ""([^""]*)""")]
    public void GivenEnforcementOngoingListTwo(string matchId1, string matchId2)
    {
        context.SetupOngoingMatches([_matches[matchId1], _matches[matchId2]]);
    }

    [When(@"timeout enforcement runs")]
    public async Task WhenTimeoutEnforcementRuns()
    {
        await context.MatchService.EnforceTimeoutsAsync(CancellationToken.None);
    }

    [Then(@"no matches were saved by enforcement")]
    public void ThenNoMatchesWereSaved()
    {
        context.Repository.DidNotReceive().ReplaceAsync(
            Arg.Any<MatchDocument>(), Arg.Any<CancellationToken>());
    }

    [Then(@"enforcement match ""([^""]*)"" still has status ""([^""]*)""")]
    public void ThenEnforcementMatchHasStatus(string matchId, string status)
    {
        Assert.Equal(status, _matches[matchId].Status);
    }
}
