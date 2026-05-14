using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;
using MaichessMatchManagerService.Tests.Support;
using Reqnroll;
using Xunit;

namespace MaichessMatchManagerService.Tests.StepDefinitions;

[Binding]
internal sealed class MatchServiceSteps(MatchServiceContext context)
{
    // ── Shared Given ─────────────────────────────────────────────────────────

    [Given(@"an ongoing (\S+) match ""([^""]*)"" between white ""([^""]*)"" and black ""([^""]*)""")]
    public void GivenAnOngoingMatch(string timeFormat, string matchId, string whiteId, string blackId)
    {
        MatchDocument match = MatchServiceContext.BuildHumanMatch(matchId, whiteId, blackId, "ongoing", timeFormat);
        context.SetupMatch(match);
    }

    [Given(@"the match has status ""([^""]*)""$")]
    public void GivenTheMatchHasStatus(string status)
    {
        context.CurrentMatch!.Status = status;
    }

    [Given(@"the match has status ""([^""]*)"" with move ""([^""]*)"" producing FEN ""([^""]*)""")]
    public void GivenTheMatchHasStatusWithMoveAndFen(string status, string move, string fen)
    {
        context.CurrentMatch!.Status = status;
        context.CurrentMatch.Moves.Add(move);
        context.CurrentMatch.FenHistory.Add(fen);
        context.CurrentMatch.CurrentFen = fen;
    }

    [Given(@"the move validator accepts move ""([^""]*)"" resulting in FEN ""([^""]*)""")]
    public void GivenTheMoveValidatorAcceptsMove(string move, string resultingFen)
    {
        context.SetupMoveValidatorAccepts(move, resultingFen);
    }

    [Given(@"the move validator rejects the move with reason ""([^""]*)""")]
    public void GivenTheMoveValidatorRejectsTheMove(string reason)
    {
        context.SetupMoveValidatorRejects(reason);
    }

    [Given(@"""([^""]*)"" has a pending draw offer on match ""([^""]*)""")]
    public void GivenHasAPendingDrawOffer(string userId, string matchId)
    {
        context.CurrentMatch!.PendingDrawOffererUserId = userId;
    }

    // ── Make Move When ────────────────────────────────────────────────────────

    [When(@"""([^""]*)"" makes move ""([^""]*)"" on match ""([^""]*)""")]
    public async Task WhenMakesMove(string userId, string move, string matchId)
    {
        context.LastException = null;
        try
        {
            context.LastMatchResult = await context.MatchService.MakeMoveAsync(matchId, userId, move, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.LastException = ex;
        }
    }

    // ── Resign When ───────────────────────────────────────────────────────────

    [When(@"""([^""]*)"" resigns from match ""([^""]*)""")]
    public async Task WhenResignsFromMatch(string userId, string matchId)
    {
        context.LastException = null;
        try
        {
            context.LastMatchResult = await context.MatchService.ResignMatchAsync(matchId, userId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.LastException = ex;
        }
    }

    // ── Draw When ────────────────────────────────────────────────────────────

    [When(@"""([^""]*)"" offers a draw on match ""([^""]*)""")]
    public async Task WhenOffersADraw(string userId, string matchId)
    {
        context.LastException = null;
        try
        {
            await context.MatchService.OfferDrawAsync(matchId, userId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.LastException = ex;
        }
    }

    [When(@"""([^""]*)"" accepts draw on match ""([^""]*)""")]
    public async Task WhenAcceptsDraw(string userId, string matchId)
    {
        context.LastException = null;
        try
        {
            context.LastMatchResult = await context.MatchService.AcceptDrawAsync(matchId, userId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.LastException = ex;
        }
    }

    [When(@"""([^""]*)"" declines draw on match ""([^""]*)""")]
    public async Task WhenDeclinesDraw(string userId, string matchId)
    {
        context.LastException = null;
        try
        {
            await context.MatchService.DeclineDrawAsync(matchId, userId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.LastException = ex;
        }
    }

    // ── Get Position When ────────────────────────────────────────────────────

    [When(@"any user requests position (.*) on match ""([^""]*)""")]
    public async Task WhenRequestsPosition(int index, string matchId)
    {
        context.LastException = null;
        try
        {
            context.LastPosition = await context.MatchService.GetPositionAsync(matchId, index, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.LastException = ex;
        }
    }

    // ── Exception Then ───────────────────────────────────────────────────────

    [Then(@"a MatchNotFoundException is thrown")]
    public void ThenMatchNotFoundExceptionIsThrown() =>
        Assert.IsType<MatchNotFoundException>(context.LastException);

    [Then(@"a MatchAlreadyEndedException is thrown")]
    public void ThenMatchAlreadyEndedExceptionIsThrown() =>
        Assert.IsType<MatchAlreadyEndedException>(context.LastException);

    [Then(@"a NotParticipantException is thrown")]
    public void ThenNotParticipantExceptionIsThrown() =>
        Assert.IsType<NotParticipantException>(context.LastException);

    [Then(@"a NotYourTurnException is thrown")]
    public void ThenNotYourTurnExceptionIsThrown() =>
        Assert.IsType<NotYourTurnException>(context.LastException);

    [Then(@"an IllegalMoveException is thrown with reason ""([^""]*)""")]
    public void ThenIllegalMoveExceptionIsThrown(string reason)
    {
        var ex = Assert.IsType<IllegalMoveException>(context.LastException);
        Assert.Equal(reason, ex.Reason);
    }

    [Then(@"a DrawOfferAlreadyPendingException is thrown")]
    public void ThenDrawOfferAlreadyPendingExceptionIsThrown() =>
        Assert.IsType<DrawOfferAlreadyPendingException>(context.LastException);

    [Then(@"a NoDrawOfferPendingException is thrown")]
    public void ThenNoDrawOfferPendingExceptionIsThrown() =>
        Assert.IsType<NoDrawOfferPendingException>(context.LastException);

    [Then(@"a NotDrawRecipientException is thrown")]
    public void ThenNotDrawRecipientExceptionIsThrown() =>
        Assert.IsType<NotDrawRecipientException>(context.LastException);

    [Then(@"an AnalysisNotPermittedException is thrown")]
    public void ThenAnalysisNotPermittedExceptionIsThrown() =>
        Assert.IsType<AnalysisNotPermittedException>(context.LastException);

    [Then(@"a PositionIndexOutOfRangeException is thrown")]
    public void ThenPositionIndexOutOfRangeExceptionIsThrown() =>
        Assert.IsType<PositionIndexOutOfRangeException>(context.LastException);

    // ── Match State Then ─────────────────────────────────────────────────────

    [Then(@"the match has status ""([^""]*)""")]
    public void ThenTheMatchHasStatus(string status)
    {
        MatchDocument result = context.LastMatchResult ?? context.CurrentMatch!;
        Assert.Equal(status, result.Status);
    }

    [Then(@"the match current FEN is ""([^""]*)""")]
    public void ThenTheMatchCurrentFenIs(string fen) =>
        Assert.Equal(fen, context.LastMatchResult!.CurrentFen);

    [Then(@"the match move list contains ""([^""]*)""")]
    public void ThenTheMatchMoveListContains(string move) =>
        Assert.Contains(move, context.LastMatchResult!.Moves);

    [Then(@"no draw offer is pending on match ""([^""]*)""")]
    public void ThenNoDrawOfferIsPending(string matchId) =>
        Assert.Null(context.CurrentMatch!.PendingDrawOffererUserId);

    [Then(@"the draw offer is from ""([^""]*)"" on match ""([^""]*)""")]
    public void ThenTheDrawOfferIsFrom(string userId, string matchId) =>
        Assert.Equal(userId, context.CurrentMatch!.PendingDrawOffererUserId);

    // ── Position Then ────────────────────────────────────────────────────────

    [Then(@"the position FEN is ""([^""]*)""")]
    public void ThenThePositionFenIs(string fen) =>
        Assert.Equal(fen, context.LastPosition!.Value.Fen);

    [Then(@"the position move is ""([^""]*)""")]
    public void ThenThePositionMoveIs(string move) =>
        Assert.Equal(move, context.LastPosition!.Value.Move);

    [Then(@"the position is current")]
    public void ThenThePositionIsCurrent() =>
        Assert.True(context.LastPosition!.Value.IsCurrent);

    [Then(@"the position is not current")]
    public void ThenThePositionIsNotCurrent() =>
        Assert.False(context.LastPosition!.Value.IsCurrent);
}
