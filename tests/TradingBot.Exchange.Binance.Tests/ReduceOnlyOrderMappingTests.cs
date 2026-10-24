using System.Reflection;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using Xunit;

namespace TradingBot.Exchange.Binance.Tests;

public sealed class ReduceOnlyOrderMappingTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void StopMapsToOneWayBothReduceOnlyAlgoOrderWithOppositeSide()
    {
        OrderIntent intent = CreateIntent("mapping-stop", OrderRole.Stop, Direction.Short, 90m);
        PortfolioPositionSnapshot position = CreatePosition(Direction.Long, "mapping-source");
        object request = CreateRequest(intent, position, "mapping-source", 0);

        object mapping = InvokeMap(request, BinancePositionMode.OneWay);

        Assert.True(Read<bool>(mapping, "IsAlgo"));
        Assert.Equal(BinanceAlgoOrderType.StopMarket, Read<BinanceAlgoOrderType>(mapping,
            "AlgoOrderType"));
        Assert.Equal(BinanceOrderSide.Sell, Read<BinanceOrderSide>(mapping, "Side"));
        Assert.Equal(BinancePositionSide.Both, Read<BinancePositionSide>(mapping,
            "PositionSide"));
        Assert.True(Read<bool>(mapping, "ReduceOnly"));
        Assert.Equal(90m, Read<Price>(mapping, "TriggerPrice").Value);
    }

    [Fact]
    public void EmergencyMapsToReduceOnlyMarketOrder()
    {
        OrderIntent intent = CreateIntent("mapping-emergency", OrderRole.Emergency,
            Direction.Short, limitPrice: null);
        PortfolioPositionSnapshot position = CreatePosition(Direction.Long,
            "mapping-emergency-source");
        object request = CreateRequest(intent, position, "mapping-emergency-source", 0);

        object mapping = InvokeMap(request, BinancePositionMode.OneWay);

        Assert.False(Read<bool>(mapping, "IsAlgo"));
        Assert.Equal(BinanceOrderType.Market, Read<BinanceOrderType>(mapping, "OrderType"));
        Assert.Null(ReadNullable(mapping, "AlgoOrderType"));
        Assert.Equal(BinanceOrderSide.Sell, Read<BinanceOrderSide>(mapping, "Side"));
        Assert.Equal(BinancePositionSide.Both, Read<BinancePositionSide>(mapping,
            "PositionSide"));
        Assert.True(Read<bool>(mapping, "ReduceOnly"));
        Assert.Null(ReadNullable(mapping, "TriggerPrice"));
    }

    [Fact]
    public void HedgeAndUnknownModesFailClosedBeforeMapping()
    {
        OrderIntent intent = CreateIntent("mapping-mode", OrderRole.TakeProfit1,
            Direction.Short, 110m);
        PortfolioPositionSnapshot position = CreatePosition(Direction.Long, "mapping-mode-source");
        object request = CreateRequest(intent, position, "mapping-mode-source", 0);

        Assert.Throws<TargetInvocationException>(() => InvokeMap(request,
            BinancePositionMode.Hedge));
        Assert.Throws<TargetInvocationException>(() => InvokeMap(request,
            BinancePositionMode.Unknown));
    }

    [Fact]
    public void EntryOrSameSideIntentCannotUseProtectiveMapping()
    {
        PortfolioPositionSnapshot position = CreatePosition(Direction.Long, "mapping-entry-source");
        OrderIntent entry = CreateIntent("mapping-entry", OrderRole.Entry, Direction.Long, 100m);
        TargetInvocationException entryException = Assert.Throws<TargetInvocationException>(() =>
            CreateRequest(entry, position, "mapping-entry-source", 0));
        Assert.IsType<ArgumentException>(entryException.InnerException);

        OrderIntent sameSide = CreateIntent("mapping-same-side", OrderRole.Stop,
            Direction.Long, 90m);
        TargetInvocationException sameSideException = Assert.Throws<TargetInvocationException>(() =>
            CreateRequest(sameSide, position, "mapping-entry-source", 0));
        Assert.IsType<ArgumentException>(sameSideException.InnerException);
    }

    private static object InvokeMap(object request, BinancePositionMode mode)
    {
        Type mapperType = typeof(BinancePrivateOrderRequest).Assembly.GetType(
            "TradingBot.Exchange.Binance.BinanceProtectiveOrderMapper")
            ?? throw new InvalidOperationException(
                "The reduce-only protective order mapper is not implemented.");
        MethodInfo method = mapperType.GetMethod("Map", BindingFlags.Public | BindingFlags.Static,
            [request.GetType(), typeof(BinancePositionMode)])
            ?? throw new InvalidOperationException(
                "The reduce-only protective order map API is not implemented.");
        return method.Invoke(null, [request, mode])
            ?? throw new InvalidOperationException("The protective mapping returned no result.");
    }

    private static object CreateRequest(OrderIntent intent, PortfolioPositionSnapshot position,
        string sourceIntentId, long revision)
    {
        Type requestType = typeof(OrderIntent).Assembly.GetType(
            "TradingBot.Core.ProtectiveExecutionRequest")
            ?? throw new InvalidOperationException(
                "The protective execution request contract is not implemented.");
        ConstructorInfo constructor = requestType.GetConstructor(
            [typeof(OrderIntent), typeof(PortfolioPositionSnapshot), typeof(string), typeof(long)])
            ?? throw new InvalidOperationException(
                "The protective execution request constructor is not implemented.");
        return constructor.Invoke([intent, position, sourceIntentId, revision]);
    }

    private static T Read<T>(object value, string propertyName) =>
        (T)(value.GetType().GetProperty(propertyName)?.GetValue(value)
            ?? throw new InvalidOperationException($"Missing mapping property {propertyName}."));

    private static object? ReadNullable(object value, string propertyName) =>
        value.GetType().GetProperty(propertyName)?.GetValue(value);

    private static OrderIntent CreateIntent(string id, OrderRole role, Direction direction,
        decimal? limitPrice) =>
        new(id, "run-mapping", "setup-mapping", $"client-{id}", Instrument, direction, role, 1,
            new Quantity(1m), limitPrice is decimal value ? new Price(value) : null, null, true);

    private static PortfolioPositionSnapshot CreatePosition(Direction direction, string intentId)
    {
        UtcTimestamp time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0,
            TimeSpan.Zero));
        Position position = new($"position-{intentId}", Instrument, direction,
            new Quantity(1m), time, time, new Price(100m));
        return new PortfolioPositionSnapshot(new PortfolioId("mapping-portfolio"), position,
            intentId, PositionOrigin.Known);
    }
}
