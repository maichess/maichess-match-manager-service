using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class DrawManagementEdgeCaseSteps(MatchServiceContext context)
{
    [Given(@"the black player is a bot with BotId ""([^""]*)""")]
    public void GivenTheBlackPlayerIsABotWithBotId(string botId)
    {
        context.CurrentMatch!.Black = new PlayerDocument { BotId = botId };
        context.SetupMatch(context.CurrentMatch);
    }
}
