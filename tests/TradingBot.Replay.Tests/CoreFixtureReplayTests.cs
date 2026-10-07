using TradingBot.Core;
using TradingBot.TestFixtures;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class CoreFixtureReplayTests
{
    [Fact]
    public void SharedCausalVectorsRetainEventTimeAndStableSequenceOrder()
    {
        CausalOrderingVector vector = CoreFixtureVectors.CausalOrdering;

        string[] orderedIds = vector.Events
            .Where(vector.EventCursor.IsEligible)
            .OrderBy(static item => item,
                Comparer<CausalMarketEvent>.Create(CausalMarketEventOrdering.Compare))
            .Select(static item => item.Payload.EventId)
            .ToArray();

        Assert.Equal(vector.ExpectedEventIds, orderedIds);
        Assert.DoesNotContain(orderedIds, id => id == "future-event");
    }

    [Fact]
    public void SharedVectorsSerializeAndRestoreWithoutAReplayRunner()
    {
        foreach (CoreJsonRoundTripVector vector in CoreFixtureVectors.JsonRoundTripVectors)
        {
            object original = vector.Create();
            string json = vector.Serialize(original);
            object restored = vector.Deserialize(json);

            Assert.Equal(original, restored);
        }

        FillSimulationResult result = CoreFixtureVectors.NewFillResult();
        FillSimulationResult restoredResult = CoreJsonSerializer.Deserialize<FillSimulationResult>(
            CoreJsonSerializer.Serialize(result));

        Assert.Equal(result, restoredResult);
        Assert.Equal(result.Input.MarketSnapshot.Select(static item => item.StableSequence),
            restoredResult.Input.MarketSnapshot.Select(static item => item.StableSequence));
    }
}
