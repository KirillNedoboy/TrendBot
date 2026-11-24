using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoQuantityPlannerTests
{
    [Fact]
    public void QuantityRoundsDownAndPartitionsWithoutIncreasingNotional()
    {
        BybitDemoQuantityPlan plan = BybitDemoQuantityPlanner.Create(
            250m, 99.9m, 0.01m, 0.01m, 5m, 100m, splitFirstTarget: true);

        Assert.Equal(2.50m, plan.EntryQuantity);
        Assert.Equal(1.25m, plan.FirstTargetQuantity);
        Assert.Equal(1.25m, plan.RemainderQuantity);
        Assert.True(plan.EntryQuantity * 99.9m <= 250m);
    }

    [Fact]
    public void UnevenQuantityKeepsTheWholeRemainderProtected()
    {
        BybitDemoQuantityPlan plan = BybitDemoQuantityPlanner.Create(
            250m, 50m, 1m, 1m, 5m, 100m, splitFirstTarget: true);

        Assert.Equal(5m, plan.EntryQuantity);
        Assert.Equal(2m, plan.FirstTargetQuantity);
        Assert.Equal(3m, plan.RemainderQuantity);
    }

    [Fact]
    public void SingleTargetUsesTheEntireQuantity()
    {
        BybitDemoQuantityPlan plan = BybitDemoQuantityPlanner.Create(
            250m, 50m, 1m, 1m, 5m, 100m, splitFirstTarget: false);

        Assert.Equal(5m, plan.EntryQuantity);
        Assert.Equal(5m, plan.FirstTargetQuantity);
        Assert.Equal(0m, plan.RemainderQuantity);
    }

    [Fact]
    public void VenueMinimumMaximumAndUnrepresentableSplitAreRejected()
    {
        Assert.Throws<ArgumentException>(() => BybitDemoQuantityPlanner.Create(
            4m, 100m, 0.01m, 0.01m, 5m, 100m, splitFirstTarget: true));
        Assert.Throws<ArgumentException>(() => BybitDemoQuantityPlanner.Create(
            250m, 50m, 1m, 1m, 5m, 4m, splitFirstTarget: true));
        Assert.Throws<ArgumentException>(() => BybitDemoQuantityPlanner.Create(
            50m, 50m, 1m, 1m, 5m, 100m, splitFirstTarget: true));
    }
}
