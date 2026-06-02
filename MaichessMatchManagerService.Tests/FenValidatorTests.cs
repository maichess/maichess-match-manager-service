using MaichessMatchManagerService.Services;
using Xunit;

namespace MaichessMatchManagerService.Tests;

public sealed class FenValidatorTests
{
    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq c6 0 2")]
    [InlineData("k6K/8/8/8/8/8/8/8 b - - 5 30")]
    public void IsValid_WellFormedFen_ReturnsTrue(string fen) =>
        Assert.True(FenValidator.IsValid(fen));

    [Theory]
    [InlineData("")]                                                   // empty / whitespace
    [InlineData("   ")]
    [InlineData("8/8/8/8/8/8/8/8 w - -")]                              // wrong field count (5)
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP w KQkq - 0 1")]    // 7 ranks
    [InlineData("44/8/8/8/8/8/8/8 w - - 0 1")]                         // consecutive digits
    [InlineData("k6K/8/8/8/8/8/8/0 w - - 0 1")]                        // placement digit below '1'
    [InlineData("X7/8/8/8/8/8/8/8 w - - 0 1")]                         // invalid piece char
    [InlineData("ppppppp/8/8/8/8/8/8/8 w - - 0 1")]                    // rank squares != 8
    [InlineData("8/8/8/8/8/8/8/8 w - - 0 1")]                          // no kings
    [InlineData("k7/8/8/8/8/8/8/8 w - - 0 1")]                         // missing white king
    [InlineData("K7/8/8/8/8/8/8/8 w - - 0 1")]                         // missing black king
    [InlineData("k6K/8/8/8/8/8/8/8 x - - 0 1")]                        // bad active colour
    [InlineData("k6K/8/8/8/8/8/8/8 w X - 0 1")]                        // bad castling char
    [InlineData("k6K/8/8/8/8/8/8/8 w KQkqK - 0 1")]                    // castling too long
    [InlineData("k6K/8/8/8/8/8/8/8 w - z9 0 1")]                       // en-passant file above 'h'
    [InlineData("k6K/8/8/8/8/8/8/8 w - 19 0 1")]                       // en-passant file below 'a'
    [InlineData("k6K/8/8/8/8/8/8/8 w - a9 0 1")]                       // en-passant rank above '8'
    [InlineData("k6K/8/8/8/8/8/8/8 w - a0 0 1")]                       // en-passant rank below '1'
    [InlineData("k6K/8/8/8/8/8/8/8 w - e444 0 1")]                     // en-passant wrong length
    [InlineData("k6K/8/8/8/8/8/8/8 w - - x 1")]                        // non-integer halfmove
    [InlineData("k6K/8/8/8/8/8/8/8 w - - -1 1")]                       // negative halfmove
    [InlineData("k6K/8/8/8/8/8/8/8 w - - 0 0")]                        // fullmove < 1
    [InlineData("k6K/8/8/8/8/8/8/8 w - - 0 x")]                        // non-integer fullmove
    public void IsValid_MalformedFen_ReturnsFalse(string fen) =>
        Assert.False(FenValidator.IsValid(fen));
}
