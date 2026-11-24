using TradingBot.Core;

namespace TradingBot.App;

internal readonly record struct BybitDemoExecution(string ExecutionId, string ExchangeOrderId,
    string ClientOrderId, string Symbol, Direction Side, decimal Quantity, decimal Price,
    decimal Fee, decimal ClosedQuantity, long Sequence, DateTimeOffset ExchangeAt,
    DateTimeOffset MessageAt, DateTimeOffset ReceivedAt);

