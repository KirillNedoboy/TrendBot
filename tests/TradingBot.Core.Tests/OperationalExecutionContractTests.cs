using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class OperationalExecutionContractTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void FillAndPortfolioSnapshotsRetainOperationalCausalValues()
    {
        PortfolioId portfolioId = new("portfolio-a");
        Fill fill = new("fill-a", "intent-a", Instrument, new Quantity(0.25m),
            new Price(100.5m), Time(0), Time(1), Time(2), 17,
            new Money(0.01m, "USDT"), Time(3));
        Position position = new("position-a", Instrument, Direction.Long, new Quantity(0.25m),
            Time(0), Time(2), new Price(100m));
        PortfolioPositionSnapshot snapshot = new(portfolioId, position, "intent-a",
            PositionOrigin.Known);
        ReconciliationCheckpoint checkpoint = new(portfolioId, 4,
            ["intent-a"], ["position-a"], [], []);

        Assert.Equal(fill, CoreJsonSerializer.Deserialize<Fill>(CoreJsonSerializer.Serialize(fill)));
        Assert.Equal(snapshot,
            CoreJsonSerializer.Deserialize<PortfolioPositionSnapshot>(
                CoreJsonSerializer.Serialize(snapshot)));
        Assert.Equal(checkpoint,
            CoreJsonSerializer.Deserialize<ReconciliationCheckpoint>(
                CoreJsonSerializer.Serialize(checkpoint)));
        Assert.Equal(ReconciliationStatus.Unreconciled, checkpoint.Status);
        Assert.Equal(17, fill.StableSequence);
        Assert.Equal("USDT", fill.Fee!.Currency);
    }

    [Fact]
    public void OperationalContractsRejectInvalidValuesAndDistinguishManualUnknownPositions()
    {
        PortfolioId portfolioId = new("portfolio-a");
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fill("fill-a", "intent-a",
            Instrument, new Quantity(0m), new Price(100m), Time(0), Time(1), Time(1), 0));
        Assert.Throws<ArgumentException>(() => new Fill("fill-a", "intent-a", Instrument,
            new Quantity(1m), new Price(100m), Time(0), Time(2), Time(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fill("fill-a", "intent-a",
            Instrument, new Quantity(1m), new Price(100m), Time(0), Time(1), Time(1), -1));

        Position position = new("position-a", Instrument, Direction.Long, new Quantity(1m),
            Time(0), Time(0));
        PortfolioPositionSnapshot manual = new(portfolioId, position, null, PositionOrigin.Manual);
        PortfolioPositionSnapshot unknown = new(portfolioId, position, null, PositionOrigin.Unknown);
        Assert.Null(manual.IntentId);
        Assert.Equal(PositionOrigin.Unknown, unknown.Origin);
        Assert.Throws<ArgumentException>(() => new PortfolioPositionSnapshot(portfolioId,
            position, "intent-a", PositionOrigin.Manual));
        Assert.Throws<ArgumentException>(() => new PortfolioPositionSnapshot(portfolioId,
            position, null, PositionOrigin.Known));

        ReconciliationCheckpoint checkpoint = new(portfolioId, 0, [], [],
            ["exchange-state-unknown"], []);
        Assert.Equal(ReconciliationStatus.Unreconciled, checkpoint.Status);
        Assert.Equal("exchange-state-unknown", Assert.Single(checkpoint.UnknownMarkers));
    }

    private static UtcTimestamp Time(int minutes) =>
        new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddMinutes(minutes));
}
