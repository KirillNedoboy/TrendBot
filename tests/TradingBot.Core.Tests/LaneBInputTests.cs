using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LaneBInputTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void SelectionPreservesExactValuesUnitsAndCausalProvenanceThroughJsonRoundTrip()
    {
        LaneBOpenInterestObservation openInterest = OpenInterest(
            "oi-1", 1, 12345678901234567890123456789.00000000000000000001m,
            "contracts", "oi-source", 2, 3, 4);
        LaneBFundingObservation funding = Funding(
            "funding-1", 2, 0.00012345678901234567890123456789m,
            "rate", "periodic", "funding-source", 3, 4, 5);
        LaneBInput input = new(Instrument, [openInterest], [funding], "fixture-provenance");
        LaneBInputConfiguration configuration = new(
            "lane-b-config-v1", "contracts", "rate", "periodic", true, true, false);

        LaneBInputSelection selected = input.Select(Cursor(5, 10), configuration);
        string json = CoreJsonSerializer.Serialize(input);
        LaneBInput restored = CoreJsonSerializer.Deserialize<LaneBInput>(json);

        Assert.True(selected.IsReady);
        Assert.Equal(openInterest.Value, selected.OpenInterest[0].Value);
        Assert.Equal(funding.FundingRate, selected.Funding[0].FundingRate);
        Assert.Equal("contracts", selected.OpenInterest[0].Unit);
        Assert.Equal("periodic", selected.Funding[0].FundingKind);
        Assert.Equal(openInterest.ExchangeTime, selected.OpenInterest[0].ExchangeTime);
        Assert.Equal(funding.AvailabilityTime, selected.Funding[0].AvailabilityTime);
        Assert.Equal(openInterest.StableSequence, selected.OpenInterest[0].StableSequence);
        Assert.Equal(input, restored);
    }

    [Fact]
    public void SelectionUsesOnlyCausallyAvailablePrefixAndLeavesUnknownConfigurationExplicit()
    {
        LaneBInput input = new(Instrument,
            [OpenInterest("oi-future", 1, 10m, "contracts", "oi", 10, 10, 10)],
            [], "fixture");

        LaneBInputSelection unknown = input.Select(Cursor(5, 5), null);
        LaneBInputSelection unavailable = input.Select(Cursor(5, 5),
            new LaneBInputConfiguration("cfg", "contracts", null, null, true, false, false));

        Assert.Equal(LaneBInputState.UnknownConfiguration, unknown.State);
        Assert.False(unknown.IsReady);
        Assert.Equal(LaneBInputState.Unavailable, unavailable.State);
        Assert.Contains(unavailable.Reasons, reason => reason.Contains("available", StringComparison.Ordinal));
        Assert.Empty(unavailable.OpenInterest);
    }

    [Fact]
    public void SelectionComputesAbsoluteDeltaBetweenExplicitCompatibleObservationsAndDeduplicatesExactRepeats()
    {
        LaneBOpenInterestObservation baseline = OpenInterest("oi-a", 1, 100m,
            "contracts", "oi", 1, 1, 1);
        LaneBOpenInterestObservation latest = OpenInterest("oi-b", 2, 75.25m,
            "contracts", "oi", 2, 2, 2);
        LaneBObservationIdentity from = baseline.Identity;
        LaneBObservationIdentity to = latest.Identity;
        LaneBInput input = new(Instrument, [baseline, latest, latest], [], "fixture");
        LaneBInputConfiguration configuration = new(
            "cfg", "contracts", null, null, true, false, true, from, to);

        LaneBInputSelection selected = input.Select(Cursor(3, 3), configuration);

        Assert.True(selected.IsReady);
        Assert.Equal(24.75m, selected.AbsoluteOpenInterestDelta);
        Assert.Equal(2, selected.OpenInterest.Count);
        Assert.Contains(selected.Reasons, reason => reason.StartsWith("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectionMakesUnitAndIdentityConflictsBlockingStates()
    {
        LaneBOpenInterestObservation first = OpenInterest("oi-1", 1, 100m,
            "contracts", "oi", 1, 1, 1);
        LaneBOpenInterestObservation conflictingPayload = OpenInterest("oi-1", 1, 101m,
            "contracts", "oi", 1, 1, 1);
        LaneBInput conflictInput = new(Instrument, [first, conflictingPayload], [], "fixture");
        LaneBInputSelection conflict = conflictInput.Select(Cursor(2, 2),
            new LaneBInputConfiguration("cfg", "contracts", null, null, true, false, false));

        LaneBInput incompatibleInput = new(Instrument,
            [OpenInterest("oi-2", 2, 100m, "base", "oi", 2, 2, 2)], [], "fixture");
        LaneBInputSelection incompatible = incompatibleInput.Select(Cursor(2, 2),
            new LaneBInputConfiguration("cfg", "contracts", null, null, true, false, false));

        Assert.Equal(LaneBInputState.Conflict, conflict.State);
        Assert.False(conflict.IsReady);
        Assert.Equal(LaneBInputState.Incompatible, incompatible.State);
        Assert.False(incompatible.IsReady);
    }

    private static LaneBOpenInterestObservation OpenInterest(string eventId, long sequence,
        decimal value, string unit, string source, int eventOffset, int receiveOffset,
        int availabilityOffset) => new(source,
        new OIUpdate(eventId, Instrument, new UtcTimestamp(Time(eventOffset)), value), unit,
        $"{source}-provenance", new UtcTimestamp(Time(receiveOffset)),
        new UtcTimestamp(Time(availabilityOffset)), sequence,
        new UtcTimestamp(Time(eventOffset).AddSeconds(-1)));

    private static LaneBFundingObservation Funding(string eventId, long sequence,
        decimal value, string unit, string kind, string source, int eventOffset,
        int receiveOffset, int availabilityOffset) => new(source,
        new FundingUpdate(eventId, Instrument, new UtcTimestamp(Time(eventOffset)), value), unit, kind,
        $"{source}-provenance", new UtcTimestamp(Time(receiveOffset)),
        new UtcTimestamp(Time(availabilityOffset)), sequence,
        new UtcTimestamp(Time(eventOffset).AddSeconds(-1)));

    private static CausalEventCursor Cursor(int offset, long sequence) =>
        new(new UtcTimestamp(Time(offset)), sequence);

    private static DateTimeOffset Time(int offset) => BaseTime.AddMinutes(offset);
}
