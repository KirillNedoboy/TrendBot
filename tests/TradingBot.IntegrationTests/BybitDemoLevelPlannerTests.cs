using TradingBot.App;
using TradingBot.Core;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoLevelPlannerTests
{
    [Theory]
    [InlineData(Direction.Long, 95, 110, 120, 190, 220, 240)]
    [InlineData(Direction.Short, 105, 90, 80, 210, 180, 160)]
    public void FlowLevelsKeepRelativeDistanceFromActualFill(Direction direction,
        decimal sourceStop, decimal sourceTp1, decimal sourceTp2,
        decimal expectedStop, decimal expectedTp1, decimal expectedTp2)
    {
        BybitDemoLevelPlan result = BybitDemoLevelPlanner.Create(
            SetupType.FlowAtrRecovery, direction, 100m, sourceStop, sourceTp1,
            sourceTp2, 200m, 0.1m);

        Assert.Equal(expectedStop, result.StopLoss);
        Assert.Equal(expectedTp1, result.TakeProfit1);
        Assert.Equal(expectedTp2, result.TakeProfit2);
        Assert.Equal(200m, result.StopAfterFirstTarget);
    }

    [Theory]
    [InlineData(Direction.Long, 97, 104, 108, 9.73, 10.43, 10.83, 10.03)]
    [InlineData(Direction.Short, 103, 96, 92, 10.33, 9.63, 9.23, 10.03)]
    public void TickRoundingDoesNotIncreaseStopRiskOrOverstateTargets(Direction direction,
        decimal sourceStop, decimal sourceTp1, decimal sourceTp2, decimal expectedStop,
        decimal expectedTp1, decimal expectedTp2, decimal expectedEntryStop)
    {
        BybitDemoLevelPlan result = BybitDemoLevelPlanner.Create(
            SetupType.FlowStochasticRecovery, direction, 100m, sourceStop, sourceTp1,
            sourceTp2, 10.03m, 0.01m);

        Assert.Equal(expectedStop, result.StopLoss);
        Assert.Equal(expectedTp1, result.TakeProfit1);
        Assert.Equal(expectedTp2, result.TakeProfit2);
        Assert.Equal(expectedEntryStop, result.StopAfterFirstTarget);
    }

    [Fact]
    public void DonchianHasOneTargetAndNoHalfPositionTransition()
    {
        BybitDemoLevelPlan result = BybitDemoLevelPlanner.Create(
            SetupType.DonchianFlowBreakout, Direction.Long, 100m, 90m, 120m,
            null, 110m, 0.1m);

        Assert.Equal(99m, result.StopLoss);
        Assert.Equal(132m, result.TakeProfit1);
        Assert.Null(result.TakeProfit2);
        Assert.Null(result.StopAfterFirstTarget);
    }

    [Fact]
    public void MissingFlowTargetOrCollapsedTicksCannotProduceAnExecutionPlan()
    {
        Assert.Throws<ArgumentException>(() => BybitDemoLevelPlanner.Create(
            SetupType.FlowAtrRecovery, Direction.Long, 100m, 95m, 105m,
            null, 100m, 0.1m));
        Assert.Throws<ArgumentException>(() => BybitDemoLevelPlanner.Create(
            SetupType.FlowAtrRecovery, Direction.Long, 100m, 99.99m, 100.01m,
            100.02m, 100m, 1m));
    }

    [Fact]
    public void OppositeOrUnknownSourceLevelsCannotProduceAnExecutionPlan()
    {
        Assert.Throws<ArgumentException>(() => BybitDemoLevelPlanner.Create(
            SetupType.FlowAtrRecovery, Direction.Long, 100m, 101m, 105m,
            110m, 100m, 0.1m));
        Assert.Throws<ArgumentException>(() => BybitDemoLevelPlanner.Create(
            SetupType.BoundaryRejection, Direction.Long, 100m, 95m, 105m,
            110m, 100m, 0.1m));
    }
}
