using System.Globalization;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class DomainValueTests
{
    [Fact]
    public void InstrumentIdUsesOrdinalIdentityAndRejectsMissingValue()
    {
        Assert.Equal(new InstrumentId("BTCUSDT"), new InstrumentId("BTCUSDT"));
        Assert.NotEqual(new InstrumentId("BTCUSDT"), new InstrumentId("btcusdt"));
        Assert.Throws<ArgumentException>(() => new InstrumentId(" "));
    }

    [Fact]
    public void DecimalValuesPreserveExactAmountsAndValidateBounds()
    {
        Assert.Equal(new Price(1.2300m), new Price(1.23m));
        Assert.Equal(1.2300m, new Price(1.2300m).Value);
        Assert.Equal(new Quantity(0m), new Quantity(0m));
        Assert.Equal(-5.25m, new Money(-5.25m, "USDT").Amount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Price(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Quantity(-0.01m));
        Assert.Throws<ArgumentException>(() => new Money(1m, " "));
    }

    [Fact]
    public void ValuesDoNotDependOnCurrentCulture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.Equal(new Price(12.5m), new Price(12.50m));
            Assert.Equal(12.5m, new Price(12.5m).Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void UtcTimestampNormalizesEquivalentOffsets()
    {
        UtcTimestamp utc = new(new DateTimeOffset(2026, 1, 2, 3, 0, 0, TimeSpan.Zero));
        UtcTimestamp offset = new(new DateTimeOffset(2026, 1, 2, 5, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(utc, offset);
        Assert.Equal(TimeSpan.Zero, offset.Value.Offset);
    }

    [Fact]
    public void GeneratedValuesRetainEqualityAndHashConsistency()
    {
        for (int index = -100; index <= 100; index++)
        {
            decimal amount = index / 10m;
            Money first = new(amount, "USDT");
            Money second = new(amount, "USDT");

            Assert.Equal(first, second);
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
            Assert.Equal(amount, first.Amount);
        }
    }
}
