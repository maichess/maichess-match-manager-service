using Maichess.MoveValidator.V1;
using MaichessMatchManagerService.Services;
using Xunit;

namespace MaichessMatchManagerService.Tests;

public sealed class GameResultToEndReasonTests
{
    [Theory]
    [InlineData(GameResult.WhiteWon, "checkmate")]
    [InlineData(GameResult.BlackWon, "checkmate")]
    [InlineData(GameResult.Stalemate, "stalemate")]
    [InlineData(GameResult.FiftyMoveRule, "fifty_move_rule")]
    [InlineData(GameResult.ThreefoldRepetition, "threefold_repetition")]
    [InlineData(GameResult.InsufficientMaterial, "insufficient_material")]
    public void MapsKnownResultsToEndReasons(GameResult result, string expected) =>
        Assert.Equal(expected, MatchService.GameResultToEndReason(result));

    [Fact]
    public void UnknownResultFallsBackToCheckmate() =>
        Assert.Equal("checkmate", MatchService.GameResultToEndReason((GameResult)999));
}
