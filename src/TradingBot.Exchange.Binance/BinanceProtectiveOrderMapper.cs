using TradingBot.Core;

namespace TradingBot.Exchange.Binance;

/// <summary>Normalized reduce-only order shape selected before a Binance call.</summary>
public sealed record BinanceProtectiveOrderMapping
{
    public BinanceProtectiveOrderMapping(string symbol, BinanceOrderSide side,
        BinancePositionSide positionSide, bool isAlgo, BinanceOrderType? orderType,
        BinanceAlgoOrderType? algoOrderType, Quantity quantity, string clientOrderId,
        Price? triggerPrice, bool reduceOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(quantity);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        if (quantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        if (!reduceOnly)
        {
            throw new ArgumentException("Protective mappings must be reduce-only.",
                nameof(reduceOnly));
        }

        Symbol = symbol.ToUpperInvariant();
        Side = side;
        PositionSide = positionSide;
        IsAlgo = isAlgo;
        OrderType = orderType;
        AlgoOrderType = algoOrderType;
        Quantity = quantity;
        ClientOrderId = clientOrderId;
        TriggerPrice = triggerPrice;
        ReduceOnly = reduceOnly;
    }

    public string Symbol { get; }
    public BinanceOrderSide Side { get; }
    public BinancePositionSide PositionSide { get; }
    public bool IsAlgo { get; }
    public BinanceOrderType? OrderType { get; }
    public BinanceAlgoOrderType? AlgoOrderType { get; }
    public Quantity Quantity { get; }
    public string ClientOrderId { get; }
    public Price? TriggerPrice { get; }
    public bool ReduceOnly { get; }
}

/// <summary>
/// Maps the Core protective contract to the supported USD-M one-way/BOTH wire
/// choices. It is a pure mapper and does not own transport or credentials.
/// </summary>
public static class BinanceProtectiveOrderMapper
{
    public static BinanceProtectiveOrderMapping Map(ProtectiveExecutionRequest request,
        BinancePositionMode positionMode)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProtectiveExecutionGuard.Validate(request);
        if (positionMode != BinancePositionMode.OneWay)
        {
            throw new BinancePrivateProtocolException(
                "Only Binance One-way/BOTH position mode is supported for protective orders.");
        }

        OrderIntent intent = request.Intent;
        bool isEmergency = intent.Role == OrderRole.Emergency;
        if (!isEmergency && intent.LimitPrice is null)
        {
            throw new BinancePrivateProtocolException(
                "A stop or take-profit protective order requires a trigger price.");
        }

        BinanceOrderSide side = intent.Direction == Direction.Long
            ? BinanceOrderSide.Buy
            : BinanceOrderSide.Sell;
        BinanceOrderType? orderType = isEmergency ? BinanceOrderType.Market : null;
        BinanceAlgoOrderType? algoOrderType = intent.Role switch
        {
            OrderRole.Stop => BinanceAlgoOrderType.StopMarket,
            OrderRole.TakeProfit1 or OrderRole.TakeProfit2 =>
                BinanceAlgoOrderType.TakeProfitMarket,
            OrderRole.Emergency => null,
            _ => throw new BinancePrivateProtocolException(
                "The order role is not supported by the protective mapper.")
        };

        return new BinanceProtectiveOrderMapping(intent.InstrumentId.Value, side,
            BinancePositionSide.Both, !isEmergency, orderType, algoOrderType, intent.Quantity,
            intent.ClientOrderId, intent.LimitPrice, reduceOnly: true);
    }
}
