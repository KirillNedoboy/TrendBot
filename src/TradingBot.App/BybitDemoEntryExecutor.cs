using TradingBot.Infrastructure;

namespace TradingBot.App;

internal enum BybitDemoEntryRequestResult
{
    Acknowledged,
    OutcomeUnknown,
    Rejected,
    ReservationUnavailable,
    AlreadyClaimed,
    ReconciliationRequired
}

/// <summary>Claims an admitted demo entry before submitting it; acceptance is not a fill.</summary>
internal sealed class BybitDemoEntryExecutor(
    SqliteBybitDemoReservationStore store, BybitDemoRestClient client,
    TimeProvider clock, BybitDemoConfiguration configuration)
{
    private readonly SqliteBybitDemoReservationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly BybitDemoRestClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly BybitDemoConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public async Task<BybitDemoEntryRequestResult> RequestAsync(BybitDemoEntryInputs inputs, CancellationToken cancellationToken)
    {
        if (_store.Capacity != _configuration.MaximumPositions)
        {
            throw new InvalidOperationException("The demo store capacity does not match the configured limit.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        inputs = inputs with { Now = _clock.GetUtcNow() };
        if (BybitDemoEntryPlanner.TryPrepare(_configuration, inputs, out BybitDemoPreparedEntry prepared, out _)
            != BybitDemoPlanningStatus.Ready)
        {
            return BybitDemoEntryRequestResult.Rejected;
        }
        BybitDemoReservationResult result = _store.TryReserve(prepared.Reservation);
        if (result is not (BybitDemoReservationResult.Reserved or BybitDemoReservationResult.AlreadyReserved))
        {
            return result == BybitDemoReservationResult.ReconciliationRequired
                ? BybitDemoEntryRequestResult.ReconciliationRequired : BybitDemoEntryRequestResult.ReservationUnavailable;
        }
        _store.SaveSource(prepared.Reservation.SignalId, TradingBot.Core.CoreJsonSerializer.Serialize(prepared.Source));
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset claimTime = _clock is BybitDemoClock calibrated ? calibrated.ClaimLowerBound() : _clock.GetUtcNow();
        DateTimeOffset claimedAt = DateTimeOffset.FromUnixTimeMilliseconds(claimTime.ToUnixTimeMilliseconds());
        if (!_store.TryClaimDispatch(prepared.Reservation.SignalId, claimedAt))
        {
            return BybitDemoEntryRequestResult.AlreadyClaimed;
        }
        if (BybitDemoEntryPlanner.TryPrepare(_configuration, inputs with { Now = _clock.GetUtcNow() },
                out BybitDemoPreparedEntry checkedEntry, out _) != BybitDemoPlanningStatus.Ready
            || checkedEntry != prepared || cancellationToken.IsCancellationRequested)
        {
            _store.RequireReconciliation(prepared.Reservation.SignalId);
            return BybitDemoEntryRequestResult.ReconciliationRequired;
        }
        try
        {
            BybitDemoReservation order = prepared.Reservation;
            BybitDemoOrderAcknowledgement acknowledgement = await _client.PlaceMarketEntryAsync(order.Symbol,
                order.Direction, order.Quantity, order.InitialStop, _configuration.MaximumSlippagePercent,
                order.ClientOrderId, cancellationToken).ConfigureAwait(false);
            _store.RecordAcknowledgement(order.SignalId, acknowledgement.ClientOrderId, acknowledgement.ExchangeOrderId);
            return BybitDemoEntryRequestResult.Acknowledged;
        }
        catch (Exception exception) when (exception is BybitDemoSubmissionUnknownException or OperationCanceledException)
        {
            _store.RequireReconciliation(prepared.Reservation.SignalId);
            return BybitDemoEntryRequestResult.OutcomeUnknown;
        }
    }
}
