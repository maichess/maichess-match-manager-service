using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class CreatedBySteps(MatchServiceContext context)
{
    [When(@"a match is created with white (human|bot) ""([^""]*)"" and black (human|bot) ""([^""]*)""")]
    public async Task WhenMatchIsCreated(string whiteKind, string whiteId, string blackKind, string blackId)
    {
        context.LastMatchResult = await context.MatchService.CreateMatchAsync(
            MakePlayer(whiteKind, whiteId),
            MakePlayer(blackKind, blackId),
            TimeFormatRegistry.Resolve("5+0"),
            null,
            null,
            ct: CancellationToken.None);
    }

    [When(@"a match is created with white bot ""([^""]*)"" and black bot ""([^""]*)"" started by ""([^""]*)""")]
    public async Task WhenBotMatchIsCreatedStartedBy(string whiteBot, string blackBot, string starter)
    {
        context.LastMatchResult = await context.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = whiteBot },
            new PlayerDocument { BotId = blackBot },
            TimeFormatRegistry.Resolve("5+0"),
            new PlayerDocument { UserId = starter },
            null,
            ct: CancellationToken.None);
    }

    [Then(@"the match created_by user is ""([^""]*)""")]
    public void ThenCreatedByUserIs(string userId) =>
        Assert.Equal(userId, context.LastMatchResult!.CreatedBy?.UserId);

    [Then(@"the match has no created_by")]
    public void ThenNoCreatedBy() =>
        Assert.Null(context.LastMatchResult!.CreatedBy);

    [Then(@"the match source is ""([^""]*)""")]
    public void ThenSourceIs(string source) =>
        Assert.Equal(source, context.LastMatchResult!.Source);

    private static PlayerDocument MakePlayer(string kind, string id) =>
        kind == "bot" ? new PlayerDocument { BotId = id } : new PlayerDocument { UserId = id };
}
