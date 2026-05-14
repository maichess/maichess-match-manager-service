using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;
using Xunit;

namespace MaichessMatchManagerService.Tests;

public sealed class TimeFormatRegistryTests
{
    [Theory]
    [InlineData("1+0", 60_000L, 0L, "bullet")]
    [InlineData("2+1", 120_000L, 1_000L, "bullet")]
    [InlineData("3+0", 180_000L, 0L, "blitz")]
    [InlineData("3+2", 180_000L, 2_000L, "blitz")]
    [InlineData("5+0", 300_000L, 0L, "blitz")]
    [InlineData("5+3", 300_000L, 3_000L, "blitz")]
    [InlineData("10+0", 600_000L, 0L, "rapid")]
    [InlineData("10+5", 600_000L, 5_000L, "rapid")]
    [InlineData("15+10", 900_000L, 10_000L, "rapid")]
    [InlineData("30+0", 1_800_000L, 0L, "classical")]
    [InlineData("30+20", 1_800_000L, 20_000L, "classical")]
    public void Resolve_KnownPreset_ReturnsExpectedFields(
        string id, long baseMs, long incrementMs, string category)
    {
        TimeFormatDocument tf = TimeFormatRegistry.Resolve(id);

        Assert.Equal(id, tf.Id);
        Assert.Equal(baseMs, tf.BaseMs);
        Assert.Equal(incrementMs, tf.IncrementMs);
        Assert.Equal(category, tf.Category);
    }

    [Fact]
    public void Resolve_UnknownId_FallsBackToDefault()
    {
        TimeFormatDocument tf = TimeFormatRegistry.Resolve("99+99");

        Assert.Equal("5+0", tf.Id);
        Assert.Equal(300_000L, tf.BaseMs);
    }

    [Fact]
    public void Default_IsBlitz5Plus0()
    {
        TimeFormatDocument tf = TimeFormatRegistry.Default;

        Assert.Equal("5+0", tf.Id);
        Assert.Equal("blitz", tf.Category);
    }

    [Theory]
    [InlineData("5+0", true)]
    [InlineData("3+2", true)]
    [InlineData("99+99", false)]
    [InlineData("", false)]
    public void IsKnown_ReturnsTrueForPresetsOnly(string id, bool expected) =>
        Assert.Equal(expected, TimeFormatRegistry.IsKnown(id));
}
