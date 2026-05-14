using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class ListMatchesSteps(MatchServiceContext context)
{
    [Given(@"the repository has (\d+) ongoing matches for category ""([^""]*)""")]
    public void GivenTheRepositoryHasMatchesForCategory(int count, string category)
    {
        List<MatchDocument> matches = [];
        for (int i = 0; i < count; i++)
        {
            matches.Add(MatchServiceContext.BuildHumanMatch(
                $"match-{i}", $"white-{i}", $"black-{i}", "ongoing", category));
        }

        context.SetupListMatches(matches, count);
    }

    [When(@"ongoing matches are listed for category ""([^""]*)"" page (\d+) size (\d+)")]
    public async Task WhenOngoingMatchesAreListedForCategoryPageSize(string category, int page, int size)
    {
        context.LastListResult = await context.MatchService.ListMatchesAsync(
            "ongoing", category, page, size, CancellationToken.None);
    }

    [When(@"ongoing matches are listed without category on page (\d+) size (\d+)")]
    public async Task WhenOngoingMatchesAreListedWithoutCategory(int page, int size)
    {
        context.LastListResult = await context.MatchService.ListMatchesAsync(
            "ongoing", null, page, size, CancellationToken.None);
    }

    [Then(@"the listed match count is (\d+)")]
    public void ThenTheListedMatchCountIs(int expected) =>
        Assert.Equal(expected, context.LastListResult!.Value.Matches.Count);

    [Then(@"the listed total is (\d+)")]
    public void ThenTheListedTotalIs(int expected) =>
        Assert.Equal(expected, context.LastListResult!.Value.Total);
}
