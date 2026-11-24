using TradingBot.App;
using TradingBot.Core;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoDonchianLevelsTests
{
    [Theory]
    [InlineData(Direction.Long, "95", "120")]
    [InlineData(Direction.Short, "105", "80")]
    public void ReadsModelLevelsForEitherDirection(Direction direction, string stop, string target)
    {
        string[] reasons = [$"model-stop:{stop}", "model-stop-period:13", "other:example",
            "model-target-period:89", $"model-target:{target}"];
        Assert.True(BybitDemoDonchianLevelReader.TryRead(reasons, direction, 100m, out BybitDemoDonchianLevels levels));
        Assert.Equal(decimal.Parse(stop, System.Globalization.CultureInfo.InvariantCulture), levels.Stop);
        Assert.Equal(decimal.Parse(target, System.Globalization.CultureInfo.InvariantCulture), levels.Target);
    }

    [Fact]
    public void MissingUnknownDuplicateAndWrongSideLevelsAreNeverUsable()
    {
        Assert.False(Read(["model-stop-period:13", "model-stop:95", "model-target-period:89"]));
        Assert.False(Read(["model-stop-period:13", "model-stop:unknown", "model-target-period:89", "model-target:120"]));
        Assert.False(Read(["model-stop-period:13", "model-stop:95", "model-stop:96", "model-target-period:89", "model-target:120"]));
        Assert.False(Read(["model-stop-period:13", "model-stop:105", "model-target-period:89", "model-target:120"]));
        Assert.False(Read(["model-stop-period:13", "model-stop:95", "model-target-period:55", "model-target:120"]));
    }

    private static bool Read(string[] reasons) =>
        BybitDemoDonchianLevelReader.TryRead(reasons, Direction.Long, 100m, out _);
}
