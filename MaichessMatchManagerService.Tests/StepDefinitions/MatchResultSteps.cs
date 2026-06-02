using Maichess.User.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class MatchResultSteps(MatchServiceContext context)
{
    private const string BlackToMoveFen =
        "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";

    // ── Given (match shape by participant kind) ───────────────────────────────

    [Given(@"an ongoing match ""([^""]*)"" between human ""([^""]*)"" and human ""([^""]*)""")]
    public void GivenOngoingHumanHuman(string matchId, string whiteId, string blackId)
    {
        context.SetupMatch(MatchServiceContext.BuildMatch(
            matchId,
            new PlayerDocument { UserId = whiteId },
            new PlayerDocument { UserId = blackId }));
    }

    [Given(@"an ongoing match ""([^""]*)"" between human ""([^""]*)"" and bot ""([^""]*)""")]
    public void GivenOngoingHumanBot(string matchId, string whiteId, string botId)
    {
        context.SetupMatch(MatchServiceContext.BuildMatch(
            matchId,
            new PlayerDocument { UserId = whiteId },
            new PlayerDocument { BotId = botId }));
    }

    [Given(@"an ongoing match ""([^""]*)"" between bot ""([^""]*)"" and bot ""([^""]*)""")]
    public void GivenOngoingBotBot(string matchId, string whiteBot, string blackBot)
    {
        context.SetupMatch(MatchServiceContext.BuildMatch(
            matchId,
            new PlayerDocument { BotId = whiteBot },
            new PlayerDocument { BotId = blackBot }));
    }

    [Given(@"the active side has timed out on match ""([^""]*)""")]
    public void GivenActiveSideTimedOut(string matchId)
    {
        MatchDocument match = context.CurrentMatch!;
        match.WhiteTimeMs = 1;
        match.BlackTimeMs = 1;
        match.LastMoveAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        context.SetupOngoingMatches([match]);
    }

    [Given(@"user ""([^""]*)"" has rating (\d+) and deviation (\d+)")]
    public void GivenUserHasRating(string userId, int rating, int deviation)
    {
        context.SetupUserRating(userId, rating, deviation);
    }

    [Given(@"bot ""([^""]*)"" has elo (\d+)")]
    public void GivenBotHasElo(string botId, int elo)
    {
        context.SetupBot(botId, elo);
    }

    // ── When ──────────────────────────────────────────────────────────────────

    [When(@"timeout enforcement runs for the match")]
    public async Task WhenTimeoutEnforcementRunsForTheMatch()
    {
        await context.MatchService.EnforceTimeoutsAsync(CancellationToken.None);
    }

    // ── Then (recorded results) ───────────────────────────────────────────────

    [Then(@"no match results were recorded")]
    public void ThenNoMatchResultsRecorded() => Assert.Empty(context.RecordedResults);

    [Then(@"(\d+) match results? (?:was|were) recorded")]
    public void ThenMatchResultCount(int count) => Assert.Equal(count, context.RecordedResults.Count);

    [Then(@"a ""([^""]*)"" result was recorded for ""([^""]*)""")]
    public void ThenResultRecordedFor(string outcome, string userId) =>
        Assert.Contains(context.RecordedResults, r => r.UserId == userId && r.Outcome == ParseOutcome(outcome));

    [Then(@"a ""([^""]*)"" result was recorded for ""([^""]*)"" against opponent rating (\d+) deviation (\d+)")]
    public void ThenResultRecordedForAgainstOpponent(string outcome, string userId, int rating, int deviation) =>
        Assert.Contains(
            context.RecordedResults,
            r => r.UserId == userId
                && r.Outcome == ParseOutcome(outcome)
                && r.OpponentRating == rating
                && r.OpponentRd == deviation);

    internal static MatchOutcome ParseOutcome(string outcome) => outcome switch
    {
        "win" => MatchOutcome.Win,
        "loss" => MatchOutcome.Loss,
        "draw" => MatchOutcome.Draw,
        _ => MatchOutcome.Unspecified,
    };
}
