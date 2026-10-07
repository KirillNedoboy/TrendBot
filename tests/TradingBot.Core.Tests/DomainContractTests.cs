using System.Collections.Generic;
using System.Reflection;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class DomainContractTests
{
    [Fact]
    public void BookDeltaCopiesLevelsAndUsesStructuralEquality()
    {
        InstrumentId instrument = new("BTCUSDT");
        UtcTimestamp eventTime = new(DateTimeOffset.UnixEpoch);
        List<BookLevel> bidInput = [new(new Price(100m), new Quantity(2m))];
        List<BookLevel> askInput = [new(new Price(101m), new Quantity(3m))];

        BookDelta delta = new("delta-1", instrument, eventTime, bidInput, askInput);
        BookDelta equal = new("delta-1", instrument, eventTime,
            [new BookLevel(new Price(100m), new Quantity(2m))],
            [new BookLevel(new Price(101m), new Quantity(3m))]);
        bidInput.Add(new BookLevel(new Price(99m), new Quantity(4m)));

        Assert.Single(delta.Bids);
        Assert.Equal(delta, equal);
        Assert.Equal(delta.GetHashCode(), equal.GetHashCode());
        Assert.False((object)delta.Bids is BookLevel[]);
    }

    [Fact]
    public void DecisionsKeepIndependentOutcomesAndImmutableReasons()
    {
        List<string> input = ["price-confirmed"];
        Decision decision = new("decision-1", "setup-1", DecisionOutcome.Allow, input);
        RiskDecision risk = new("risk-1", "decision-1", RiskDecisionOutcome.Blocked, ["kill-switch"]);
        input[0] = "changed";

        Assert.Equal("price-confirmed", decision.Reasons[0]);
        Assert.Equal(DecisionOutcome.Allow, decision.Outcome);
        Assert.Equal(RiskDecisionOutcome.Blocked, risk.Outcome);
        Assert.Throws<ArgumentException>(() => new Decision("d", "s", DecisionOutcome.Block, []));
        Assert.Throws<ArgumentException>(() => new RiskDecision("r", "d", RiskDecisionOutcome.Approved, []));
    }

    [Fact]
    public void OrderIntentAndSetupRejectInvalidLocalValues()
    {
        InstrumentId instrument = new("BTCUSDT");
        UtcTimestamp timestamp = new(DateTimeOffset.UnixEpoch);

        Assert.Throws<ArgumentOutOfRangeException>(() => new OrderIntent(
            "intent", "run", "setup", "client", instrument, Direction.Long,
            OrderRole.Entry, 0, new Quantity(1m)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OIUpdate(
            "oi", instrument, timestamp, -1m));

        Setup setup = new("setup", instrument, SetupType.BoundaryRejection, Direction.Long,
            SetupState.Observing, "anchor", timestamp, new Price(100m), new Price(101m),
            new Price(105m), new Price(95m), "context-v1");

        Assert.Equal("anchor", setup.StructuralAnchorId);
        Assert.Throws<ArgumentException>(() => new Setup(" ", instrument, SetupType.CenterRetest,
            Direction.Short, SetupState.Observing, "anchor", timestamp, new Price(100m),
            new Price(101m), new Price(105m), new Price(95m), "context-v1"));
    }

    [Fact]
    public void MarketContractsRetainCallerSuppliedEventIdentityAndTime()
    {
        InstrumentId instrument = new("ETHUSDT");
        UtcTimestamp eventTime = new(DateTimeOffset.UnixEpoch.AddMinutes(1));
        Trade trade = new("trade-1", instrument, eventTime, new Price(20m),
            new Quantity(4m), TradeSide.Buy);

        Assert.Equal("trade-1", trade.EventId);
        Assert.Equal(instrument, trade.InstrumentId);
        Assert.Equal(eventTime, trade.EventTime);
    }

    [Fact]
    public void PublicContractPropertiesCannotBeReassigned()
    {
        Type[] contractTypes =
        [
            typeof(InstrumentId), typeof(Price), typeof(Quantity), typeof(Money), typeof(UtcTimestamp),
            typeof(Trade), typeof(Bar), typeof(BookLevel), typeof(BookDelta), typeof(FundingUpdate),
            typeof(OIUpdate), typeof(Setup), typeof(Decision), typeof(RiskDecision),
            typeof(OrderIntent), typeof(Position)
        ];

        foreach (Type contractType in contractTypes)
        {
            Assert.All(contractType.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                property => Assert.Null(property.SetMethod));
        }
    }
}
