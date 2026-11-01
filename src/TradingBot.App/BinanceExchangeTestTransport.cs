using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>
/// The concrete exchange-test transport. It is intentionally constructed only
/// by <see cref="ExchangeTestRuntime" />, which has already pinned the private
/// REST and WebSocket clients to the Binance USD-M test origins.
/// </summary>
public sealed class BinanceExchangeTestTransport : IExchangeTestTransport
{
    private readonly ExchangeTestRuntime _runtime;
    private readonly ExchangeTestRunMetadata _metadata;
    private readonly PortfolioId _portfolioId;
    private readonly TimeSpan _freshnessWindow;
    private readonly TimeProvider _timeProvider;

    internal BinanceExchangeTestTransport(ExchangeTestRuntime runtime,
        ExchangeTestRunMetadata metadata, PortfolioId portfolioId,
        TimeSpan freshnessWindow, TimeProvider timeProvider)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _portfolioId = portfolioId ?? throw new ArgumentNullException(nameof(portfolioId));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(freshnessWindow, TimeSpan.Zero);

        _freshnessWindow = freshnessWindow;
        if (!StringComparer.Ordinal.Equals(metadata.ConfigurationId,
                runtime.Configuration.ConfigurationId)
            || !StringComparer.Ordinal.Equals(metadata.EnvironmentId,
                runtime.Configuration.EnvironmentId))
        {
            throw new InvalidOperationException(
                "The exchange-test transport metadata does not match its runtime configuration.");
        }
    }

    public async ValueTask<ExchangeTestTransportResult> SubmitAsync(OrderIntent intent,
        ExecutionDispatchAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(attempt);
        EnsureAttempt(intent, attempt);

        string listenKey = await _runtime.RestClient.StartUserDataStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using CancellationTokenSource streamCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        TaskCompletionSource<bool> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using IAsyncEnumerator<BinancePrivateStreamEnvelope> streamReader =
            _runtime.PrivateWebSocketClient.ReadAsync(listenKey,
                () => connected.TrySetResult(true), streamCancellation.Token)
            .GetAsyncEnumerator(streamCancellation.Token);
        ValueTask<bool> firstStreamMove = streamReader.MoveNextAsync();
        try
        {
            DateTimeOffset submitStartedAt = _timeProvider.GetUtcNow();
            await connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            BinancePrivateOrder submitted = await _runtime.RestClient.PlaceOrderAsync(
                ToBinanceRequest(intent), cancellationToken).ConfigureAwait(false);
            StreamBatch stream = await ReadMatchingOrderUpdateAsync(streamReader,
                firstStreamMove, intent.ClientOrderId, intent.IntentId, submitStartedAt,
                cancellationToken).ConfigureAwait(false);
            BinancePrivateOrder observed = stream.Order
                ?? throw new BinancePrivateProtocolException(
                    "The private stream did not confirm the exchange-test order.");
            if (submitted.OrderId != 0 && observed.OrderId != 0
                && submitted.OrderId != observed.OrderId)
            {
                throw new BinancePrivateProtocolException(
                    "REST and private-stream order identities disagree.");
            }

            ExecutionObservation observation = ToObservation(observed, stream.OrderReceiptId,
                intent.IntentId);
            Fill? fill = ToSingleFill(stream.OrderEvent, intent.IntentId);
            List<ExchangeTestEventReceipt> receipts = [.. stream.Receipts];
            List<PortfolioPositionSnapshot> positionSnapshots = [];
            bool protectionConfirmed = true;
            if (observation.Type == ExecutionObservationType.PartialFill)
            {
                if (fill is null)
                {
                    throw new BinancePrivateProtocolException(
                        "A partial exchange-test fill has no private trade evidence.");
                }

                if (intent.ReduceOnly)
                {
                    protectionConfirmed = observed.ReduceOnly;
                }
                else
                {
                    ExchangeTestProtectionResult protection = await PlacePartialProtectionAsync(
                        intent, attempt, observation, fill, receipts, cancellationToken)
                        .ConfigureAwait(false);
                    receipts.Add(protection.Receipt);
                    positionSnapshots.Add(protection.PositionSnapshot);
                    protectionConfirmed = protection.Confirmed;
                }
            }

            ReconciliationCheckpoint? checkpoint = fill is null
                ? null
                : BuildCheckpoint(intent, attempt, observation, positionSnapshots);
            return new ExchangeTestTransportResult(observation, receipts,
                protectionConfirmed, fill, positionSnapshots, checkpoint);
        }
        finally
        {
            streamCancellation.Cancel();
            await CloseListenKeyAsync(listenKey, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<ExecutionRecoveryEvidence> ReconcileAsync(OrderIntent intent,
        PortfolioId portfolioId, ExecutionStateSnapshot state,
        ReconciliationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(checkpoint);
        EnsureAttemptIdentity(intent, state);
        if (portfolioId != _portfolioId)
        {
            throw new InvalidOperationException(
                "The exchange-test reconciliation portfolio is not bound to this transport.");
        }

        string? listenKey = null;
        try
        {
            BinancePrivateOrder? order;
            try
            {
                order = await _runtime.RestClient.QueryOrderAsync(intent.InstrumentId.Value, null,
                    intent.ClientOrderId, cancellationToken).ConfigureAwait(false);
            }
            catch (BinanceApiException exception) when (exception.Code == -2013)
            {
                // A bounded all-orders response is useful evidence for the
                // audit trail, but an empty/limited history must never prove
                // that the timed-out request was not accepted.
                _ = await _runtime.RestClient.GetAllOrdersAsync(
                    intent.InstrumentId.Value, limit: 100, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                BinancePositionMode missingMode = await _runtime.RestClient.GetPositionModeAsync(
                    cancellationToken).ConfigureAwait(false);
                IReadOnlyList<BinancePrivatePosition> missingPositions =
                    await _runtime.RestClient.GetPositionsAsync(intent.InstrumentId.Value,
                        cancellationToken).ConfigureAwait(false);
                PositionAssessment missingAssessment = AssessPositions(intent, null,
                    missingMode, missingPositions);
                return BuildMissingOrderEvidence(intent, checkpoint, state, missingAssessment);
            }

            if (order is null)
            {
                throw new BinancePrivateProtocolException(
                    "The exchange-test order query returned no identity.");
            }

            BinancePositionMode positionMode = await _runtime.RestClient.GetPositionModeAsync(
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<BinancePrivatePosition> positions =
                await _runtime.RestClient.GetPositionsAsync(intent.InstrumentId.Value,
                    cancellationToken).ConfigureAwait(false);
            PositionAssessment assessment = AssessPositions(intent, order, positionMode, positions);
            if (assessment.Status == ExecutionRecoveryEvidenceStatus.Manual)
            {
                return BuildKnownOrderEvidence(intent, checkpoint, order, assessment,
                    Array.Empty<BinancePrivateTrade>());
            }

            listenKey = await _runtime.RestClient.StartUserDataStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset streamStartedAt = _timeProvider.GetUtcNow();
            using CancellationTokenSource streamCancellation = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            TaskCompletionSource<bool> connected = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using IAsyncEnumerator<BinancePrivateStreamEnvelope> streamReader =
                _runtime.PrivateWebSocketClient.ReadAsync(listenKey,
                    () => connected.TrySetResult(true), streamCancellation.Token)
                .GetAsyncEnumerator(streamCancellation.Token);
            ValueTask<bool> firstStreamMove = streamReader.MoveNextAsync();
            await connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            StreamBatch stream = await ReadMatchingOrderUpdateAsync(streamReader,
                firstStreamMove, intent.ClientOrderId, intent.IntentId, streamStartedAt,
                cancellationToken).ConfigureAwait(false);
            BinancePrivateOrder observed = stream.Order
                ?? throw new BinancePrivateProtocolException(
                    "Private-stream reconciliation did not confirm the queried order.");
            if (order.OrderId != 0 && observed.OrderId != 0 && order.OrderId != observed.OrderId)
            {
                throw new BinancePrivateProtocolException(
                    "Reconciliation REST and private-stream identities disagree.");
            }

            IReadOnlyList<BinancePrivateTrade> trades = await _runtime.RestClient.GetUserTradesAsync(
                intent.InstrumentId.Value, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<BinancePrivatePosition> currentPositions =
                await _runtime.RestClient.GetPositionsAsync(intent.InstrumentId.Value,
                    cancellationToken).ConfigureAwait(false);
            PositionAssessment streamAssessment = AssessPositions(intent, observed, positionMode,
                currentPositions);
            if (streamAssessment.Status == ExecutionRecoveryEvidenceStatus.Manual)
            {
                return BuildKnownOrderEvidence(intent, checkpoint, observed, streamAssessment,
                    trades);
            }

            if (observed.ExecutedQuantity > 0m
                && !trades.Any(trade => trade.OrderId == observed.OrderId
                    && trade.Quantity > 0m))
            {
                throw new BinancePrivateProtocolException(
                    "A filled exchange-test order has no bounded private trade identity.");
            }

            ExecutionObservation observation = ToObservation(observed, stream.OrderReceiptId,
                intent.IntentId, isReconciliation: true);
            string[] fillIds = trades.Where(trade => trade.OrderId == observed.OrderId)
                .Select(trade => $"binance-trade:{trade.TradeId}").Distinct(StringComparer.Ordinal)
                .ToArray();
            return new ExecutionRecoveryEvidence(intent.IntentId, checkpoint.Revision,
                ExecutionRecoveryEvidenceStatus.Fresh, true, true, true,
                streamAssessment.PositionsKnown, state.State != ExecutionLifecycleState.Conflict,
                new ImmutableValueList<ExecutionObservation>([observation]),
                new ImmutableValueList<string>(fillIds),
                new ImmutableValueList<string>(streamAssessment.PositionIds),
                new ImmutableValueList<string>(Array.Empty<string>()));
        }
        catch (BinancePrivateProtocolException)
        {
            return new ExecutionRecoveryEvidence(intent.IntentId, checkpoint.Revision,
                ExecutionRecoveryEvidenceStatus.Stale, false, false, false, false, false,
                new ImmutableValueList<ExecutionObservation>(Array.Empty<ExecutionObservation>()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ExecutionRecoveryEvidence(intent.IntentId, checkpoint.Revision,
                ExecutionRecoveryEvidenceStatus.Unknown, false, false, false, false, false,
                new ImmutableValueList<ExecutionObservation>(Array.Empty<ExecutionObservation>()));
        }
        finally
        {
            if (listenKey is not null)
            {
                await CloseListenKeyAsync(listenKey, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<StreamBatch> ReadMatchingOrderUpdateAsync(
        IAsyncEnumerator<BinancePrivateStreamEnvelope> streamReader,
        ValueTask<bool> firstStreamMove, string clientOrderId, string intentId,
        DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        List<ExchangeTestEventReceipt> receipts = [];
        // notBefore is intentionally not used as a hard lower bound. Binance
        // exchange timestamps can precede local receive time because of clock
        // skew, while EnsureFresh still rejects stale/replayed evidence.
        _ = notBefore;
        bool hasCurrent = await firstStreamMove.ConfigureAwait(false);
        while (hasCurrent)
        {
            BinancePrivateStreamEnvelope envelope = streamReader.Current;
            EnsureFresh(envelope);

            ExchangeTestEventReceipt receipt = Normalize(envelope, intentId);
            receipts.Add(receipt);
            if (envelope.Payload is BinanceListenKeyExpiredEvent)
            {
                throw new BinancePrivateProtocolException(
                    "The Binance private listen key expired during exchange-test execution.");
            }

            if (envelope.Payload is BinanceAlgoUpdateEvent)
            {
                throw new BinancePrivateProtocolException(
                    "ALGO_UPDATE requires a dedicated Algo REST reconciliation before execution can continue.");
            }

            if (envelope.Payload is BinanceOrderTradeUpdateEvent orderUpdate
                && StringComparer.Ordinal.Equals(orderUpdate.Order.ClientOrderId, clientOrderId))
            {
                return new StreamBatch(orderUpdate.Order, orderUpdate, receipt.Identity,
                    receipts.AsReadOnly());
            }

            hasCurrent = await streamReader.MoveNextAsync().ConfigureAwait(false);
        }

        throw new BinancePrivateProtocolException(
            "The Binance private stream ended without a fresh matching order update.");
    }

    private async ValueTask<ExchangeTestProtectionResult> PlacePartialProtectionAsync(
        OrderIntent sourceIntent, ExecutionDispatchAttempt attempt,
        ExecutionObservation observation, Fill fill,
        List<ExchangeTestEventReceipt> existingReceipts,
        CancellationToken cancellationToken)
    {
        if (observation.Quantity <= 0m)
        {
            throw new BinancePrivateProtocolException(
                "A partial exchange-test observation has no positive remainder quantity.");
        }

        Position position = new($"binance:{sourceIntent.InstrumentId.Value}:Both",
            sourceIntent.InstrumentId, sourceIntent.Direction,
            new Quantity(observation.Quantity), fill.EventTime, fill.AvailabilityTime,
            fill.Price);
        PortfolioPositionSnapshot snapshot = new(_portfolioId, position,
            sourceIntent.IntentId, PositionOrigin.Known);
        Direction protectiveDirection = sourceIntent.Direction == Direction.Long
            ? Direction.Short : Direction.Long;
        OrderIntent protectiveIntent = new(
            $"{sourceIntent.IntentId}:partial-protection:{attempt.Attempt}",
            sourceIntent.StrategyRunId, sourceIntent.SetupId,
            $"{sourceIntent.ClientOrderId}:partial-protection:{attempt.Attempt}",
            sourceIntent.InstrumentId, protectiveDirection, OrderRole.Stop,
            attempt.Attempt, new Quantity(observation.Quantity), fill.Price,
            reduceOnly: true);
        ProtectiveExecutionRequest request = new(protectiveIntent, snapshot,
            sourceIntent.IntentId, attempt.ReconciliationRevision);
        BinancePositionMode positionMode = await _runtime.RestClient.GetPositionModeAsync(
            cancellationToken).ConfigureAwait(false);
        BinanceProtectiveOrderMapping mapping = BinanceProtectiveOrderMapper.Map(request,
            positionMode);
        if (mapping.AlgoOrderType is not { } algoOrderType || mapping.TriggerPrice is null)
        {
            throw new BinancePrivateProtocolException(
                "A partial entry protection must map to a triggered Binance Algo order.");
        }

        string clientAlgoId = CreateProtectiveClientId(sourceIntent, attempt);
        BinancePrivateAlgoOrderRequest algoRequest = new(mapping.Symbol, mapping.Side,
            algoOrderType, mapping.Quantity, clientAlgoId, mapping.TriggerPrice,
            mapping.PositionSide, mapping.ReduceOnly);
        BinancePrivateAlgoOrder placed = await _runtime.RestClient.PlaceAlgoOrderAsync(
            algoRequest, cancellationToken).ConfigureAwait(false);
        ValidateProtectiveAlgo(placed, mapping, clientAlgoId, observation.Quantity);
        BinancePrivateAlgoOrder confirmed = await _runtime.RestClient.QueryAlgoOrderAsync(
            mapping.Symbol, placed.AlgoId > 0 ? placed.AlgoId : null, clientAlgoId,
            cancellationToken).ConfigureAwait(false);
        ValidateProtectiveAlgo(confirmed, mapping, clientAlgoId, observation.Quantity);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        long sequence = existingReceipts.Count == 0
            ? 0L : checked(existingReceipts.Max(receipt => receipt.StableSequence) + 1L);
        string identity = $"{_metadata.RunId}:algo:{clientAlgoId}";
        string orderId = confirmed.AlgoId > 0
            ? confirmed.AlgoId.ToString(CultureInfo.InvariantCulture) : clientAlgoId;
        string canonical = string.Join('|', identity, sourceIntent.IntentId,
            "ALGO_UPDATE", orderId, confirmed.Status,
            observation.Quantity.ToString("G29", CultureInfo.InvariantCulture));
        string fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical)));
        ExchangeTestEventReceipt receipt = new(_metadata.RunId, identity,
            sourceIntent.IntentId, "ALGO_UPDATE", sequence, now, now, now, orderId,
            observation.Quantity, confirmed.Status, confirmed.RequiresReconciliation,
            fingerprint);
        return new ExchangeTestProtectionResult(true, receipt, snapshot);
    }

    private static string CreateProtectiveClientId(OrderIntent intent,
        ExecutionDispatchAttempt attempt)
    {
        string canonical = $"{intent.IntentId}:{intent.ClientOrderId}:{attempt.Attempt}";
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return $"tbp-{digest[..24]}";
    }

    private static void ValidateProtectiveAlgo(BinancePrivateAlgoOrder order,
        BinanceProtectiveOrderMapping mapping, string clientAlgoId, decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!StringComparer.Ordinal.Equals(order.ClientAlgoId, clientAlgoId)
            || !StringComparer.OrdinalIgnoreCase.Equals(order.Symbol, mapping.Symbol)
            || order.Side != mapping.Side
            || order.PositionSide != BinancePositionSide.Both
            || !order.ReduceOnly
            || order.Quantity != quantity
            || order.TriggerPrice != mapping.TriggerPrice?.Value
            || order.OrderType != mapping.AlgoOrderType
            || order.Status is not ("NEW" or "WORKING"))
        {
            throw new BinancePrivateProtocolException(
                "The Binance protective Algo response did not confirm the requested reduce-only remainder.");
        }
    }

    private static PositionAssessment AssessPositions(OrderIntent intent,
        BinancePrivateOrder? order, BinancePositionMode positionMode,
        IReadOnlyList<BinancePrivatePosition> positions)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(positions);
        string symbol = intent.InstrumentId.Value;
        BinancePrivatePosition[] active = positions
            .Where(position => StringComparer.OrdinalIgnoreCase.Equals(position.Symbol, symbol)
                && Math.Abs(position.PositionAmount) > 0m)
            .ToArray();
        string[] positionIds = active.Select(PositionIdentity)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (positionMode != BinancePositionMode.OneWay)
        {
            return new PositionAssessment(ExecutionRecoveryEvidenceStatus.Manual, false,
                positionIds);
        }

        if (order is null)
        {
            return active.Length == 0
                ? new PositionAssessment(ExecutionRecoveryEvidenceStatus.Fresh, true,
                    positionIds)
                : new PositionAssessment(ExecutionRecoveryEvidenceStatus.Manual, false,
                    positionIds);
        }

        if (order.Status == BinanceOrderStatus.Unknown || active.Length > 1)
        {
            return new PositionAssessment(ExecutionRecoveryEvidenceStatus.Manual, false,
                positionIds);
        }

        decimal expectedQuantity = order.ExecutedQuantity;
        if (expectedQuantity <= 0m)
        {
            return active.Length == 0
                ? new PositionAssessment(ExecutionRecoveryEvidenceStatus.Fresh, true,
                    positionIds)
                : new PositionAssessment(ExecutionRecoveryEvidenceStatus.Manual, false,
                    positionIds);
        }

        if (active.Length != 1)
        {
            return new PositionAssessment(ExecutionRecoveryEvidenceStatus.Manual, false,
                positionIds);
        }

        BinancePrivatePosition position = active[0];
        Direction expectedDirection = order.Side == BinanceOrderSide.Buy
            ? Direction.Long : Direction.Short;
        Direction actualDirection = position.PositionAmount < 0m
            ? Direction.Short : Direction.Long;
        return position.PositionSide == BinancePositionSide.Both
            && actualDirection == expectedDirection
            && position.PositionAmount is not 0m
            && Math.Abs(position.PositionAmount) == expectedQuantity
            ? new PositionAssessment(ExecutionRecoveryEvidenceStatus.Fresh, true,
                positionIds)
            : new PositionAssessment(ExecutionRecoveryEvidenceStatus.Manual, false,
                positionIds);
    }

    private static string PositionIdentity(BinancePrivatePosition position) =>
        $"binance:{position.Symbol}:{position.PositionSide}";

    private static ExecutionRecoveryEvidence BuildMissingOrderEvidence(OrderIntent intent,
        ReconciliationCheckpoint checkpoint, ExecutionStateSnapshot state,
        PositionAssessment assessment)
    {
        if (assessment.Status == ExecutionRecoveryEvidenceStatus.Manual)
        {
            return new ExecutionRecoveryEvidence(intent.IntentId, checkpoint.Revision,
                ExecutionRecoveryEvidenceStatus.Manual, false, false, false, false, false,
                new ImmutableValueList<ExecutionObservation>(Array.Empty<ExecutionObservation>()),
                positionIds: new ImmutableValueList<string>(assessment.PositionIds));
        }

        // The response came from a bounded history query. Its absence is not
        // complete evidence that no order exists, so retry remains forbidden.
        return new ExecutionRecoveryEvidence(intent.IntentId, checkpoint.Revision,
            ExecutionRecoveryEvidenceStatus.Fresh, false, false, false,
            assessment.PositionsKnown, state.State != ExecutionLifecycleState.Conflict,
            new ImmutableValueList<ExecutionObservation>(Array.Empty<ExecutionObservation>()),
            positionIds: new ImmutableValueList<string>(assessment.PositionIds));
    }

    private static ExecutionRecoveryEvidence BuildKnownOrderEvidence(OrderIntent intent,
        ReconciliationCheckpoint checkpoint, BinancePrivateOrder? order,
        PositionAssessment assessment, IReadOnlyList<BinancePrivateTrade> trades)
    {
        string[] fillIds = order is null ? [] : trades
            .Where(trade => trade.OrderId == order.OrderId)
            .Select(trade => $"binance-trade:{trade.TradeId}")
            .Distinct(StringComparer.Ordinal).ToArray();
        if (assessment.Status == ExecutionRecoveryEvidenceStatus.Manual)
        {
            return new ExecutionRecoveryEvidence(intent.IntentId, checkpoint.Revision,
                ExecutionRecoveryEvidenceStatus.Manual, false, false, order is not null,
                false, false,
                new ImmutableValueList<ExecutionObservation>(Array.Empty<ExecutionObservation>()),
                new ImmutableValueList<string>(fillIds),
                new ImmutableValueList<string>(assessment.PositionIds));
        }

        return new ExecutionRecoveryEvidence(intent.IntentId, checkpoint.Revision,
            ExecutionRecoveryEvidenceStatus.Fresh, true, true, order is not null,
            assessment.PositionsKnown, true,
            new ImmutableValueList<ExecutionObservation>(Array.Empty<ExecutionObservation>()),
            new ImmutableValueList<string>(fillIds),
            new ImmutableValueList<string>(assessment.PositionIds));
    }

    private void EnsureFresh(BinancePrivateStreamEnvelope envelope)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset available = envelope.AvailabilityTime.Value;
        if (available > now || now - available > _freshnessWindow)
        {
            throw new BinancePrivateProtocolException(
                "The Binance private stream evidence is stale or has an invalid future timestamp.");
        }
    }

    private ExchangeTestEventReceipt Normalize(BinancePrivateStreamEnvelope envelope,
        string intentId)
    {
        BinancePrivateOrder? order = envelope.Payload is BinanceOrderTradeUpdateEvent update
            ? update.Order
            : null;
        decimal fillQuantity = envelope.Payload is BinanceOrderTradeUpdateEvent orderUpdate
            ? orderUpdate.Fills.Sum(static fill => fill.Quantity)
            : 0m;
        string lifecycle = order?.StatusText
            ?? (envelope.Payload is BinanceAlgoUpdateEvent algo ? "ALGO_UPDATE"
                : envelope.Payload.EventType);
        bool requiresReconciliation = envelope.Payload is BinanceAlgoUpdateEvent
            or BinanceListenKeyExpiredEvent
            || order?.RequiresReconciliation == true;
        string identity = $"{_metadata.RunId}:{envelope.SessionId}:{envelope.StableSequence}:{envelope.Payload.EventType}";
        string canonical = string.Join('|', identity, intentId, envelope.Payload.EventType,
            envelope.Payload.EventTimeMilliseconds.ToString(CultureInfo.InvariantCulture),
            order?.OrderId.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            fillQuantity.ToString("G29", CultureInfo.InvariantCulture), lifecycle,
            requiresReconciliation ? "1" : "0");
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new ExchangeTestEventReceipt(_metadata.RunId, identity, intentId,
            envelope.Payload.EventType, envelope.StableSequence,
            envelope.Payload.EventTime.Value, envelope.ReceiveTime.Value,
            envelope.AvailabilityTime.Value,
            order?.OrderId.ToString(CultureInfo.InvariantCulture), fillQuantity > 0m
                ? fillQuantity : null, lifecycle, requiresReconciliation, fingerprint);
    }

    private static Fill? ToSingleFill(BinanceOrderTradeUpdateEvent? update, string intentId)
    {
        if (update is null || update.Fills.Count == 0)
        {
            return null;
        }

        if (update.Fills.Count != 1)
        {
            throw new BinancePrivateProtocolException(
                "Multiple private fills require a transaction batch not available to this adapter.");
        }

        return update.Fills[0].ToCoreFill(update.ReceiveTime, update.AvailabilityTime,
            update.StableSequence, intentId);
    }

    private static ExecutionObservation ToObservation(BinancePrivateOrder order,
        string receiptId, string intentId, bool isReconciliation = false) => order.Status switch
        {
            BinanceOrderStatus.New => ExecutionObservation.Accepted(receiptId, intentId,
                order.OrderId.ToString(CultureInfo.InvariantCulture), isReconciliation),
            BinanceOrderStatus.PartiallyFilled => ExecutionObservation.PartialFill(receiptId,
                intentId, order.OrderId.ToString(CultureInfo.InvariantCulture), order.ExecutedQuantity,
                isReconciliation),
            BinanceOrderStatus.Filled => ExecutionObservation.Filled(receiptId, intentId,
                order.OrderId.ToString(CultureInfo.InvariantCulture), order.ExecutedQuantity,
                isReconciliation),
            BinanceOrderStatus.Canceled or BinanceOrderStatus.Expired or BinanceOrderStatus.ExpiredInMatch
                => ExecutionObservation.Canceled(receiptId, intentId,
                    order.OrderId.ToString(CultureInfo.InvariantCulture), isReconciliation),
            _ => ExecutionObservation.Unknown(receiptId, intentId, isReconciliation)
        };

    private ReconciliationCheckpoint BuildCheckpoint(OrderIntent intent,
        ExecutionDispatchAttempt attempt, ExecutionObservation observation,
        IReadOnlyList<PortfolioPositionSnapshot> positionSnapshots) =>
        new(_portfolioId, checked(attempt.ReconciliationRevision + 1),
            observation.Type is ExecutionObservationType.Filled
                or ExecutionObservationType.Canceled
                or ExecutionObservationType.Rejected
                ? Array.Empty<string>() : [intent.IntentId],
            positionSnapshots.Select(snapshot => snapshot.Position.PositionId).ToArray(),
            Array.Empty<string>(), Array.Empty<string>());

    private static BinancePrivateOrderRequest ToBinanceRequest(OrderIntent intent)
    {
        bool reduceOnly = intent.ReduceOnly || intent.Role != OrderRole.Entry;
        BinanceOrderSide side = intent.Direction == Direction.Long
            ? (reduceOnly ? BinanceOrderSide.Sell : BinanceOrderSide.Buy)
            : (reduceOnly ? BinanceOrderSide.Buy : BinanceOrderSide.Sell);
        return new BinancePrivateOrderRequest(intent.InstrumentId.Value, side,
            intent.LimitPrice is null ? BinanceOrderType.Market : BinanceOrderType.Limit,
            intent.Quantity, intent.ClientOrderId, intent.LimitPrice,
            intent.LimitPrice is null ? null : BinanceTimeInForce.GoodTillCancel,
            BinancePositionSide.Both, reduceOnly);
    }

    private static void EnsureAttempt(OrderIntent intent, ExecutionDispatchAttempt attempt)
    {
        if (!StringComparer.Ordinal.Equals(intent.IntentId, attempt.IntentId))
        {
            throw new InvalidOperationException(
                "The exchange-test attempt does not belong to the supplied intent.");
        }
    }

    private static void EnsureAttemptIdentity(OrderIntent intent, ExecutionStateSnapshot state)
    {
        if (!StringComparer.Ordinal.Equals(intent.IntentId, state.IntentId)
            || !StringComparer.Ordinal.Equals(intent.ClientOrderId, state.ClientOrderId))
        {
            throw new InvalidOperationException(
                "The exchange-test reconciliation state does not belong to the intent.");
        }
    }

    private async ValueTask CloseListenKeyAsync(string listenKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await _runtime.RestClient.CloseUserDataStreamAsync(listenKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Closing a test stream is best-effort; the order outcome remains
            // governed by the fresh REST/private-stream evidence above.
        }
    }

    private sealed record PositionAssessment(ExecutionRecoveryEvidenceStatus Status,
        bool PositionsKnown, IReadOnlyList<string> PositionIds);

    private sealed record ExchangeTestProtectionResult(bool Confirmed,
        ExchangeTestEventReceipt Receipt, PortfolioPositionSnapshot PositionSnapshot);

    private sealed record StreamBatch(BinancePrivateOrder? Order,
        BinanceOrderTradeUpdateEvent? OrderEvent, string OrderReceiptId,
        IReadOnlyList<ExchangeTestEventReceipt> Receipts);
}
