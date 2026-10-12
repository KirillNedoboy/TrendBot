using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class PortfolioReservationContractTests
{
    [Fact]
    public void PortfolioLimitsAndReservationsRetainCallerSuppliedExactCapacity()
    {
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(12.345678901234567890123456789m, "USDT"),
            new Money(987.65432109876543210987654321m, "USDT"), 3);
        PortfolioReservation reservation = new("reservation-a", portfolioId, "intent-a",
            new Money(1.2345678901234567890123456789m, "USDT"),
            new Money(98.765432109876543210987654321m, "USDT"), 1);

        Assert.Equal(portfolioId, limits.PortfolioId);
        Assert.Equal(12.345678901234567890123456789m, limits.MaxRisk.Amount);
        Assert.Equal(987.65432109876543210987654321m, limits.MaxNotionalExposure.Amount);
        Assert.Equal(3, limits.MaxPositionSlots);
        Assert.Equal("reservation-a", reservation.ReservationId);
        Assert.Equal("intent-a", reservation.IntentId);
        Assert.Equal(1.2345678901234567890123456789m, reservation.Risk.Amount);
        Assert.Equal(98.765432109876543210987654321m, reservation.NotionalExposure.Amount);
        Assert.Equal(1, reservation.PositionSlots);
    }

    [Fact]
    public void PortfolioMonetaryValuesRequireOneCurrencyAndNonNegativeCapacity()
    {
        PortfolioId portfolioId = new("portfolio-a");

        Assert.Throws<ArgumentException>(() => new PortfolioLimits(portfolioId,
            new Money(1m, "USDT"), new Money(2m, "USD"), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioLimits(portfolioId,
            new Money(-1m, "USDT"), new Money(2m, "USDT"), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioLimits(portfolioId,
            new Money(1m, "USDT"), new Money(2m, "USDT"), -1));
        Assert.Throws<ArgumentException>(() => new PortfolioReservation("reservation-a", portfolioId,
            "intent-a", new Money(1m, "USDT"), new Money(2m, "USD"), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioReservation("reservation-a",
            portfolioId, "intent-a", new Money(-1m, "USDT"), new Money(2m, "USDT"), 1));
    }

    [Fact]
    public void PortfolioContractsRoundTripThroughCanonicalJsonWithoutRounding()
    {
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId,
            new Money(12.345678901234567890123456789m, "USDT"),
            new Money(987.65432109876543210987654321m, "USDT"), 3);
        PortfolioReservation reservation = new("reservation-a", portfolioId, "intent-a",
            new Money(1.2345678901234567890123456789m, "USDT"),
            new Money(98.765432109876543210987654321m, "USDT"), 1);

        Assert.Equal(limits, CoreJsonSerializer.Deserialize<PortfolioLimits>(
            CoreJsonSerializer.Serialize(limits)));
        Assert.Equal(reservation, CoreJsonSerializer.Deserialize<PortfolioReservation>(
            CoreJsonSerializer.Serialize(reservation)));
    }
}
