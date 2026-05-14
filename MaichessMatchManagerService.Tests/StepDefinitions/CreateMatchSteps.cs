using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class CreateMatchSteps(MatchServiceContext context)
{
    private const string InitialFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    [When(@"a (.*) match is created between white ""([^""]*)"" and black ""([^""]*)""")]
    public async Task WhenAMatchIsCreated(string timeFormatCategory, string whiteId, string blackId)
    {
        PlayerDocument white = new() { UserId = whiteId };
        PlayerDocument black = new() { UserId = blackId };
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName(timeFormatCategory);
        context.LastMatchResult = await context.MatchService.CreateMatchAsync(
            white, black, tf, CancellationToken.None);
    }

    [When(@"a match is created between white ""([^""]*)"" and black ""([^""]*)"" with format ""([^""]*)""")]
    public async Task WhenAMatchIsCreatedWithFormatId(string whiteId, string blackId, string timeFormatId)
    {
        PlayerDocument white = new() { UserId = whiteId };
        PlayerDocument black = new() { UserId = blackId };
        TimeFormatDocument tf = TimeFormatRegistry.Resolve(timeFormatId);
        context.LastMatchResult = await context.MatchService.CreateMatchAsync(
            white, black, tf, CancellationToken.None);
    }

    [Then(@"the created match has WhiteTimeMs (.*)")]
    public void ThenTheCreatedMatchHasWhiteTimeMs(long expectedMs) =>
        Assert.Equal(expectedMs, context.LastMatchResult!.WhiteTimeMs);

    [Then(@"the created match has BlackTimeMs (.*)")]
    public void ThenTheCreatedMatchHasBlackTimeMs(long expectedMs) =>
        Assert.Equal(expectedMs, context.LastMatchResult!.BlackTimeMs);

    [Then(@"the created match has status ""([^""]*)""")]
    public void ThenTheCreatedMatchHasStatus(string expectedStatus) =>
        Assert.Equal(expectedStatus, context.LastMatchResult!.Status);

    [Then(@"the created match FenHistory starts with the initial FEN")]
    public void ThenTheCreatedMatchFenHistoryStartsWithInitialFen() =>
        Assert.Equal(InitialFen, context.LastMatchResult!.FenHistory[0]);

    [Then(@"the created match has time format ""([^""]*)""")]
    public void ThenTheCreatedMatchHasTimeFormat(string expectedId) =>
        Assert.Equal(expectedId, context.LastMatchResult!.TimeFormat.Id);

    [Then(@"the created match has IncrementMs (.*)")]
    public void ThenTheCreatedMatchHasIncrementMs(long expectedIncrement) =>
        Assert.Equal(expectedIncrement, context.LastMatchResult!.TimeFormat.IncrementMs);
}
