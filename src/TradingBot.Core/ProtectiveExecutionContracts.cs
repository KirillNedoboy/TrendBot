using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Describes a reduce-only order that protects one already-known position.
/// Protective execution does not acquire entry capacity; it is bounded by the
/// position snapshot and the acknowledged reconciliation revision.
/// </summary>
public sealed record ProtectiveExecutionRequest
{
    [JsonConstructor]
    public ProtectiveExecutionRequest(OrderIntent intent,
        PortfolioPositionSnapshot position, string sourceIntentId,
        long expectedReconciliationRevision)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(position);
        ContractGuard.RequireText(sourceIntentId, nameof(sourceIntentId));
        if (expectedReconciliationRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedReconciliationRevision),
                expectedReconciliationRevision,
                "A protective execution revision cannot be negative.");
        }

        if (intent.Role is OrderRole.Entry)
        {
            throw new ArgumentException(
                "An entry intent cannot use the protective execution path.", nameof(intent));
        }

        if (intent.Role is not (OrderRole.Stop or OrderRole.TakeProfit1
            or OrderRole.TakeProfit2 or OrderRole.Emergency))
        {
            throw new ArgumentException("The order role is not a protective role.", nameof(intent));
        }

        if (!intent.ReduceOnly)
        {
            throw new ArgumentException(
                "Protective execution requires a reduce-only intent.", nameof(intent));
        }

        if (position.Origin != PositionOrigin.Known
            || !StringComparer.Ordinal.Equals(position.IntentId, sourceIntentId))
        {
            throw new ArgumentException(
                "Protective execution requires a known position owned by the source intent.",
                nameof(position));
        }

        if (intent.InstrumentId != position.Position.InstrumentId)
        {
            throw new ArgumentException(
                "The protective intent instrument must match the owned position.", nameof(intent));
        }

        if (intent.Direction == position.Position.Direction)
        {
            throw new ArgumentException(
                "A protective order must use the opposite direction to its position.",
                nameof(intent));
        }

        if (intent.Quantity.Value > position.Position.Quantity.Value)
        {
            throw new ArgumentException(
                "A protective quantity cannot exceed the known position quantity.",
                nameof(intent));
        }

        Intent = intent;
        Position = position;
        SourceIntentId = sourceIntentId;
        ExpectedReconciliationRevision = expectedReconciliationRevision;
    }

    public OrderIntent Intent { get; }

    public PortfolioPositionSnapshot Position { get; }

    public string SourceIntentId { get; }

    public long ExpectedReconciliationRevision { get; }
}

public static class ProtectiveExecutionGuard
{
    public static void Validate(ProtectiveExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = new ProtectiveExecutionRequest(request.Intent, request.Position,
            request.SourceIntentId, request.ExpectedReconciliationRevision);
    }
}
