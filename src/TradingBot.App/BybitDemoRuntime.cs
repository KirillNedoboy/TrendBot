using System.Security.Cryptography;
using System.Text;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

internal readonly record struct BybitDemoPrivateHealth(bool Ready, DateTimeOffset ConfirmedAt, long Revision);
internal sealed record BybitDemoRuntimeResult(string State, int Positions, int Submissions);

/// <summary>One serial, restartable demo-only signal/entry/protection/exit cycle.</summary>
internal sealed class BybitDemoRuntime(SqliteBybitDemoReservationStore store, BybitDemoRestClient rest,
    BybitDemoPublicMarketClient market, BybitDemoChannelMonitor channels, BybitDemoConfiguration configuration,
    IReadOnlySet<string> symbols, TimeProvider clock, Func<BybitDemoPrivateHealth> privateHealth,
    Func<bool> validatePrivateEvents, Func<BybitDemoPreparedEntry, CancellationToken, Task<bool>> approveEntry)
{
    private readonly Dictionary<string, (long Bucket, bool Exit)> _channelCache = new(StringComparer.Ordinal);

    public async Task<BybitDemoRuntimeResult> StepAsync(CancellationToken cancellationToken)
    {
        int submissions = 0;
        bool reconciled = true;
        foreach (BybitDemoReservationSnapshot position in store.ReadActive())
        {
            if (position.State == BybitDemoReservationState.Reserved)
            {
                string? json = store.ReadSource(position.Reservation.SignalId);
                if (json is null || clock.GetUtcNow() - CoreJsonSerializer.Deserialize<BybitDemoSignalCandidate>(json).DecisionAt
                    > TimeSpan.FromSeconds(configuration.MaximumSignalAgeSeconds))
                    store.AbandonUnsent(position.Reservation.SignalId);
                continue;
            }
            try { reconciled &= await ReconcileTradeAsync(position, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException
                or ArgumentException or BybitDemoSubmissionUnknownException or BybitDemoProtectionUnknownException
                or HttpRequestException)
            {
                store.RequireReconciliation(position.Reservation.SignalId);
                reconciled = false;
            }
        }
        reconciled &= validatePrivateEvents();
        BybitDemoPrivateHealth health = privateHealth();
        if (!reconciled || !Fresh(health)) return new("reconciliation-required", store.ReadActive().Count, 0);
        BybitDemoSignalBatch batch = BybitDemoSignalSource.Read(configuration, symbols, clock.GetUtcNow());
        string monitoringState = batch.UnavailableStreams.Count == 0 ? "monitoring"
            : batch.UnavailableStreams.Count == symbols.Count * 4 ? "waiting-for-public-streams" : "monitoring-partial-public-streams";
        string state = monitoringState;
        foreach (BybitDemoSignalCandidate candidate in batch.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BybitDemoReservationSnapshot? existing = store.ReadActive().FirstOrDefault(item => item.Reservation.SignalId == candidate.SignalId);
            if (existing is not null && existing.State != BybitDemoReservationState.Reserved) continue;
            BybitDemoEntryInputs? beforeApproval = await ReadEntryInputsAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (beforeApproval is not { } input) return new("entry-preflight-blocked", store.ReadActive().Count, submissions);
            BybitDemoPlanningStatus planning = BybitDemoEntryPlanner.TryPrepare(configuration, input,
                out BybitDemoPreparedEntry prepared, out BybitDemoEntryBlock reason);
            if (planning != BybitDemoPlanningStatus.Ready)
            {
                state = "entry-blocked:" + planning + ":" + reason;
                continue;
            }
            if (existing is not null && existing.Reservation != prepared.Reservation)
            {
                store.AbandonUnsent(candidate.SignalId);
                continue;
            }
            if (!await approveEntry(prepared, cancellationToken).ConfigureAwait(false))
            {
                state = "awaiting-demo-authorization";
                break;
            }
            BybitDemoEntryInputs? afterApproval = await ReadEntryInputsAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (afterApproval is not { } refreshed) return new("entry-preflight-blocked", store.ReadActive().Count, submissions);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await approveEntry(prepared, cancellationToken).ConfigureAwait(false))
            {
                state = "awaiting-demo-authorization";
                break;
            }
            BybitDemoEntryExecutor executor = new(store, rest, clock, configuration);
            BybitDemoEntryRequestResult result = await executor.RequestAsync(refreshed, cancellationToken).ConfigureAwait(false);
            if (result == BybitDemoEntryRequestResult.Acknowledged) { state = monitoringState; submissions++; break; }
            state = "entry-blocked:" + result;
            if (result is BybitDemoEntryRequestResult.OutcomeUnknown or BybitDemoEntryRequestResult.ReconciliationRequired) break;
        }
        return new(state, store.ReadActive().Count, submissions);
    }

    internal async Task<bool> ReconcileTradeAsync(BybitDemoReservationSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!await ReadOnlyAccountGateAsync(cancellationToken).ConfigureAwait(false)) return false;
        BybitDemoReservation reservation = snapshot.Reservation;
        string signalId = reservation.SignalId;
        DateTimeOffset since = snapshot.ClaimedAt ?? throw new InvalidDataException("The demo claim is missing.");
        BybitDemoSignalCandidate source = CoreJsonSerializer.Deserialize<BybitDemoSignalCandidate>(
            store.ReadSource(signalId) ?? throw new InvalidDataException("The demo source is missing."));
        if (source.SignalId != signalId || source.RecordHash != reservation.SignalRecordHash || source.Symbol != reservation.Symbol
            || source.Family != reservation.Family || source.Timeframe != reservation.Timeframe || source.Direction != reservation.Direction)
            throw new InvalidDataException("The demo source does not match its reservation.");
        BybitDemoOrderFact? entry = await rest.FindOrderAsync(reservation.Symbol, reservation.ClientOrderId,
            snapshot.ExchangeOrderId, cancellationToken).ConfigureAwait(false);
        if (entry is null) return false;
        if (entry.ClientId != reservation.ClientOrderId || entry.ReduceOnly || entry.Side != reservation.Direction
            || entry.Quantity != reservation.Quantity) throw new InvalidDataException("The demo entry differs from its intent.");
        store.RecordAcknowledgement(signalId, reservation.ClientOrderId, entry.Id);
        foreach (BybitDemoExecution fill in await rest.GetOrderExecutionsAsync(entry, since, cancellationToken).ConfigureAwait(false))
        {
            BybitDemoFillResult result = store.RecordEntryFill(new(signalId, fill.ExecutionId, reservation.ClientOrderId,
                entry.Id, fill.Symbol, fill.Side, fill.Quantity, fill.Price, fill.Fee, fill.Sequence, fill.ExchangeAt, fill.ReceivedAt));
            if (result == BybitDemoFillResult.Conflict) throw new InvalidDataException("The demo entry fill conflicts.");
        }
        decimal entered = store.ReadEntryFills(signalId).Sum(item => item.Quantity);
        if (entered != entry.Filled) return false;
        if (!entry.Terminal)
        {
            await rest.CancelOrderAsync(reservation.Symbol, entry.Id, cancellationToken).ConfigureAwait(false);
            return false;
        }
        IReadOnlyList<BybitDemoOrderFact> related = await rest.GetRelatedOrdersAsync(reservation.Symbol, since, cancellationToken).ConfigureAwait(false);
        BybitDemoOpenOrderSnapshot open = await rest.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        foreach (BybitDemoOpenOrder stop in open.Orders.Where(item => item.ParentClientOrderId == reservation.ClientOrderId && item.StopOrderType == "StopLoss"))
        {
            if (!stop.ReduceOnly || !stop.CloseOnTrigger || stop.PositionIndex != 0 || stop.Side == reservation.Direction || stop.TriggerBy != "LastPrice")
                throw new InvalidDataException("The demo stop protection is invalid.");
            RegisterStop(stop.ExchangeOrderId, stop.TriggerPrice, entered);
        }
        foreach (BybitDemoOrderFact stop in related.Where(item => item.ParentId == reservation.ClientOrderId && item.StopType == "StopLoss"))
        {
            if (!stop.ReduceOnly || stop.Side == reservation.Direction) throw new InvalidDataException("The demo stop history is invalid.");
            RegisterStop(stop.Id, stop.Trigger, entered);
        }
        bool exitsReconciled = true;
        foreach (BybitDemoExitOrderState exit in store.ReadExitOrders(signalId))
        {
            BybitDemoOrderFact? fact = await rest.FindOrderAsync(reservation.Symbol, exit.Intent.ClientOrderId,
                exit.ExchangeOrderId, cancellationToken).ConfigureAwait(false);
            if (fact is null) { exitsReconciled = false; continue; }
            if (!fact.ReduceOnly || fact.Side == reservation.Direction || fact.Quantity > exit.Intent.Quantity
                || exit.Intent.Kind != "StopLoss" && fact.ClientId != exit.Intent.ClientOrderId)
                throw new InvalidDataException("The demo exit differs from its intent.");
            if (exit.Intent.Kind is "TP1" or "TP2"
                && (fact.OrderType != "Market" || (exit.Intent.Market ? fact.Trigger is > 0m
                    : fact.Trigger != exit.Intent.TriggerPrice || fact.TriggerBy != "LastPrice"
                        || fact.TriggerDirection != (reservation.Direction == Direction.Long ? 1 : 2))))
                exitsReconciled = false;
            store.ObserveExitOrder(signalId, exit.Intent.ClientOrderId, fact.Id, fact.Status, fact.Filled);
            foreach (BybitDemoExecution fill in await rest.GetOrderExecutionsAsync(fact, since, cancellationToken).ConfigureAwait(false))
                store.RecordExitFill(new(signalId, fill.ExecutionId, fact.Id, fill.Symbol, fill.Side,
                    fill.Quantity, fill.Price, fill.Fee, fill.ExchangeAt, fill.ReceivedAt));
            if (store.ReadExitFills(signalId).Where(item => item.ExchangeOrderId == fact.Id).Sum(item => item.Quantity) != fact.Filled)
                exitsReconciled = false;
        }
        BybitDemoPositionInfo position = await rest.GetPositionAsync(reservation.Symbol, cancellationToken).ConfigureAwait(false);
        decimal remaining = entered - store.ReadExitFills(signalId).Sum(item => item.Quantity);
        if (remaining != position.Quantity || remaining > 0m && position.Direction != reservation.Direction) return false;
        if (remaining == 0m)
        {
            bool waiting = false;
            foreach (BybitDemoExitOrderState exit in store.ReadExitOrders(signalId).Where(item => !SqliteBybitDemoReservationStore.Terminal(item.Status)))
            {
                if (exit.ExchangeOrderId is null) return false;
                await rest.CancelOrderAsync(reservation.Symbol, exit.ExchangeOrderId, cancellationToken).ConfigureAwait(false);
                waiting = true;
            }
            if (waiting) return false;
            store.ReconcilePosition(signalId, 0m, entry.Average ?? 0m, 0m, true, clock.GetUtcNow());
            return true;
        }
        if (position.AverageEntry is not { } average || average <= 0m) return false;
        BybitDemoOpenOrder[] protective = open.Orders.Where(item => item.ParentClientOrderId == reservation.ClientOrderId
            && item.StopOrderType == "StopLoss" && item.Status == "Untriggered" && item.Quantity == remaining
            && item.TriggerPrice == position.StopLoss && item.Side != reservation.Direction && item.ReduceOnly && item.CloseOnTrigger).ToArray();
        if (protective.Length != 1 || position.StopLoss is not { } actualStop || actualStop <= 0m)
        {
            await SubmitExitAsync("Safety", remaining, null, 0m, cancellationToken).ConfigureAwait(false);
            return false;
        }
        BybitDemoInstrument instrument = await market.GetInstrumentAsync(reservation.Symbol, cancellationToken).ConfigureAwait(false);
        BybitDemoExitPlan plan;
        try { plan = BybitDemoExitPlanner.Create(source, instrument, entered, average); }
        catch (ArgumentException)
        {
            await SubmitExitAsync("Safety", remaining, null, 0m, cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (reservation.Family == SetupType.DonchianFlowBreakout)
        {
            long bucket = clock.GetUtcNow().ToUnixTimeSeconds() / (reservation.Timeframe == "30m" ? 1800 : 3600);
            if (!_channelCache.TryGetValue(signalId, out var cut) || cut.Bucket != bucket)
            {
                bool channelExit = await channels.HasExitAsync(reservation.Symbol, reservation.Timeframe,
                    reservation.Direction, store.ReadEntryFills(signalId).Min(fill => fill.ExchangeAt), cancellationToken).ConfigureAwait(false);
                _channelCache[signalId] = (bucket, channelExit);
            }
            if (_channelCache[signalId].Exit)
            {
                await CancelTargetsAsync(cancellationToken).ConfigureAwait(false);
                await SubmitExitAsync("Channel", remaining, null, 0m, cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        decimal desiredStop = store.FilledExitQuantity(signalId, "TP1") >= plan.FirstQuantity
            ? plan.StopAfterFirstTarget ?? plan.StopLoss : plan.StopLoss;
        decimal last = await market.GetLastPriceAsync(reservation.Symbol, cancellationToken).ConfigureAwait(false);
        if (reservation.Direction == Direction.Long ? last <= desiredStop : last >= desiredStop)
        {
            await SubmitExitAsync("Safety", remaining, null, 0m, cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (!exitsReconciled) return false;
        if (store.ReadPendingStopChanges().Any(item => item.SignalId == signalId && item.NewStop != actualStop)) return false;
        store.ReconcilePosition(signalId, remaining, average, actualStop, true, clock.GetUtcNow());
        if (actualStop != desiredStop)
        {
            string command = Id("sl", signalId + ":" + desiredStop.ToString(System.Globalization.CultureInfo.InvariantCulture));
            BybitDemoStopChange change = new(command, signalId, remaining, actualStop, desiredStop, clock.GetUtcNow());
            if (store.TryClaimStopChange(change) == BybitDemoStopClaimResult.Claimed)
            {
                await rest.RequestFullStopAsync(reservation.Symbol, desiredStop, cancellationToken).ConfigureAwait(false);
                store.RecordStopAcknowledgement(command);
            }
            return false;
        }
        if (store.ReadExitPlan(signalId) is null) BybitDemoExitPlanner.PersistProtected(store, source, instrument);
        bool targetsReady = await EnsureTargetAsync("TP1", plan.FirstQuantity, plan.FirstTarget, last, cancellationToken).ConfigureAwait(false);
        if (plan.SecondTarget is { } second)
            targetsReady &= await EnsureTargetAsync("TP2", plan.SecondQuantity, second, last, cancellationToken).ConfigureAwait(false);
        return targetsReady && !store.ReadExitOrders(signalId).Any(item => item.Intent.Kind is "Safety" or "Channel"
            && !SqliteBybitDemoReservationStore.Terminal(item.Status));

        void RegisterStop(string orderId, decimal? trigger, decimal quantity)
        {
            if (quantity <= 0m || trigger is not > 0m) return;
            if (store.ReadExitOrders(signalId).Any(item => item.ExchangeOrderId == orderId)) return;
            store.ClaimExit(new(signalId, Id("sl", orderId), "StopLoss", quantity, trigger, clock.GetUtcNow()), orderId);
        }

        async Task<bool> EnsureTargetAsync(string kind, decimal total, decimal trigger, decimal current, CancellationToken token)
        {
            decimal filled = store.FilledExitQuantity(signalId, kind);
            if (filled >= total) return true;
            BybitDemoExitOrderState[] pending = store.ReadExitOrders(signalId).Where(item => item.Intent.Kind == kind
                && !SqliteBybitDemoReservationStore.Terminal(item.Status)).ToArray();
            if (pending.Length != 0) return pending.All(item => !item.Intent.Market && item.Status == "Untriggered");
            await SubmitExitAsync(kind, Math.Min(remaining, total - filled), trigger, current, token).ConfigureAwait(false);
            return false;
        }

        async Task CancelTargetsAsync(CancellationToken token)
        {
            foreach (BybitDemoExitOrderState exit in store.ReadExitOrders(signalId).Where(item => item.Intent.Kind is "TP1" or "TP2"
                && !SqliteBybitDemoReservationStore.Terminal(item.Status) && item.ExchangeOrderId is not null))
                await rest.CancelOrderAsync(reservation.Symbol, exit.ExchangeOrderId!, token).ConfigureAwait(false);
        }

        async Task SubmitExitAsync(string kind, decimal quantity, decimal? trigger, decimal current, CancellationToken token)
        {
            IReadOnlyList<BybitDemoExitOrderState> saved = store.ReadExitOrders(signalId);
            if (kind is "TP1" or "TP2" && saved.Any(item => item.Intent.Kind is "Safety" or "Channel"
                && !SqliteBybitDemoReservationStore.Terminal(item.Status))) return;
            if (saved.Any(item => item.Intent.Kind == kind && !SqliteBybitDemoReservationStore.Terminal(item.Status))) return;
            string client = Id(kind.ToLowerInvariant(), signalId + ":" + kind + ":" + saved.Count(item => item.Intent.Kind == kind));
            bool marketExit = trigger is null || (reservation.Direction == Direction.Long ? current >= trigger : current <= trigger);
            BybitDemoExitIntent intent = new(signalId, client, kind, quantity, trigger, clock.GetUtcNow(), marketExit);
            if (!store.ClaimExit(intent)) return;
            BybitDemoOrderAcknowledgement ack = marketExit
                ? await rest.PlaceMarketExitAsync(reservation.Symbol, reservation.Direction, quantity,
                    configuration.MaximumSlippagePercent, client, token).ConfigureAwait(false)
                : await rest.PlaceTakeProfitAsync(reservation.Symbol, reservation.Direction, quantity,
                    trigger!.Value, current, client, token).ConfigureAwait(false);
            store.ObserveExitOrder(signalId, client, ack.ExchangeOrderId, "Acknowledged", 0m);
        }
    }

    private bool Fresh(BybitDemoPrivateHealth health) => health.Ready && health.ConfirmedAt <= clock.GetUtcNow()
        && clock.GetUtcNow() - health.ConfirmedAt <= TimeSpan.FromSeconds(configuration.PrivateStreamFreshnessSeconds);

    private async Task<BybitDemoEntryInputs?> ReadEntryInputsAsync(BybitDemoSignalCandidate signal, CancellationToken token)
    {
        BybitDemoPrivateHealth health = privateHealth();
        if (!Fresh(health) || !BybitDemoSignalSource.IsCurrent(configuration, signal, symbols, clock.GetUtcNow())) return null;
        BybitDemoAccountInfo account = await rest.GetAccountInfoAsync(token).ConfigureAwait(false);
        BybitDemoPositionSnapshot positions = await rest.GetOpenPositionsAsync(token).ConfigureAwait(false);
        BybitDemoOpenOrderSnapshot orders = await rest.GetOpenOrdersAsync(token).ConfigureAwait(false);
        if (!AccountMatches(positions, orders) || health.Revision != privateHealth().Revision) return null;
        BybitDemoPositionInfo position = await rest.GetPositionAsync(signal.Symbol, token).ConfigureAwait(false);
        BybitDemoInstrument instrument = await market.GetInstrumentAsync(signal.Symbol, token).ConfigureAwait(false);
        BybitDemoPublicQuote quote = await market.GetQuoteAsync(signal.Symbol, token).ConfigureAwait(false);
        if (health.Revision != privateHealth().Revision || !Fresh(privateHealth())
            || !BybitDemoSignalSource.IsCurrent(configuration, signal, symbols, clock.GetUtcNow())) return null;
        return new(signal, quote, instrument, account, position, BybitDemoAccountComparisonResult.NoMismatchObserved,
            true, true, privateHealth().ConfirmedAt, clock.GetUtcNow());
    }

    private async Task<bool> ReadOnlyAccountGateAsync(CancellationToken token)
    {
        BybitDemoAccountInfo account = await rest.GetAccountInfoAsync(token).ConfigureAwait(false);
        if (account.MarginMode != "ISOLATED_MARGIN") return false;
        BybitDemoPositionSnapshot positions = await rest.GetOpenPositionsAsync(token).ConfigureAwait(false);
        BybitDemoOpenOrderSnapshot orders = await rest.GetOpenOrdersAsync(token).ConfigureAwait(false);
        IReadOnlyList<BybitDemoReservationSnapshot> active = store.ReadActive();
        foreach (BybitDemoAccountPosition position in positions.Positions)
        {
            BybitDemoReservationSnapshot? owner = active.SingleOrDefault(item => item.Reservation.Symbol == position.Symbol
                && item.State != BybitDemoReservationState.Reserved);
            if (owner is null || position.Direction != owner.Reservation.Direction || position.Quantity > owner.Reservation.Quantity
                || position.PositionIndex != 0 || position.Status != "Normal" || position.ReduceOnly
                || position.Leverage is null or < 1m || position.Leverage > configuration.MaximumLeverage) return false;
        }
        foreach (BybitDemoOpenOrder order in orders.Orders)
        {
            BybitDemoReservationSnapshot? owner = active.SingleOrDefault(item => item.Reservation.Symbol == order.Symbol
                && item.State != BybitDemoReservationState.Reserved);
            if (owner is null || order.PositionIndex != 0 || order.Quantity > owner.Reservation.Quantity) return false;
            if (order.ClientOrderId == owner.Reservation.ClientOrderId && order.Side == owner.Reservation.Direction && !order.ReduceOnly) continue;
            if (!order.ReduceOnly || order.Side == owner.Reservation.Direction) return false;
            if (order.StopOrderType == "StopLoss" && order.ParentClientOrderId == owner.Reservation.ClientOrderId
                && order.CloseOnTrigger && order.TriggerBy == "LastPrice") continue;
            if (!store.ReadExitOrders(owner.Reservation.SignalId).Any(exit => exit.ExchangeOrderId == order.ExchangeOrderId
                || exit.Intent.ClientOrderId == order.ClientOrderId)) return false;
        }
        return true;
    }

    private bool AccountMatches(BybitDemoPositionSnapshot positions, BybitDemoOpenOrderSnapshot orders)
    {
        IReadOnlyList<BybitDemoReservationSnapshot> active = store.ReadActive();
        foreach (BybitDemoAccountPosition position in positions.Positions)
        {
            BybitDemoReservationSnapshot? owned = active.SingleOrDefault(item => item.Reservation.Symbol == position.Symbol);
            if (owned is null || owned.State != BybitDemoReservationState.Protected || position.PositionIndex != 0
                || position.Quantity != owned.PositionQuantity || position.Direction != owned.Reservation.Direction
                || position.AverageEntry != owned.AverageEntry || position.StopLoss != owned.StopLoss || position.Status != "Normal"
                || position.ReduceOnly || position.Leverage is null or < 1m || position.Leverage > configuration.MaximumLeverage) return false;
        }
        if (positions.Positions.Count != active.Count(item => item.State == BybitDemoReservationState.Protected)) return false;
        foreach (BybitDemoOpenOrder order in orders.Orders)
        {
            BybitDemoReservationSnapshot? owned = active.SingleOrDefault(item => item.Reservation.Symbol == order.Symbol);
            if (owned is null || !order.ReduceOnly || order.Side == owned.Reservation.Direction || order.PositionIndex != 0) return false;
            if (order.StopOrderType == "StopLoss" && order.ParentClientOrderId == owned.Reservation.ClientOrderId
                && order.TriggerBy == "LastPrice" && order.TriggerPrice == owned.StopLoss && order.CloseOnTrigger) continue;
            if (!store.ReadExitOrders(owned.Reservation.SignalId).Any(item => item.ExchangeOrderId == order.ExchangeOrderId
                && item.Intent.ClientOrderId == order.ClientOrderId && item.Intent.Quantity >= order.Quantity)) return false;
        }
        return true;
    }

    private static string Id(string prefix, string source) => prefix + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..(35 - prefix.Length)];
}
