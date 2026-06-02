using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class SyncExternalMatchSteps(MatchServiceContext context)
{
    [Given(@"an external match ""([^""]*)"" between external ""([^""]*)"" and external ""([^""]*)""")]
    public void GivenAnExternalMatch(string matchId, string whiteName, string blackName)
    {
        MatchDocument match = MatchServiceContext.BuildMatch(
            matchId,
            new PlayerDocument { ExternalName = whiteName },
            new PlayerDocument { ExternalName = blackName });
        match.Source = "external";
        match.ExternalProvider = "tournament-server";
        context.SetupMatch(match);
    }

    [Given(@"a native match ""([^""]*)"" between user ""([^""]*)"" and user ""([^""]*)""")]
    public void GivenANativeMatch(string matchId, string whiteId, string blackId)
    {
        MatchDocument match = MatchServiceContext.BuildHumanMatch(matchId, whiteId, blackId);
        context.SetupMatch(match);
    }

    [When(@"the external match ""([^""]*)"" is synced with moves ""([^""]*)"" and fen ""([^""]*)"" and status ""([^""]*)""")]
    public async Task WhenSyncedWithMoves(string matchId, string movesCsv, string fen, string status)
    {
        string[] moves = movesCsv.Split(',');
        context.LastMatchResult = await context.MatchService.SyncExternalMatchAsync(
            matchId,
            fen,
            moves,
            status,
            300_000,
            300_000,
            0,
            "checkmate",
            CancellationToken.None);
    }

    [When(@"the external match ""([^""]*)"" is synced with white_time_ms (\d+) and black_time_ms (\d+)")]
    public async Task WhenSyncedWithClock(string matchId, long whiteMs, long blackMs)
    {
        context.LastMatchResult = await context.MatchService.SyncExternalMatchAsync(
            matchId,
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            [],
            "ongoing",
            whiteMs,
            blackMs,
            0,
            "checkmate",
            CancellationToken.None);
    }

    [When(@"the external match ""([^""]*)"" is synced with status ""([^""]*)"" and finished_at_ms (\d+)")]
    public async Task WhenSyncedWithFinish(string matchId, string status, long finishedAtMs)
    {
        context.LastMatchResult = await context.MatchService.SyncExternalMatchAsync(
            matchId,
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            [],
            status,
            300_000,
            300_000,
            finishedAtMs,
            "checkmate",
            CancellationToken.None);
    }

    [When(@"the native match ""([^""]*)"" is synced")]
    public async Task WhenNativeMatchSynced(string matchId)
    {
        try
        {
            await context.MatchService.SyncExternalMatchAsync(
                matchId,
                "fen",
                [],
                "ongoing",
                300_000,
                300_000,
                0,
                "checkmate",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.LastException = ex;
        }
    }

    [When(@"an unknown match ""([^""]*)"" is synced")]
    public async Task WhenUnknownMatchSynced(string matchId)
    {
        try
        {
            await context.MatchService.SyncExternalMatchAsync(
                matchId,
                "fen",
                [],
                "ongoing",
                300_000,
                300_000,
                0,
                "checkmate",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.LastException = ex;
        }
    }

    [When(@"an external match is created with provider ""([^""]*)"" and ref ""([^""]*)""")]
    public async Task WhenExternalMatchCreated(string provider, string externalRef)
    {
        context.LastMatchResult = await context.MatchService.CreateMatchAsync(
            new PlayerDocument { ExternalName = "Alice" },
            new PlayerDocument { ExternalName = "Bob" },
            TimeFormatRegistry.Resolve("5+0"),
            null,
            null,
            "external",
            provider,
            externalRef,
            CancellationToken.None);
    }

    [When(@"an external match is created with bot white ""([^""]*)"" and external black ""([^""]*)""")]
    public async Task WhenExternalMatchCreatedWithBot(string botId, string externalName)
    {
        context.LastMatchResult = await context.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = botId },
            new PlayerDocument { ExternalName = externalName },
            TimeFormatRegistry.Resolve("5+0"),
            null,
            null,
            "external",
            "tournament-server",
            ct: CancellationToken.None);
    }

    [Then(@"the match ""([^""]*)"" has (\d+) moves")]
    public void ThenMatchHasMoves(string matchId, int count)
    {
        Assert.Equal(matchId, context.LastMatchResult!.Id);
        Assert.Equal(count, context.LastMatchResult.Moves.Count);
    }

    [Then(@"the match ""([^""]*)"" current fen is ""([^""]*)""")]
    public void ThenMatchFenIs(string matchId, string fen)
    {
        Assert.Equal(matchId, context.LastMatchResult!.Id);
        Assert.Equal(fen, context.LastMatchResult.CurrentFen);
    }

    [Then(@"the match ""([^""]*)"" status is ""([^""]*)""")]
    public void ThenMatchStatusIs(string matchId, string status)
    {
        Assert.Equal(matchId, context.LastMatchResult!.Id);
        Assert.Equal(status, context.LastMatchResult.Status);
    }

    [Then(@"the match ""([^""]*)"" has white_time_ms (\d+)")]
    public void ThenWhiteTimeMs(string matchId, long ms)
    {
        Assert.Equal(matchId, context.LastMatchResult!.Id);
        Assert.Equal(ms, context.LastMatchResult.WhiteTimeMs);
    }

    [Then(@"the match ""([^""]*)"" has black_time_ms (\d+)")]
    public void ThenBlackTimeMs(string matchId, long ms)
    {
        Assert.Equal(matchId, context.LastMatchResult!.Id);
        Assert.Equal(ms, context.LastMatchResult.BlackTimeMs);
    }

    [Then(@"the match ""([^""]*)"" finished_at_ms is (\d+)")]
    public void ThenFinishedAtMs(string matchId, long ms)
    {
        Assert.Equal(matchId, context.LastMatchResult!.Id);
        Assert.Equal(ms, context.LastMatchResult.FinishedAtMs);
    }

    [Then(@"the sync fails with InvalidOperationException")]
    public void ThenSyncFailsWithInvalidOperation() =>
        Assert.IsType<InvalidOperationException>(context.LastException);

    [Then(@"the sync fails with MatchNotFoundException")]
    public void ThenSyncFailsWithNotFound() =>
        Assert.IsType<MatchNotFoundException>(context.LastException);

    [Then(@"the created match source is ""([^""]*)""")]
    public void ThenSourceIs(string source) =>
        Assert.Equal(source, context.LastMatchResult!.Source);

    [Then(@"the created match external provider is ""([^""]*)""")]
    public void ThenExternalProviderIs(string provider) =>
        Assert.Equal(provider, context.LastMatchResult!.ExternalProvider);

    [Then(@"the created match external ref is ""([^""]*)""")]
    public void ThenExternalRefIs(string externalRef) =>
        Assert.Equal(externalRef, context.LastMatchResult!.ExternalRef);

    [Then(@"no bot move is triggered")]
    public void ThenNoBotMoveTriggered()
    {
        Assert.Equal("external", context.LastMatchResult!.Source);
    }
}
