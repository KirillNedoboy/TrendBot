using System.Security.Cryptography;
using System.Text;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>One serial public-input consumer for one instrument.</summary>
public sealed class SignalOnlyInstrumentConsumer : IDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string _streamId;
    private readonly NativePublicKlineFeed _feed;
    private readonly SqliteNativeBarInputLedger _inputLedger;
    private readonly SqliteSignalJournalStore _signalJournal;
    private readonly PublicSignalProfileConfiguration _profile;
    private readonly string _profileHash;
    private readonly ISignalMultiLaneSource? _multiLane;
    private readonly SignalScenarioProjectionEngine _scenarioEngine;
    private readonly Dictionary<string, string> _revisionHashes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _committedInputEventIds = new(StringComparer.Ordinal);
    private SignalJournalCurrentState _currentState;
    private SignalScenarioProjectionSnapshot _scenarioSnapshot;
    private DateTimeOffset _nextRecoveryAt = DateTimeOffset.MinValue;
    private bool _receivedInput;
    private bool _historyBackfillPending = true;

    public string Symbol { get; }

    public SignalOnlyInstrumentConsumer(string symbol,
        PublicSignalProfileConfiguration profile, SignalScenarioPolicySet scenarioPolicies,
        NativePublicKlineFeed feed, SqliteNativeBarInputLedger inputLedger,
        SqliteSignalJournalStore signalJournal)
        : this(symbol, profile, scenarioPolicies, feed, inputLedger, signalJournal, null)
    {
    }

    internal SignalOnlyInstrumentConsumer(string symbol,
        PublicSignalProfileConfiguration profile, SignalScenarioPolicySet scenarioPolicies,
        NativePublicKlineFeed feed, SqliteNativeBarInputLedger inputLedger,
        SqliteSignalJournalStore signalJournal, ISignalMultiLaneSource? multiLane)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(scenarioPolicies);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(inputLedger);
        ArgumentNullException.ThrowIfNull(signalJournal);
        if (!profile.IsReady || !scenarioPolicies.IsReady)
        {
            throw new ArgumentException(
                "The signal-only consumer requires ready public and scenario configuration.");
        }

        Symbol = symbol.ToUpperInvariant();
        if (!StringComparer.Ordinal.Equals(Symbol, feed.Symbol))
        {
            throw new ArgumentException(
                "The native feed symbol does not match this serial consumer.", nameof(feed));
        }

        _streamId = feed.StreamId;
        _feed = feed;
        _inputLedger = inputLedger;
        _signalJournal = signalJournal;
        _profile = profile;
        _multiLane = multiLane;
        _profileHash = ComputeProfileHash(profile, multiLane?.Policy);
        _scenarioEngine = new SignalScenarioProjectionEngine(scenarioPolicies);
        SignalJournalSnapshot journalSnapshot = signalJournal.Read(_streamId);
        _currentState = new SignalJournalCurrentState(journalSnapshot.Checkpoint,
            journalSnapshot.ScenarioSnapshot);
        if ((_currentState.Checkpoint is null) != (_currentState.ScenarioSnapshot is null))
        {
            throw new InvalidDataException(
                "The signal journal cursor and scenario snapshot are inconsistent.");
        }

        _scenarioSnapshot = _currentState.ScenarioSnapshot
            ?? _scenarioEngine.CreateInitialSnapshot();
        if (!StringComparer.Ordinal.Equals(
                CoreJsonSerializer.Serialize(_scenarioSnapshot.Policies),
                CoreJsonSerializer.Serialize(scenarioPolicies)))
        {
            throw new InvalidOperationException(
                "The configured scenario policies differ from the durable checkpoint.");
        }

        foreach (SignalJournalDiagnostic diagnostic in journalSnapshot.Diagnostics)
        {
            _committedInputEventIds.Add(diagnostic.InputEventId);
        }

        foreach (SignalScenarioProcessedInput processed in _scenarioSnapshot.ProcessedInputs)
        {
            _committedInputEventIds.Add(processed.EventId);
        }

        if (_currentState.Checkpoint is { } checkpoint)
        {
            _committedInputEventIds.Add(checkpoint.InputEventId);
        }

        RecoverPendingInputs(inputLedger.Read(_streamId));
    }

    /// <summary>Consumes one public envelope without concurrent state mutation.</summary>
    public async Task<NativePublicKlineFeedUpdate> ProcessAsync(
        BinanceStreamEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NativePublicKlineFeedInput input = _feed.AcceptWithReceipt(envelope);
            if (envelope.Payload is BinanceKlineEvent klineEvent)
            {
                ProcessDurableInput(input.Receipt, input.Update, klineEvent.Symbol,
                    klineEvent.Kline, BinanceKlineSourceKind.WebSocket,
                    envelope.AvailabilityTime);
            }

            return input.Update;
        }
        finally
        {
            _serial.Release();
        }
    }

    internal async Task BackfillAsync(CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _feed.BackfillAsync(cancellationToken).ConfigureAwait(false);
            foreach (NativeBarInputLedgerReceipt receipt in _inputLedger.Read(_streamId).Receipts)
            {
                RememberRevision(receipt);
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Reads both public timeframes and awaits each write in instrument order.</summary>
    public Task RunAsync(BinanceMarketWebSocketClient marketClient,
        CancellationToken cancellationToken = default) =>
        RunAsync(marketClient, null, null, cancellationToken);

    internal async Task RunAsync(BinanceMarketWebSocketClient marketClient,
        Func<NativePublicKlineFeedUpdate, Task>? onClose, Func<Task>? onFirstInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marketClient);
        await foreach (BinanceStreamEnvelope envelope in _feed
            .ReadEnvelopesAsync(marketClient, cancellationToken).ConfigureAwait(false))
        {
            await ProcessStreamEnvelopeAsync(envelope, onClose, onFirstInput,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task ProcessStreamEnvelopeAsync(BinanceStreamEnvelope envelope,
        Func<NativePublicKlineFeedUpdate, Task>? onClose, Func<Task>? onFirstInput,
        CancellationToken cancellationToken)
    {
        if (envelope.Payload is BinanceKlineEvent item
            && StringComparer.Ordinal.Equals(item.Symbol, Symbol)
            && (item.Kline.Timeframe == _feed.SetupTimeframe
                || item.Kline.Timeframe == _feed.ContextTimeframe))
        {
            if (!_receivedInput)
            {
                _receivedInput = true;
                if (onFirstInput is not null)
                {
                    await onFirstInput().ConfigureAwait(false);
                }
            }
            if (!item.Kline.IsClosed)
            {
                if ((_historyBackfillPending || _feed.RequiresStreamRecovery)
                    && DateTimeOffset.UtcNow >= _nextRecoveryAt)
                {
                    await BackfillAsync(cancellationToken).ConfigureAwait(false);
                    _historyBackfillPending = !_feed.HasCompleteHistoryNow();
                    _nextRecoveryAt = _historyBackfillPending || _feed.RequiresStreamRecovery
                        ? DateTimeOffset.UtcNow.AddSeconds(30) : DateTimeOffset.MinValue;
                }
                return;
            }

            DateTimeOffset close = DateTimeOffset.FromUnixTimeMilliseconds(
                checked(item.Kline.CloseTimeMilliseconds + 1));
            TimeSpan delay = close - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.FromSeconds(30))
            {
                throw new InvalidDataException("A public kline closed too far in the future.");
            }
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        NativePublicKlineFeedUpdate update = await ProcessAsync(envelope,
            cancellationToken).ConfigureAwait(false);
        if (update.Evaluation?.ReadinessReasons.Any(static reason =>
                reason is "history-gap:setup" or "history-gap:context") == true)
        {
            _historyBackfillPending = true;
        }
        if (onClose is not null)
        {
            await onClose(update).ConfigureAwait(false);
        }
    }

    internal SignalMarketContext? CaptureLiveMarketContext(NativePublicKlineFeedUpdate update)
    {
        if (update.Evaluation is not { } evaluation
            || _multiLane is not ISignalMarketContextSource source
            || _profile.Indicators?.BoundaryMinimumChannelWidth is not { } binSize)
        {
            return null;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset earliest = evaluation.LaneA.SetupObservations.Cursor.AsOf.Value;
        UtcTimestamp asOf = new(now > earliest ? now : earliest);
        return SignalMarketContextCapture.FromLive(evaluation, source, binSize, asOf);
    }

    public void Dispose() => _serial.Dispose();

    private void RecoverPendingInputs(NativeBarInputLedgerSnapshot inputSnapshot)
    {
        foreach (NativeBarInputLedgerReceipt receipt in inputSnapshot.Receipts)
        {
            if (receipt.Status is NativeBarInputReceiptStatus.Accepted
                or NativeBarInputReceiptStatus.Duplicate)
            {
                RememberRevision(receipt);
            }
        }

        long firstSequence = FindRecoverySequence(inputSnapshot.Receipts,
            _currentState.Checkpoint);
        foreach (NativeBarInputLedgerReceipt receipt in inputSnapshot.Receipts
            .Where(receipt => receipt.IntakeSequence >= firstSequence))
        {
            NativePublicKlineFeedReplay replay = _feed.ReplayDurableReceipt(receipt);
            if (replay.SourceKind != BinanceKlineSourceKind.WebSocket
                || !StringComparer.Ordinal.Equals(replay.Symbol, Symbol)
                || replay.Kline is not { IsClosed: true }
                || replay.Kline.Timeframe != _feed.SetupTimeframe
                || receipt.Status is not (NativeBarInputReceiptStatus.Accepted
                    or NativeBarInputReceiptStatus.Invalid))
            {
                continue;
            }

            UtcTimestamp availabilityTime = replay.AvailabilityTime
                ?? throw new InvalidDataException(
                    "A durable public input is missing availability time.");
            ProcessDurableInput(receipt, replay.Update, replay.Symbol,
                replay.Kline, replay.SourceKind, availabilityTime, isRecovery: true);
        }
    }

    private static long FindRecoverySequence(
        IReadOnlyList<NativeBarInputLedgerReceipt> receipts,
        SignalJournalCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return receipts.Count == 0 ? 1 : receipts[0].IntakeSequence;
        }

        string sourceEventId = SourceEventId(checkpoint.InputEventId);
        NativeBarInputLedgerReceipt? committedReceipt = receipts.FirstOrDefault(receipt =>
            StringComparer.Ordinal.Equals(receipt.EventId, sourceEventId)
            && StringComparer.Ordinal.Equals(ReceiptHash(receipt), checkpoint.InputHash));
        return committedReceipt?.IntakeSequence ?? throw new InvalidDataException(
            "The signal journal checkpoint does not match a durable public input.");
    }

    private void ProcessDurableInput(NativeBarInputLedgerReceipt receipt,
        NativePublicKlineFeedUpdate update, string? symbol, BinanceKline kline,
        BinanceKlineSourceKind? sourceKind, UtcTimestamp availabilityTime,
        bool isRecovery = false)
    {
        if (sourceKind != BinanceKlineSourceKind.WebSocket
            || !StringComparer.Ordinal.Equals(symbol, Symbol))
        {
            return;
        }

        RememberRevision(receipt);
        if (!StringComparer.Ordinal.Equals(kline.Timeframe, _feed.SetupTimeframe)
            || !kline.IsClosed)
        {
            return;
        }
        if (update.Status == BinanceKlineIngestionStatus.Duplicate)
        {
            return;
        }

        if (update.Status == BinanceKlineIngestionStatus.RevisionConflict)
        {
            throw new InvalidOperationException(
                "A public kline identity arrived with conflicting content.");
        }

        string inputEventId = receipt.EventId ?? update.Identity?.EventId
            ?? BinanceKlineIdentity.Create(Symbol, _feed.SetupTimeframe,
                kline.OpenTimeMilliseconds).EventId;
        if (_committedInputEventIds.Contains(inputEventId))
        {
            return;
        }

        string inputHash = ReceiptHash(receipt);
        UtcTimestamp eventTime = update.Evaluation?.TriggerRevision.CoreBar.EventTime
            ?? new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(
                kline.CloseTimeMilliseconds));
        if (update.Evaluation is not { } evaluation)
        {
            CommitDiagnostic(inputEventId, inputHash, eventTime, availabilityTime,
                receipt.IntakeSequence, update.DiagnosticCode
                    ?? "closed-input-not-evaluated",
                update.DiagnosticCode is null ? [] : [update.DiagnosticCode]);
            return;
        }

        if (!evaluation.IsReady)
        {
            CommitDiagnostic(inputEventId, inputHash, eventTime, availabilityTime,
                receipt.IntakeSequence, "signal-evaluation-not-ready",
                EvaluationReasons(evaluation));
            return;
        }

        SetupCandidate[] candidates = ConfirmedCandidates(evaluation);
        if (candidates.Length == 0)
        {
            string code = evaluation.LaneA.SetupObservations.IsComplete
                ? "no-confirmed-setup" : "signal-evaluation-unknown";
            CommitDiagnostic(inputEventId, inputHash, eventTime, availabilityTime,
                receipt.IntakeSequence, code, EvaluationReasons(evaluation).Append(code));
            return;
        }

        foreach (SetupCandidate candidate in candidates)
        {
            CommitCandidate(evaluation, candidate, inputEventId, inputHash,
                availabilityTime, receipt.IntakeSequence, isRecovery);
        }
    }

    private void CommitCandidate(NativePublicKlineEvaluation evaluation,
        SetupCandidate candidate, string sourceEventId, string inputHash,
        UtcTimestamp availabilityTime, long intakeSequence, bool isRecovery)
    {
        string scenarioEventId = ScenarioInputEventId(sourceEventId, candidate);
        if (_committedInputEventIds.Contains(scenarioEventId))
        {
            return;
        }

        PriceContextPivotKind anchorKind = candidate.Direction == Direction.Long
            ? PriceContextPivotKind.Low : PriceContextPivotKind.High;
        PriceContextPivotEvidence? pivot = evaluation.LaneA.PriceContext.ConfirmedPivots.Pivots
            .Where(item => item.Kind == anchorKind
                && item.PivotBar.EventTime.Value < candidate.SignalBar.EventTime.Value
                && item.ConfirmationSequence <= evaluation.IntakeSequence)
            .OrderBy(static item => item.ConfirmationTime.Value)
            .ThenBy(static item => item.ConfirmationSequence)
            .LastOrDefault();
        if (pivot is null)
        {
            CommitDiagnostic(scenarioEventId, inputHash, candidate.SignalBar.EventTime,
                availabilityTime, intakeSequence,
                "confirmed-setup-anchor-unavailable",
                ["confirmed-setup-anchor-unavailable"]);
            return;
        }

        SignalMultiLaneInput? multiLaneInput = null;
        if (_multiLane is not null)
        {
            if (isRecovery)
            {
                CommitDiagnostic(scenarioEventId, inputHash, candidate.SignalBar.EventTime,
                    availabilityTime, intakeSequence, "multi-lane-recovery-unavailable",
                    ["multi-lane-recovery-evidence-unavailable"]);
                return;
            }
            multiLaneInput = _multiLane.Capture(candidate.Direction,
                evaluation.LaneA.SetupObservations.Cursor.AsOf);
            if (multiLaneInput.InstrumentId.Value != Symbol
                || multiLaneInput.Direction != candidate.Direction
                || multiLaneInput.Policy != _multiLane.Policy
                || multiLaneInput.DecisionAt.Value < availabilityTime.Value)
            {
                throw new InvalidDataException("The multi-lane input does not match its signal.");
            }
            SignalMultiLaneAssessment assessment =
                SignalMultiLaneResearchEvaluator.Evaluate(multiLaneInput);
            availabilityTime = multiLaneInput.DecisionAt;
            if (!assessment.IsAllowed)
            {
                CommitDiagnostic(scenarioEventId, inputHash, candidate.SignalBar.EventTime,
                    availabilityTime, intakeSequence, "multi-lane-blocked",
                    assessment.Reasons.Append($"lane-b-{assessment.LaneB.ToString().ToLowerInvariant()}")
                        .Append($"lane-c-{assessment.LaneC.ToString().ToLowerInvariant()}"));
                return;
            }
        }

        SignalScenarioAnchor anchor = SignalScenarioAnchor.Create(
            new InstrumentId(Symbol), BinanceKlineIdentity.VenueName, _feed.SetupTimeframe,
            candidate.Direction, candidate.Family, _profile.ProfileVersion!,
            candidate.AlgorithmVersion, pivot.PivotBar.Bar.EventId,
            pivot.PivotBar.EventTime, pivot.PivotBar.StableSequence);
        SignalScenarioProcessedInput? prior = _scenarioSnapshot.ProcessedInputs
            .FirstOrDefault(item => StringComparer.Ordinal.Equals(item.StreamId, _streamId)
                && StringComparer.Ordinal.Equals(item.EventId, scenarioEventId));
        long stableSequence = prior?.StableSequence ?? NextStableSequence(intakeSequence);
        SignalScenarioInput scenarioInput = SignalScenarioInput.Confirm(_streamId,
            scenarioEventId, anchor, candidate.SignalBar.EventTime,
            availabilityTime, stableSequence);
        if (prior is not null)
        {
            if (!StringComparer.Ordinal.Equals(prior.InputHash, scenarioInput.InputHash))
            {
                throw new InvalidOperationException(
                    "A persisted scenario input identity has conflicting content.");
            }

            return;
        }

        (SignalRecord Signal, SignalEvidenceRecord Evidence) signal =
            CreateSignal(evaluation, candidate, anchor.Identity, pivot.PivotBar, multiLaneInput);
        SignalScenarioProjectionTransition transition = _scenarioEngine.Apply(
            _scenarioSnapshot, scenarioInput);
        if (transition.Status == SignalScenarioTransitionStatus.IdentityConflict)
        {
            throw new InvalidOperationException(
                "The scenario projector detected an input identity conflict.");
        }

        if (transition.Status is SignalScenarioTransitionStatus.Late
            or SignalScenarioTransitionStatus.UnknownPolicy)
        {
            CommitDiagnostic(scenarioEventId, inputHash,
                candidate.SignalBar.EventTime, availabilityTime, intakeSequence,
                "scenario-input-not-applied", [transition.ReasonCode]);
            return;
        }

        SignalScenarioEvent? alert = transition.EmittedEvents.FirstOrDefault(item =>
            item.Kind == SignalScenarioEventKind.AlertCreated);
        SignalJournalDiagnostic? diagnostic = transition.Status
            == SignalScenarioTransitionStatus.Rejected
            ? SignalJournalDiagnostic.Create(_streamId, scenarioEventId, inputHash,
                "scenario-transition-rejected", [transition.ReasonCode])
            : null;
        bool processed = transition.NextSnapshot.ProcessedInputs.Any(item =>
            StringComparer.Ordinal.Equals(item.StreamId, _streamId)
            && StringComparer.Ordinal.Equals(item.EventId, scenarioEventId)
            && StringComparer.Ordinal.Equals(item.InputHash, scenarioInput.InputHash));
        Commit(new SignalJournalCommit(_streamId, CurrentInputSequence,
            checked(CurrentInputSequence + 1), scenarioEventId, inputHash,
            scenarioInput.EventTime, scenarioInput.AvailabilityTime, stableSequence,
            processed ? scenarioInput.InputHash : null, transition.NextSnapshot,
            transition.EmittedEvents, alert is null ? null : signal.Signal,
            alert is null ? null : signal.Evidence, diagnostic));
    }

    private (SignalRecord Signal, SignalEvidenceRecord Evidence) CreateSignal(
        NativePublicKlineEvaluation evaluation, SetupCandidate candidate,
        SignalIdentity identity, ScreenerBar anchorBar, SignalMultiLaneInput? multiLaneInput)
    {
        ScreenerBar signalBar = candidate.SignalBar;
        string setupVersion = _profile.Setup!.SourceVersion!;
        string contextVersion = _profile.Context!.SourceVersion!;
        List<SignalInputReference> references = [];
        HashSet<string> setupEventIds = new(StringComparer.Ordinal);
        foreach (ScreenerBar bar in evaluation.Input.SetupBars.Append(anchorBar))
        {
            if (setupEventIds.Add(bar.Bar.EventId))
            {
                references.Add(CreateInputReference(bar, SignalInputRole.Setup,
                    _feed.SetupTimeframe, setupVersion));
            }
        }

        foreach (ScreenerBar bar in evaluation.Input.HtfBars)
        {
            references.Add(CreateInputReference(bar, SignalInputRole.HigherTimeframe,
                _feed.ContextTimeframe, contextVersion));
        }

        if (evaluation.HtfCutEventId is null || evaluation.HtfCutTime is null
            || !references.Any(reference => reference.Role == SignalInputRole.HigherTimeframe
                && StringComparer.Ordinal.Equals(reference.EventId,
                    evaluation.HtfCutEventId)))
        {
            throw new InvalidDataException(
                "A confirmed signal is missing its causal higher-timeframe cut.");
        }

        CausalEventCursor decisionCut = evaluation.LaneA.SetupObservations.Cursor;
        SignalProvenance provenance = SignalProvenance.Create(_profile.ProfileVersion!,
            _profileHash, decisionCut, decisionCut, references);
        string[] qualityReasons = evaluation.LaneA.Quality.Features
            .SelectMany(static feature => feature.Reasons)
            .Concat(candidate.Reasons)
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal)
            .ToArray();
        SignalMarketContext? marketContext = _multiLane is ISignalMarketContextSource contextSource
            && multiLaneInput is not null && _profile.Indicators?.BoundaryMinimumChannelWidth is { } binSize
                ? SignalMarketContextCapture.FromDecision(evaluation, multiLaneInput, contextSource, binSize)
                : null;
        SignalEvidenceRecord evidence = SignalEvidenceRecord.Create(identity,
            provenance, SignalEvidenceState.Complete, qualityReasons, multiLaneInput, marketContext);
        SignalRecord signal = SignalRecord.Create(identity, evidence,
            SignalAssessmentState.ConfirmedOnClosedBar,
            new SignalBarInterval(signalBar.Bar.OpenTime, signalBar.Bar.CloseTime),
            multiLaneInput?.DecisionAt ?? decisionCut.AsOf,
            new SignalReferencePrice(signalBar.SourceId, setupVersion,
                signalBar.Bar.EventId, signalBar.EventTime, signalBar.Bar.Close));
        return (signal, evidence);
    }

    private SignalInputReference CreateInputReference(ScreenerBar bar,
        SignalInputRole role, string timeframe, string sourceVersion)
    {
        if (!_revisionHashes.TryGetValue(bar.Bar.EventId, out string? revisionHash))
        {
            throw new InvalidDataException(
                "A signal provenance bar is missing its durable public input hash.");
        }

        return new SignalInputReference(bar.SourceId, sourceVersion, bar.Bar.EventId,
            revisionHash, role, timeframe, bar.EventTime, bar.AvailabilityTime,
            bar.StableSequence);
    }

    private void CommitDiagnostic(string eventId, string inputHash,
        UtcTimestamp eventTime, UtcTimestamp availabilityTime, long intakeSequence,
        string code, IEnumerable<string> reasons)
    {
        SignalJournalDiagnostic diagnostic = SignalJournalDiagnostic.Create(
            _streamId, eventId, inputHash, code, reasons);
        long stableSequence = NextStableSequence(intakeSequence);
        Commit(new SignalJournalCommit(_streamId, CurrentInputSequence,
            checked(CurrentInputSequence + 1), eventId, inputHash, eventTime,
            availabilityTime, stableSequence, null, _scenarioSnapshot, [],
            diagnostic: diagnostic));
    }

    private void Commit(SignalJournalCommit commit)
    {
        _signalJournal.Append(commit);
        _currentState = _signalJournal.ReadCurrentState(_streamId);
        _scenarioSnapshot = _currentState.ScenarioSnapshot
            ?? throw new InvalidDataException(
                "The committed signal journal is missing its scenario checkpoint.");
        _committedInputEventIds.Add(commit.InputEventId);
    }

    private void RememberRevision(NativeBarInputLedgerReceipt receipt)
    {
        if (receipt.EventId is null || receipt.RevisionHash is null
            || receipt.Status is not (NativeBarInputReceiptStatus.Accepted
                or NativeBarInputReceiptStatus.Duplicate))
        {
            return;
        }

        if (_revisionHashes.TryGetValue(receipt.EventId, out string? priorHash)
            && !StringComparer.Ordinal.Equals(priorHash, receipt.RevisionHash))
        {
            throw new InvalidOperationException(
                "A public input identity has conflicting durable content hashes.");
        }

        _revisionHashes[receipt.EventId] = receipt.RevisionHash;
    }

    private long CurrentInputSequence => _currentState.Checkpoint?.InputSequence ?? 0;

    private long NextStableSequence(long intakeSequence) => checked(Math.Max(
        _currentState.Checkpoint?.StableSequence ?? 0, intakeSequence) + 1);

    private static string ComputeProfileHash(PublicSignalProfileConfiguration profile,
        SignalMultiLanePolicy? policy)
    {
        string json = policy is null ? CoreJsonSerializer.Serialize(profile)
            : CoreJsonSerializer.Serialize(new { Profile = profile, MultiLanePolicy = policy });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static string ReceiptHash(NativeBarInputLedgerReceipt receipt) =>
        receipt.RevisionHash ?? Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(receipt.PayloadJson))).ToLowerInvariant();

    internal static string SourceEventId(string inputEventId)
    {
        const string prefix = "scenario-confirm:";
        if (!inputEventId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return inputEventId;
        }

        int directionSeparator = inputEventId.LastIndexOf(':');
        int familySeparator = directionSeparator > prefix.Length
            ? inputEventId.LastIndexOf(':', directionSeparator - 1) : -1;
        if (familySeparator <= prefix.Length
            || directionSeparator <= familySeparator + 1)
        {
            return inputEventId;
        }

        string family = inputEventId[(familySeparator + 1)..directionSeparator];
        string direction = inputEventId[(directionSeparator + 1)..];
        if (family is not (nameof(SetupType.BoundaryRejection)
                or nameof(SetupType.CenterRetest))
            || direction is not (nameof(Direction.Long) or nameof(Direction.Short)))
        {
            return inputEventId;
        }

        return inputEventId[prefix.Length..familySeparator];
    }

    private static SetupCandidate[] ConfirmedCandidates(
        NativePublicKlineEvaluation evaluation)
    {
        LaneASetupObservationProjection projection = evaluation.LaneA.SetupObservations;
        List<SetupCandidate> candidates = [];
        AddBoundary(Direction.Long, projection.BoundaryLongStatus,
            evaluation.LaneA.BoundaryRejection.Long);
        AddBoundary(Direction.Short, projection.BoundaryShortStatus,
            evaluation.LaneA.BoundaryRejection.Short);
        AddCenter(Direction.Long, projection.CenterRetestLongStatus,
            evaluation.LaneA.CenterRetest.Long);
        AddCenter(Direction.Short, projection.CenterRetestShortStatus,
            evaluation.LaneA.CenterRetest.Short);
        return [.. candidates];

        void AddBoundary(Direction direction, LaneASetupObservationStatus status,
            BoundaryRejectionSetupObservation observation)
        {
            if (status != LaneASetupObservationStatus.Confirmed)
            {
                return;
            }

            if (!observation.IsReady || observation.SignalBar is null)
            {
                throw new InvalidDataException(
                    "The passive SETUP-01 projection disagrees with evaluator evidence.");
            }

            candidates.Add(new SetupCandidate(SetupType.BoundaryRejection, direction,
                observation.SignalBar, observation.Reasons,
                BoundaryRejectionSetupResult.CurrentAlgorithmVersion));
        }

        void AddCenter(Direction direction, LaneASetupObservationStatus status,
            CenterRetestSetupObservation observation)
        {
            if (status != LaneASetupObservationStatus.Confirmed)
            {
                return;
            }

            if (!observation.IsReady || observation.SignalBar is null)
            {
                throw new InvalidDataException(
                    "The passive SETUP-02 projection disagrees with evaluator evidence.");
            }

            candidates.Add(new SetupCandidate(SetupType.CenterRetest, direction,
                observation.SignalBar, observation.Reasons,
                CenterRetestSetupResult.CurrentAlgorithmVersion));
        }
    }

    private static string[] EvaluationReasons(
        NativePublicKlineEvaluation evaluation)
    {
        IEnumerable<string> reasons = evaluation.ReadinessReasons
            .Concat(evaluation.LaneA.Quality.Features.SelectMany(
                static feature => feature.Reasons))
            .Concat(evaluation.LaneA.BoundaryRejection.Long.Reasons)
            .Concat(evaluation.LaneA.BoundaryRejection.Short.Reasons)
            .Concat(evaluation.LaneA.CenterRetest.Long.Reasons)
            .Concat(evaluation.LaneA.CenterRetest.Short.Reasons);
        string[] stable = reasons.Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal)
            .ToArray();
        return stable.Length == 0 ? ["signal-evidence-unknown"] : stable;
    }

    private static string ScenarioInputEventId(string sourceEventId,
        SetupCandidate candidate) =>
        $"scenario-confirm:{sourceEventId}:{candidate.Family}:{candidate.Direction}";

    private sealed record SetupCandidate(SetupType Family, Direction Direction,
        ScreenerBar SignalBar, ImmutableValueList<string> Reasons,
        string AlgorithmVersion);
}
