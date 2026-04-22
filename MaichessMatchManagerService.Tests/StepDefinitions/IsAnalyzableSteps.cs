using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class IsAnalyzableSteps(MatchServiceContext context)
{
    [Given(@"a match with status ""([^""]*)"" between two human players")]
    public void GivenAMatchWithStatusBetweenTwoHumanPlayers(string status)
    {
        context.CurrentMatch = MatchServiceContext.BuildHumanMatch("match-1", "white-1", "black-1", status);
    }

    [Given(@"a match with status ""([^""]*)"" where white is a bot")]
    public void GivenAMatchWhereWhiteIsABot(string status)
    {
        context.CurrentMatch = new MatchDocument
        {
            Id = "match-1",
            White = new PlayerDocument { BotId = "bot-1" },
            Black = new PlayerDocument { UserId = "black-1" },
            CurrentFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            Status = status,
            TimeControl = "blitz",
            WhiteTimeMs = 300_000,
            BlackTimeMs = 300_000,
            LastMoveAt = DateTimeOffset.UtcNow,
            FenHistory = ["rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"],
        };
    }

    [Given(@"a match with status ""([^""]*)"" where black is a bot")]
    public void GivenAMatchWhereBlackIsABot(string status)
    {
        context.CurrentMatch = new MatchDocument
        {
            Id = "match-1",
            White = new PlayerDocument { UserId = "white-1" },
            Black = new PlayerDocument { BotId = "bot-1" },
            CurrentFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            Status = status,
            TimeControl = "blitz",
            WhiteTimeMs = 300_000,
            BlackTimeMs = 300_000,
            LastMoveAt = DateTimeOffset.UtcNow,
            FenHistory = ["rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"],
        };
    }

    [Then(@"the match is analyzable")]
    public void ThenTheMatchIsAnalyzable() =>
        Assert.True(MatchService.IsAnalyzable(context.CurrentMatch!));

    [Then(@"the match is not analyzable")]
    public void ThenTheMatchIsNotAnalyzable() =>
        Assert.False(MatchService.IsAnalyzable(context.CurrentMatch!));
}
