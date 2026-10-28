using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>One complete, caller-supplied evaluation snapshot for shadow mode.</summary>
public sealed record OperationalShadowEvaluation
{
    public DecisionEngineInput DecisionInput { get; }
    public DecisionEngineConfiguration DecisionConfiguration { get; }
    public RiskEvaluationInput RiskInput { get; }
    public RiskEvaluationConfiguration RiskConfiguration { get; }
    public IRiskMathCalculator RiskMathCalculator { get; }

    public OperationalShadowEvaluation(DecisionEngineInput decisionInput,
        DecisionEngineConfiguration decisionConfiguration, RiskEvaluationInput riskInput,
        RiskEvaluationConfiguration riskConfiguration, IRiskMathCalculator riskMathCalculator)
    {
        ArgumentNullException.ThrowIfNull(decisionInput);
        ArgumentNullException.ThrowIfNull(decisionConfiguration);
        ArgumentNullException.ThrowIfNull(riskInput);
        ArgumentNullException.ThrowIfNull(riskConfiguration);
        ArgumentNullException.ThrowIfNull(riskMathCalculator);
        DecisionInput = decisionInput;
        DecisionConfiguration = decisionConfiguration;
        RiskInput = riskInput;
        RiskConfiguration = riskConfiguration;
        RiskMathCalculator = riskMathCalculator;
    }

    public bool IsConfigurationValid => DecisionConfiguration.Profile == DecisionEngineProfile.Shadow;
}

/// <summary>One causal public-market observation consumed by the shadow runtime.</summary>
public sealed record OperationalShadowMarketObservation
{
    public string StreamId { get; }
    public CausalMarketEvent CausalEvent { get; }
    public bool DataFresh { get; }
    public bool ReconnectReady { get; }
    public bool RecoveryComplete { get; }
    public bool KillSwitchEnabled { get; }
    public bool KillSwitchKnown { get; }
    public OperationalShadowEvaluation? Evaluation { get; }

    public OperationalShadowMarketObservation(string streamId, CausalMarketEvent causalEvent,
        bool dataFresh, bool reconnectReady, bool recoveryComplete, bool killSwitchEnabled,
        bool killSwitchKnown, OperationalShadowEvaluation? evaluation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(causalEvent);
        StreamId = streamId;
        CausalEvent = causalEvent;
        DataFresh = dataFresh;
        ReconnectReady = reconnectReady;
        RecoveryComplete = recoveryComplete;
        KillSwitchEnabled = killSwitchEnabled;
        KillSwitchKnown = killSwitchKnown;
        Evaluation = evaluation;
    }

    public bool ConfigurationValid => KillSwitchKnown && Evaluation?.IsConfigurationValid == true;
}

/// <summary>Source of public market observations. It has no order/execution surface.</summary>
public interface IOperationalShadowMarketDataSource
{
    IAsyncEnumerable<OperationalShadowMarketObservation> ReadAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Delegate seam used by deterministic runtime fixtures and local smoke tests.</summary>
public sealed class DelegateOperationalShadowMarketDataSource : IOperationalShadowMarketDataSource
{
    private readonly Func<CancellationToken,
        IAsyncEnumerable<OperationalShadowMarketObservation>> _reader;

    public DelegateOperationalShadowMarketDataSource(Func<CancellationToken,
        IAsyncEnumerable<OperationalShadowMarketObservation>> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public IAsyncEnumerable<OperationalShadowMarketObservation> ReadAsync(
        CancellationToken cancellationToken = default) => _reader(cancellationToken)
        ?? throw new InvalidOperationException("The shadow source returned a null stream.");
}

/// <summary>Runtime-only options; no strategy or risk defaults are supplied.</summary>
public sealed record OperationalShadowRuntimeOptions
{
    public string StreamId { get; init; } = "shadow";
    public TimeSpan CycleDelay { get; init; } = TimeSpan.Zero;
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(2);

    public OperationalShadowRuntimeOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StreamId);
        if (CycleDelay < TimeSpan.Zero || HealthCheckInterval <= TimeSpan.Zero
            || StaleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HealthCheckInterval));
        }

        return this;
    }
}

/// <summary>Passive result of one shadow cycle. It never contains an order intent.</summary>
public sealed record OperationalShadowCycleResult
{
    public OperationalShadowGateResult Gate { get; }
    public DecisionEngineResult? Decision { get; }
    public RiskEvaluationResult? Risk { get; }
    public bool HypotheticalEntryCreated { get; }
    public bool Persisted { get; }
    public bool Duplicate { get; }
    public long RealOrderCount { get; }
    public IReadOnlyList<string> Reasons { get; }

    public OperationalShadowCycleResult(OperationalShadowGateResult gate,
        DecisionEngineResult? decision, RiskEvaluationResult? risk,
        bool hypotheticalEntryCreated, bool persisted, bool duplicate,
        IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(reasons);
        Gate = gate;
        Decision = decision;
        Risk = risk;
        HypotheticalEntryCreated = hypotheticalEntryCreated;
        Persisted = persisted;
        Duplicate = duplicate;
        RealOrderCount = 0;
        Reasons = reasons.Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal).ToArray();
    }
}

/// <summary>
/// Composes public market observations, the Core Decision Engine, and the
/// side-effect-free shared Risk Evaluation Engine for continuous shadow mode.
/// </summary>
public sealed class OperationalShadowRuntime : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IOperationalShadowMarketDataSource _source;
    private readonly OperationalShadowStore _store;
    private readonly OperationalShadowRuntimeOptions _options;
    private readonly OperationalShadowMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly object _healthLock = new();
    private bool _healthEvidenceWriteHealthy = true;
    private long _nextHealthSequence;
    private OperationalShadowMarketObservation? _lastObservation;
    private OperationalShadowCycleResult? _lastCycleResult;
    private OperationalShadowHealthSnapshot _healthSnapshot;
    private bool _disposed;

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    public OperationalShadowRuntime(IOperationalShadowMarketDataSource source,
        OperationalShadowStore store, OperationalShadowRuntimeOptions? options = null,
        OperationalShadowMetrics? metrics = null, TimeProvider? timeProvider = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = (options ?? new OperationalShadowRuntimeOptions()).Validate();
        _metrics = metrics ?? new OperationalShadowMetrics();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthSnapshot = OperationalShadowHealthSnapshot.Unknown(_timeProvider.GetUtcNow());
        if (_source is IOperationalShadowHealthSource healthSource)
        {
            healthSource.HealthChanged += OnSourceHealthChanged;
        }

        RefreshHealth(causalCheckpointLink: null);
    }

    public OperationalShadowMetrics Metrics => _metrics;

    public OperationalShadowHealthSnapshot HealthSnapshot
    {
        get
        {
            lock (_healthLock)
            {
                return _healthSnapshot;
            }
        }
    }

    public event Action<OperationalShadowCycleResult>? CycleCompleted;

    public event Action<OperationalShadowHealthSnapshot>? HealthChanged;

    public OperationalShadowCycleResult Process(OperationalShadowMarketObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ThrowIfDisposed();

        PersistedKillSwitchState persistedKillSwitch = ReadPersistedKillSwitch(
            observation.StreamId);
        bool persistenceHealthy = IsPersistenceHealthy();
        bool configurationValid = observation.ConfigurationValid
            && persistedKillSwitch.Valid && persistedKillSwitch.Known;
        bool killSwitchEnabled = observation.KillSwitchEnabled || persistedKillSwitch.Enabled;
        IReadOnlyList<OperationalShadowFeedHealth> sourceFeeds =
            _source is IOperationalShadowHealthSource healthSource
                ? healthSource.GetHealth(_timeProvider.GetUtcNow(), _options.StaleAfter)
                : Array.Empty<OperationalShadowFeedHealth>();
        bool hasHealthProvider = _source is IOperationalShadowHealthSource;
        CoverageSummary coverage = GetCoverage(sourceFeeds,
            hasHealthProvider
                ? ((IOperationalShadowHealthSource)_source).RequiredFeedStreams
                : null,
            hasHealthProvider);
        bool sourceFresh = !hasHealthProvider || IsRequiredFeedState(sourceFeeds,
            coverage.RequiredStreams, static feed => feed.Fresh && !feed.GapDetected);
        bool sourceConnected = !hasHealthProvider || IsRequiredFeedState(sourceFeeds,
            coverage.RequiredStreams, static feed => feed.Connected && !feed.Reconnecting);
        bool sourceRecovered = !hasHealthProvider || IsRequiredFeedState(sourceFeeds,
            coverage.RequiredStreams, static feed => feed.RecoveryComplete);
        OperationalShadowGateInput gateInput = new(configurationValid,
            observation.DataFresh && sourceFresh,
            observation.ReconnectReady && sourceConnected,
            observation.RecoveryComplete && sourceRecovered, persistenceHealthy,
            killSwitchEnabled)
        {
            CoverageKnown = !hasHealthProvider || coverage.Known,
            CoverageComplete = !hasHealthProvider || coverage.Complete
        };
        OperationalShadowGateResult gate = OperationalShadowGate.Evaluate(gateInput);
        DecisionEngineResult? decision = null;
        RiskEvaluationResult? risk = null;
        bool hypotheticalEntry = false;
        List<string> reasons = [.. gate.BlockReasons.Select(static reason => reason.ToString())];

        if (gate.CanCreateHypotheticalEntry && observation.Evaluation is { } evaluation)
        {
            try
            {
                decision = DecisionEngine.Evaluate(evaluation.DecisionInput,
                    evaluation.DecisionConfiguration);
                risk = RiskEvaluationEngine.Evaluate(evaluation.RiskInput,
                    evaluation.RiskConfiguration, evaluation.RiskMathCalculator);
                reasons.AddRange(decision.Reasons);
                reasons.AddRange(risk.Reasons);
                if (risk.Reasons.Any(IsRiskEvaluationUnavailable))
                {
                    gate = OperationalShadowGate.Evaluate(
                        gateInput with { ConfigurationValid = false });
                    reasons.Clear();
                    reasons.AddRange(gate.BlockReasons.Select(static reason => reason.ToString()));
                    reasons.AddRange(decision.Reasons);
                    reasons.AddRange(risk.Reasons);
                    reasons.Add("shadow-evaluation-unavailable");
                }
                else
                {
                    hypotheticalEntry = decision.Decision.Outcome == DecisionOutcome.Shadow
                        && risk.IsApproved;
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                or InvalidOperationException or OverflowException)
            {
                gate = OperationalShadowGate.Evaluate(
                    gateInput with { ConfigurationValid = false });
                reasons.Clear();
                reasons.AddRange(gate.BlockReasons.Select(static reason => reason.ToString()));
                reasons.Add("shadow-evaluation-unavailable");
            }
        }

        bool effectiveKillSwitchKnown = observation.KillSwitchKnown && persistedKillSwitch.Known;
        OperationalShadowJournalEntry entry = CreateEntry(observation, gate, decision, risk,
            hypotheticalEntry, effectiveKillSwitchKnown, killSwitchEnabled);
        OperationalShadowAppendResult append;
        try
        {
            append = _store.Append(entry);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or ObjectDisposedException or SqliteException)
        {
            List<OperationalShadowBlockReason> persistenceReasons =
                [.. gate.BlockReasons];
            if (!persistenceReasons.Contains(OperationalShadowBlockReason.PersistenceUnavailable))
            {
                persistenceReasons.Add(OperationalShadowBlockReason.PersistenceUnavailable);
            }

            OperationalShadowGateResult persistenceGate = new(false, persistenceReasons);
            reasons.Add(OperationalShadowBlockReason.PersistenceUnavailable.ToString());
            OperationalShadowCycleResult failed = new(persistenceGate, decision, risk, false,
                persisted: false, duplicate: false, reasons);
            _metrics.Record(failed);
            RecordHealth(observation, failed);
            Publish(failed);
            return failed;
        }

        OperationalShadowCycleResult result = new(gate, decision, risk, hypotheticalEntry,
            persisted: true, append.Duplicate, reasons);
        _metrics.Record(result);
        RecordHealth(observation, result);
        Publish(result);
        return result;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using CancellationTokenSource monitorCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task monitor = MonitorHealthAsync(monitorCancellation.Token);
        try
        {
            await foreach (OperationalShadowMarketObservation observation in _source.ReadAsync(
                cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                _ = Process(observation);
                if (_options.CycleDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.CycleDelay, _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            monitorCancellation.Cancel();
            try
            {
                await monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
            {
                // The timer is stopped as part of the runtime's normal shutdown.
            }

        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_source is IOperationalShadowHealthSource healthSource)
        {
            healthSource.HealthChanged -= OnSourceHealthChanged;
        }

        if (_source is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_source is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _store.Dispose();
        _metrics.Dispose();
    }

    private OperationalShadowJournalEntry CreateEntry(OperationalShadowMarketObservation observation,
        OperationalShadowGateResult gate, DecisionEngineResult? decision,
        RiskEvaluationResult? risk, bool hypotheticalEntry, bool killSwitchKnown,
        bool killSwitchEnabled)
    {
        CausalMarketEvent causal = observation.CausalEvent;
        string causalJson = JsonSerializer.Serialize(new
        {
            streamId = observation.StreamId,
            eventId = causal.Payload.EventId,
            instrumentId = causal.Payload.InstrumentId.Value,
            eventTime = causal.Payload.EventTime.Value,
            exchangeTime = causal.ExchangeTime?.Value,
            receiveTime = causal.ReceiveTime.Value,
            availabilityTime = causal.AvailabilityTime.Value,
            stableSequence = causal.StableSequence,
            payload = causal.Payload
        }, JsonOptions);
        string stateJson = JsonSerializer.Serialize(new
        {
            dataFresh = observation.DataFresh,
            reconnectReady = observation.ReconnectReady,
            recoveryComplete = observation.RecoveryComplete,
            persistenceHealthy = _store.IsHealthy,
            killSwitchKnown,
            killSwitchEnabled,
            canCreateHypotheticalEntry = gate.CanCreateHypotheticalEntry
        }, JsonOptions);
        string outcomeJson = JsonSerializer.Serialize(new
        {
            decision = decision is null ? null : CoreJsonSerializer.Serialize(decision.Decision),
            risk = risk is null ? null : CoreJsonSerializer.Serialize(risk.RiskDecision),
            hypotheticalEntry,
            realOrderCount = 0L,
            blockReasons = gate.BlockReasons.Select(static reason => reason.ToString()).ToArray()
        }, JsonOptions);
        string checkpointJson = JsonSerializer.Serialize(new
        {
            streamId = observation.StreamId,
            stableSequence = causal.StableSequence,
            eventId = causal.Payload.EventId,
            outcome = hypotheticalEntry ? "hypothetical-entry" : "blocked-or-observed"
        }, JsonOptions);
        return new OperationalShadowJournalEntry(observation.StreamId, causal.Payload.EventId,
            causal.StableSequence, causal.Payload.EventTime, causal.ReceiveTime,
            causal.AvailabilityTime, causal.ExchangeTime, causalJson, stateJson, outcomeJson,
            checkpointJson);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private PersistedKillSwitchState ReadPersistedKillSwitch(string streamId)
    {
        if (!_store.IsHealthy)
        {
            return new PersistedKillSwitchState(false, false, false);
        }

        try
        {
            OperationalShadowCheckpoint? checkpoint = _store.ReadCheckpoint(streamId);
            if (checkpoint is null)
            {
                return new PersistedKillSwitchState(true, false, true);
            }

            using JsonDocument document = JsonDocument.Parse(checkpoint.DerivedStateJson);
            JsonElement root = document.RootElement;
            if (!TryReadBoolean(root, "killSwitchKnown", out bool known)
                || !TryReadBoolean(root, "killSwitchEnabled", out bool enabled))
            {
                return new PersistedKillSwitchState(false, false, false);
            }

            return new PersistedKillSwitchState(known, enabled, true);
        }
        catch (Exception exception) when (exception is JsonException or SqliteException
            or IOException or InvalidDataException or ObjectDisposedException)
        {
            return new PersistedKillSwitchState(false, false, false);
        }
    }

    private static bool TryReadBoolean(JsonElement root, string propertyName, out bool value)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element)
            || (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False))
        {
            value = false;
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool IsRiskEvaluationUnavailable(string reason) => reason is
        RiskReasonCodes.ConfigurationInvalid or
        RiskReasonCodes.RiskMathVersionMismatch or
        RiskReasonCodes.RiskMathInputMismatch or
        RiskReasonCodes.RiskMathCalculationFailed or
        RiskReasonCodes.RiskMathOverflow or
        RiskReasonCodes.RiskMathUnavailable;

    private void Publish(OperationalShadowCycleResult result)
    {
        try
        {
            CycleCompleted?.Invoke(result);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException or IOException)
        {
            // Observability subscribers cannot change shadow safety or stop the runtime.
        }
    }

    private async Task MonitorHealthAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(_options.HealthCheckInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            RefreshHealth(causalCheckpointLink: null);
        }
    }

    private void OnSourceHealthChanged() => RefreshHealth(causalCheckpointLink: null);

    private void RecordHealth(OperationalShadowMarketObservation observation,
        OperationalShadowCycleResult result)
    {
        lock (_healthLock)
        {
            _lastObservation = observation;
            _lastCycleResult = result;
        }

        RefreshHealth(CreateCheckpointLink(observation));
    }

    private void RefreshHealth(string? causalCheckpointLink)
    {
        OperationalShadowHealthSnapshot snapshot;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_healthLock)
        {
            IOperationalShadowHealthSource? provider = _source as IOperationalShadowHealthSource;
            IReadOnlyList<OperationalShadowFeedHealth> feeds =
                provider is not null
                    ? provider.GetHealth(now, _options.StaleAfter)
                    : Array.Empty<OperationalShadowFeedHealth>();
            bool hasProvider = provider is not null;
            bool healthProviderWithoutFeeds = hasProvider && feeds.Count == 0;
            CoverageSummary coverage = GetCoverage(feeds, provider?.RequiredFeedStreams,
                hasProvider);
            OperationalShadowMarketObservation? observation = _lastObservation;
            OperationalShadowCycleResult? result = _lastCycleResult;
            bool feedStale = hasProvider
                ? !IsRequiredFeedState(feeds, coverage.RequiredStreams,
                    static feed => feed.Fresh && feed.Connected)
                : false;
            bool feedGap = hasProvider
                ? feeds.Any(feed => feed.GapDetected
                    && (!coverage.Known || coverage.RequiredStreams.Contains(feed.Stream,
                        StringComparer.Ordinal)))
                : false;
            bool feedReconnecting = hasProvider
                ? !IsRequiredFeedState(feeds, coverage.RequiredStreams,
                    static feed => feed.Connected && !feed.Reconnecting)
                : false;
            bool feedRecoveryComplete = hasProvider
                && IsRequiredFeedState(feeds, coverage.RequiredStreams,
                    static feed => feed.RecoveryComplete);
            bool stale = hasProvider ? feedStale : healthProviderWithoutFeeds
                || observation is null || !observation.DataFresh;
            bool gap = feedGap;
            bool reconnecting = hasProvider ? feedReconnecting : healthProviderWithoutFeeds
                || observation is null || !observation.ReconnectReady;
            bool recoveryComplete = hasProvider ? feedRecoveryComplete
                : !healthProviderWithoutFeeds && observation is not null
                    && observation.RecoveryComplete;
            bool persistenceHealthy = _store.IsHealthy;
            bool killSwitchKnown = observation is not null && observation.KillSwitchKnown
                && (result is null || !result.Gate.BlockReasons.Contains(
                    OperationalShadowBlockReason.ConfigurationUnknown));
            bool killSwitchEnabled = observation?.KillSwitchEnabled == true
                || result?.Gate.BlockReasons.Contains(
                    OperationalShadowBlockReason.KillSwitchEnabled) == true;
            bool allowed = result?.Gate.CanCreateHypotheticalEntry == true
                && !stale && !gap && !reconnecting && recoveryComplete && persistenceHealthy
                && killSwitchKnown && !killSwitchEnabled && coverage.Known
                && coverage.Complete;
            List<string> reasons = [];
            if (observation is null) reasons.Add("health-unknown");
            if (healthProviderWithoutFeeds) reasons.Add("feed-health-unknown");
            if (stale) reasons.Add(OperationalShadowBlockReason.DataStale.ToString());
            if (gap) reasons.Add("SequenceGap");
            if (reconnecting) reasons.Add(OperationalShadowBlockReason.ReconnectPending.ToString());
            if (!recoveryComplete) reasons.Add(OperationalShadowBlockReason.RecoveryPending.ToString());
            if (!coverage.Known)
            {
                reasons.Add(OperationalShadowBlockReason.CoverageUnknown.ToString());
            }
            else if (!coverage.Complete)
            {
                reasons.Add(OperationalShadowBlockReason.CoverageIncomplete.ToString());
                reasons.AddRange(coverage.MissingStreamReasons.Select(static missing =>
                    $"missing-stream:{missing.Stream}:{missing.Reason}"));
            }
            if (!persistenceHealthy) reasons.Add(OperationalShadowBlockReason.PersistenceUnavailable.ToString());
            if (!killSwitchKnown) reasons.Add(OperationalShadowBlockReason.ConfigurationUnknown.ToString());
            if (killSwitchEnabled) reasons.Add(OperationalShadowBlockReason.KillSwitchEnabled.ToString());
            if (result is not null) reasons.AddRange(result.Gate.BlockReasons.Select(static reason =>
                reason.ToString()));
            reasons = reasons.Distinct(StringComparer.Ordinal).ToList();
            OperationalShadowHealthStatus status = observation is null
                && (!hasProvider || healthProviderWithoutFeeds)
                ? OperationalShadowHealthStatus.Unknown
                : allowed ? OperationalShadowHealthStatus.Healthy
                : OperationalShadowHealthStatus.Degraded;
            snapshot = new OperationalShadowHealthSnapshot(now, status,
                Array.AsReadOnly(feeds.ToArray()), stale, gap, reconnecting,
                recoveryComplete, persistenceHealthy, killSwitchKnown,
                killSwitchEnabled, allowed, reasons.AsReadOnly(),
                OperationalShadowReconciliationStatus.NotApplicableInShadow, 0)
            {
                ExpectedFeedCount = coverage.ExpectedFeedCount,
                CoveredFeedCount = coverage.CoveredFeedCount,
                CoverageRatio = coverage.CoverageRatio,
                CoverageKnown = coverage.Known,
                CoverageApplicable = hasProvider,
                MissingStreamReasons = coverage.MissingStreamReasons,
                RunId = RunId,
                HealthSequence = _nextHealthSequence,
                CausalCheckpointLink = causalCheckpointLink
            };

            long healthSequence = _nextHealthSequence++;
            string healthJson = JsonSerializer.Serialize(new
            {
                formatVersion = OperationalShadowHealthEvidence.CurrentEvidenceVersion,
                runId = RunId,
                healthSequence,
                checkedAt = snapshot.CheckedAt,
                causalCheckpointLink,
                snapshot
            }, JsonOptions);
            OperationalShadowHealthEvidence evidence = new(RunId, healthSequence,
                new UtcTimestamp(snapshot.CheckedAt), healthJson, causalCheckpointLink);
            try
            {
                _store.AppendHealthEvidence(evidence);
                _healthEvidenceWriteHealthy = true;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                or InvalidOperationException or ObjectDisposedException or SqliteException
                or UnauthorizedAccessException)
            {
                _healthEvidenceWriteHealthy = false;
                List<string> failedReasons = [.. snapshot.BlockReasons];
                if (!failedReasons.Contains(
                        OperationalShadowBlockReason.PersistenceUnavailable.ToString(),
                        StringComparer.Ordinal))
                {
                    failedReasons.Add(OperationalShadowBlockReason.PersistenceUnavailable.ToString());
                }

                snapshot = snapshot with
                {
                    PersistenceHealthy = false,
                    CanCreateHypotheticalEntry = false,
                    Status = OperationalShadowHealthStatus.Degraded,
                    BlockReasons = failedReasons.AsReadOnly()
                };
            }

            _healthSnapshot = snapshot;
        }

        _metrics.RecordHealth(snapshot);
        try
        {
            HealthChanged?.Invoke(snapshot);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException or IOException)
        {
            // Observability subscribers cannot change shadow safety or stop the runtime.
        }
    }

    private bool IsPersistenceHealthy()
    {
        lock (_healthLock)
        {
            return _healthEvidenceWriteHealthy && _store.IsHealthy;
        }
    }

    private static string CreateCheckpointLink(OperationalShadowMarketObservation observation) =>
        $"{observation.StreamId}/{observation.CausalEvent.StableSequence}/"
        + observation.CausalEvent.Payload.EventId;

    private static bool IsRequiredFeedState(
        IReadOnlyList<OperationalShadowFeedHealth> feeds,
        IReadOnlyList<string> requiredStreams,
        Func<OperationalShadowFeedHealth, bool> predicate)
    {
        if (requiredStreams.Count == 0)
        {
            return false;
        }

        return requiredStreams.All(stream => feeds.Any(feed =>
            StringComparer.Ordinal.Equals(feed.Stream, stream) && predicate(feed)));
    }

    private static CoverageSummary GetCoverage(
        IReadOnlyList<OperationalShadowFeedHealth> feeds,
        IReadOnlyCollection<string>? requiredFeedStreams,
        bool hasProvider)
    {
        if (!hasProvider)
        {
            return new CoverageSummary(true, 0, 0, null, [], [], false);
        }

        if (requiredFeedStreams is null)
        {
            return new CoverageSummary(false, 0, 0, null, [], [], true);
        }

        string[] required = requiredFeedStreams
            .Where(static stream => !string.IsNullOrWhiteSpace(stream))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static stream => stream, StringComparer.Ordinal)
            .ToArray();
        if (required.Length == 0)
        {
            return new CoverageSummary(true, 0, 0, null, required,
                [new OperationalShadowMissingStreamReason("<none>",
                    "required-stream-set-empty")], true);
        }

        List<OperationalShadowMissingStreamReason> missing = [];
        int covered = 0;
        foreach (string stream in required)
        {
            OperationalShadowFeedHealth[] rows = feeds.Where(feed =>
                StringComparer.Ordinal.Equals(feed.Stream, stream)).ToArray();
            bool receive = rows.Any(static feed => feed.LastReceiveTime is not null);
            bool availability = rows.Any(static feed => feed.LastAvailabilityTime is not null);
            if (receive && availability)
            {
                covered++;
                continue;
            }

            string reason = rows.Length == 0
                ? "stream-not-observed"
                : !receive && !availability
                    ? "receive-and-availability-missing"
                    : !receive ? "receive-time-missing" : "availability-time-missing";
            missing.Add(new OperationalShadowMissingStreamReason(stream, reason));
        }

        return new CoverageSummary(true, required.Length, covered,
            (double)covered / required.Length, required, missing.AsReadOnly(), true);
    }

    private sealed record PersistedKillSwitchState(bool Known, bool Enabled, bool Valid);

    private sealed record CoverageSummary(bool Known, int ExpectedFeedCount,
        int CoveredFeedCount, double? CoverageRatio, IReadOnlyList<string> RequiredStreams,
        IReadOnlyList<OperationalShadowMissingStreamReason> MissingStreamReasons,
        bool Applicable)
    {
        public bool Complete => Known && (!Applicable || (ExpectedFeedCount > 0
            && CoveredFeedCount == ExpectedFeedCount));
    }
}

