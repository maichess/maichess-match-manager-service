using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class ListUserMatchesSteps(MatchServiceContext context)
{
    private readonly List<MatchDocument> _candidates = [];

    [Given(@"a finished match ""([^""]*)"" with white ""([^""]*)"" black ""([^""]*)"" finished at (\d+)")]
    public void GivenFinishedMatch(string id, string white, string black, long finishedAt)
    {
        _candidates.Add(MatchServiceContext.BuildMatch(
            id,
            new PlayerDocument { UserId = white },
            new PlayerDocument { UserId = black },
            status: "white_won",
            finishedAtMs: finishedAt));
    }

    [Given(@"a finished bot-vs-bot match ""([^""]*)"" created by ""([^""]*)"" finished at (\d+)")]
    public void GivenFinishedBotMatchCreatedBy(string id, string creator, long finishedAt)
    {
        _candidates.Add(MatchServiceContext.BuildMatch(
            id,
            new PlayerDocument { BotId = "bot-a" },
            new PlayerDocument { BotId = "bot-b" },
            status: "draw",
            createdBy: new PlayerDocument { UserId = creator },
            finishedAtMs: finishedAt));
    }

    [Given(@"a candidate ongoing match ""([^""]*)"" with white ""([^""]*)"" black ""([^""]*)""")]
    public void GivenCandidateOngoingMatch(string id, string white, string black)
    {
        _candidates.Add(MatchServiceContext.BuildMatch(
            id,
            new PlayerDocument { UserId = white },
            new PlayerDocument { UserId = black },
            status: "ongoing"));
    }

    [Given(@"a finished match ""([^""]*)"" between other players")]
    public void GivenUnrelatedMatch(string id)
    {
        _candidates.Add(MatchServiceContext.BuildMatch(
            id,
            new PlayerDocument { UserId = "other-1" },
            new PlayerDocument { UserId = "other-2" },
            status: "draw",
            finishedAtMs: 100));
    }

    [When(@"matches are listed for user ""([^""]*)"" with status ""([^""]*)"" page (\d+) size (\d+)")]
    public async Task WhenMatchesAreListed(string userId, string status, int page, int size)
    {
        context.SetupFindForUser(_candidates);
        context.LastListResult = await context.MatchService.ListUserMatchesAsync(
            userId, status, page, size, CancellationToken.None);
    }

    [Then(@"the listed match at position (\d+) is ""([^""]*)""")]
    public void ThenListedMatchAtPositionIs(int index, string matchId) =>
        Assert.Equal(matchId, context.LastListResult!.Value.Matches[index].Id);
}
