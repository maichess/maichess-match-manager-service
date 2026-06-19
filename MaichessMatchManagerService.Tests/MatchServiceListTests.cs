using Grpc.Core;
using Maichess.MoveValidator.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Tests.Support;
using NSubstitute;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// ListMatchesAsync normalises the page, page size, and category before handing them to
// the repository's equality-filter pagination query. These assert the *arguments the
// repository receives*, which the BDD scenarios (asserting only the returned slice)
// leave unverified.
public sealed class MatchServiceListTests
{
    [Fact]
    public async Task ListMatches_InRangeArgs_PassThroughUnchanged()
    {
        MatchServiceContext ctx = new();
        ctx.SetupListMatches([], total: 0);

        await ctx.MatchService.ListMatchesAsync("ongoing", "blitz", 5, 50, CancellationToken.None);

        await ctx.Repository.Received(1).ListAsync(
            "ongoing", "blitz", 5, 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListMatches_PageBelowOneAndSizeZeroAndEmptyCategory_NormalisedForRepository()
    {
        MatchServiceContext ctx = new();
        ctx.SetupListMatches([], total: 0);

        await ctx.MatchService.ListMatchesAsync("ongoing", string.Empty, 0, 0, CancellationToken.None);

        // page → 1, size → default 20, empty category → null.
        await ctx.Repository.Received(1).ListAsync(
            "ongoing", null, 1, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListMatches_SizeAboveCap_IsCappedForRepository()
    {
        MatchServiceContext ctx = new();
        ctx.SetupListMatches([], total: 0);

        await ctx.MatchService.ListMatchesAsync("ongoing", null, 1, 500, CancellationToken.None);

        await ctx.Repository.Received(1).ListAsync(
            "ongoing", null, 1, 100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLegalMoves_SendsTheMatchFenToTheValidator()
    {
        MatchServiceContext ctx = new();
        MatchDocument match = MatchServiceContext.BuildHumanMatch("m1", "w", "b");
        ctx.SetupMatch(match);
        ctx.SetupLegalMovesResponse(["e2e4", "d2d4"]);

        await ctx.MatchService.GetLegalMovesAsync("m1", fromSquare: null, CancellationToken.None);

        _ = ctx.MoveValidator.Received(1).GetLegalMovesAsync(
            Arg.Is<GetLegalMovesRequest>(r => r.Fen == match.CurrentFen),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }
}
