using System.Collections.Concurrent;

namespace TradingBot.Core;

/// <summary>
/// Pure deterministic state machine for setup lifecycle facts. The engine does
/// not calculate strategy evidence, risk, orders, positions, or persistence.
/// </summary>
public sealed class SetupLifecycleEngine
{
    private readonly SetupLifecycleConfiguration _configuration;

    public SetupLifecycleEngine(SetupLifecycleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    public static SetupLifecycleSnapshot CreateInitial(
        SetupLifecycleIdentity identity, SetupLifecycleConfiguration configuration) =>
        SetupLifecycleSnapshot.CreateInitial(identity, configuration);

    /// <summary>Convenience entry point for one pure transition.</summary>
    public static SetupLifecycleTransition Evaluate(SetupLifecycleSnapshot current,
        SetupLifecycleInput input, SetupLifecycleConfiguration configuration) =>
        new SetupLifecycleEngine(configuration).Apply(current, input);

    public SetupLifecycleTransition Apply(SetupLifecycleSnapshot current,
        SetupLifecycleInput input)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(input);

        if (!StringComparer.Ordinal.Equals(input.RulesVersion, _configuration.RulesVersion)
            || !StringComparer.Ordinal.Equals(input.ConfigurationVersion,
                _configuration.ConfigurationVersion))
        {
            return Reject(current, input, "configuration-version-mismatch");
        }

        if (input.Identity.InstrumentId != current.Identity.InstrumentId)
        {
            return Reject(current, input, "instrument-mismatch");
        }

        SetupLifecycleEventReceipt? prior = current.ProcessedEvents.FirstOrDefault(
            item => StringComparer.Ordinal.Equals(item.StreamId, input.StreamId)
                && StringComparer.Ordinal.Equals(item.EventId, input.EventId));
        if (prior is not null)
        {
            if (StringComparer.Ordinal.Equals(prior.PayloadFingerprint,
                input.PayloadFingerprint))
            {
                return Transition(SetupLifecycleTransitionStatus.Duplicate, current,
                    current, input, "duplicate-event", current.Observation);
            }

            SetupLifecycleSnapshot conflict = current with
            {
                State = SetupState.Suspended,
                RecoveryRequired = true,
                Observation = MergeObservation(current.Observation,
                    "identity-conflict")
            };
            return Transition(SetupLifecycleTransitionStatus.IdentityConflict, current,
                conflict, input, "event-identity-conflict", conflict.Observation);
        }

        if (current.RecoveryRequired && input.FactKind != SetupLifecycleFactKind.Recovery)
        {
            return Reject(current, input, "recovery-required");
        }

        if (input.FactKind == SetupLifecycleFactKind.Recovery)
        {
            if (input.RecoverySnapshot is null)
            {
                return Reject(current, input, "recovery-snapshot-missing");
            }

            if (input.RecoverySnapshot.Identity != current.Identity)
            {
                return Reject(current, input, "recovery-identity-mismatch");
            }

            SetupLifecycleSnapshot recovered = input.RecoverySnapshot with
            {
                RecoveryRequired = false,
                RulesVersion = _configuration.RulesVersion,
                ConfigurationVersion = _configuration.ConfigurationVersion
            };
            return Transition(SetupLifecycleTransitionStatus.RecoveryApplied, current,
                recovered, input, "recovery-applied", recovered.Observation);
        }

        if (current.ProcessedEvents.Count > 0 && IsLate(current, input))
        {
            return Transition(SetupLifecycleTransitionStatus.Late, current, current,
                input, "late-event", current.Observation);
        }

        if (input.FactKind == SetupLifecycleFactKind.StructureReset)
        {
            if (current.State is not (SetupState.Terminal or SetupState.Invalidated))
            {
                return Reject(current, input, "structure-reset-requires-terminal-state");
            }

            if (input.Identity == current.Identity)
            {
                return Reject(current, input, "structure-reset-requires-new-identity");
            }

            SetupLifecycleSnapshot reset = CreateResetSnapshot(current, input);
            return Transition(SetupLifecycleTransitionStatus.Applied, current, reset,
                input, "structure-reset", reset.Observation);
        }

        if (input.Identity != current.Identity)
        {
            return Reject(current, input, "identity-change-requires-structure-reset");
        }

        (SetupState nextState, string reason, bool accepted) = EvaluateState(current, input);
        if (!accepted)
        {
            return Reject(current, input, reason);
        }

        SetupEvidenceSnapshot? latestEvidence = input.Evidence ?? current.LatestEvidence;
        SetupLifecycleObservation observation = MergeObservation(current.Observation,
            input.ShadowOutcome, input.Outcome, reason);
        ImmutableValueList<SetupLifecycleEventReceipt> receipts = AppendReceipt(
            current.ProcessedEvents, SetupLifecycleEventReceipt.From(input));
        SetupLifecycleSnapshot next = current with
        {
            State = nextState,
            ProcessedCursor = new CausalEventCursor(input.EventTime,
                input.StableSequence),
            ProcessedAvailabilityTime = input.AvailabilityTime,
            ProcessedEvents = receipts,
            LatestEvidence = latestEvidence,
            Observation = observation
        };
        return Transition(SetupLifecycleTransitionStatus.Applied, current, next, input,
            reason, observation);
    }

    /// <summary>Replays a deterministic ordered input sequence from one snapshot.</summary>
    public ImmutableValueList<SetupLifecycleTransition> Replay(
        SetupLifecycleSnapshot initial, IEnumerable<SetupLifecycleInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(inputs);
        List<SetupLifecycleTransition> transitions = [];
        SetupLifecycleSnapshot current = initial;
        foreach (SetupLifecycleInput input in inputs)
        {
            SetupLifecycleTransition transition = Apply(current, input);
            transitions.Add(transition);
            current = transition.NextSnapshot;
        }

        return new ImmutableValueList<SetupLifecycleTransition>(transitions);
    }

    private (SetupState State, string Reason, bool Accepted) EvaluateState(
        SetupLifecycleSnapshot current, SetupLifecycleInput input)
    {
        switch (input.FactKind)
        {
            case SetupLifecycleFactKind.ContextReady:
                return current.State is SetupState.Observing or SetupState.ContextReady
                    ? (SetupState.ContextReady, "context-ready", true)
                    : (current.State, "context-ready-state-not-allowed", false);

            case SetupLifecycleFactKind.StructureFormed:
                return current.State is SetupState.ContextReady or SetupState.SetupForming
                    ? (SetupState.SetupForming, "structure-formed", true)
                    : (current.State, "structure-forming-state-not-allowed", false);

            case SetupLifecycleFactKind.SetupEvidence:
                return EvaluateEvidence(current, input);

            case SetupLifecycleFactKind.Confirmation:
                return EvaluateConfirmation(current, input);

            case SetupLifecycleFactKind.ExternalPlanReady:
                return current.State == SetupState.ConfirmationWindow
                    && _configuration.RequireExternalPlan
                    ? (SetupState.EntryArmed, "external-plan-ready", true)
                    : current.State == SetupState.EntryArmed
                        ? (SetupState.EntryArmed, "external-plan-already-ready", true)
                        : (current.State, "external-plan-state-not-allowed", false);

            case SetupLifecycleFactKind.OrderObserved:
                return current.State == SetupState.EntryArmed
                    ? (SetupState.OrderPending, "order-observed", true)
                    : (current.State, "order-state-not-allowed", false);

            case SetupLifecycleFactKind.PositionObserved:
                return current.State == SetupState.OrderPending
                    ? (SetupState.PositionOpen, "position-observed", true)
                    : (current.State, "position-state-not-allowed", false);

            case SetupLifecycleFactKind.ExitObserved:
                return current.State is SetupState.PositionOpen or SetupState.ExitPending
                    ? (SetupState.Cooldown, "exit-observed", true)
                    : (current.State, "exit-state-not-allowed", false);

            case SetupLifecycleFactKind.CooldownElapsed:
                return current.State == SetupState.Cooldown
                    ? (SetupState.Terminal, "cooldown-elapsed", true)
                    : (current.State, "cooldown-state-not-allowed", false);

            default:
                return (current.State, "unsupported-lifecycle-fact", false);
        }
    }

    private static (SetupState State, string Reason, bool Accepted) EvaluateEvidence(
        SetupLifecycleSnapshot current, SetupLifecycleInput input)
    {
        if (input.Evidence is null)
        {
            return (current.State, "setup-evidence-missing", false);
        }

        return input.Evidence.State switch
        {
            SetupEvidenceState.Ready when current.State == SetupState.SetupForming =>
                (SetupState.SetupConfirmed, "setup-evidence-ready", true),
            SetupEvidenceState.NoSetup =>
                (current.State, "setup-evidence-no-setup", true),
            SetupEvidenceState.Invalidated =>
                (SetupState.Invalidated, "setup-evidence-invalidated", true),
            _ => (SetupState.Suspended, "setup-evidence-unknown", true)
        };
    }

    private (SetupState State, string Reason, bool Accepted) EvaluateConfirmation(
        SetupLifecycleSnapshot current, SetupLifecycleInput input)
    {
        SetupLifecycleConfirmation? confirmation = input.Confirmation;
        if (confirmation is null)
        {
            return (current.State, "confirmation-missing", false);
        }

        if (confirmation.Status == SetupConfirmationStatus.Veto)
        {
            return (SetupState.Invalidated, "confirmation-veto", true);
        }

        if (confirmation.Status == SetupConfirmationStatus.Unknown)
        {
            return (SetupState.Suspended, "confirmation-unknown", true);
        }

        return current.State == SetupState.SetupConfirmed
            ? (_configuration.RequireConfirmationWindow
                ? SetupState.ConfirmationWindow
                : SetupState.EntryArmed, "confirmation-window", true)
            : (current.State, "confirmation-state-not-allowed", false);
    }

    private static SetupLifecycleSnapshot CreateResetSnapshot(
        SetupLifecycleSnapshot current, SetupLifecycleInput input) =>
        new(input.Identity, SetupState.Observing,
            new CausalEventCursor(input.EventTime, input.StableSequence),
            new ImmutableValueList<SetupLifecycleEventReceipt>(
                [SetupLifecycleEventReceipt.From(input)]), null,
            SetupLifecycleObservation.Empty, false, current.RulesVersion,
            current.ConfigurationVersion, input.AvailabilityTime);

    private static bool IsLate(SetupLifecycleSnapshot current, SetupLifecycleInput input)
    {
        int eventTime = input.EventTime.Value.CompareTo(current.ProcessedCursor.AsOf.Value);
        if (eventTime != 0)
        {
            return eventTime < 0;
        }

        int availability = input.AvailabilityTime.Value.CompareTo(
            current.ProcessedAvailabilityTime.Value);
        if (availability != 0)
        {
            return availability < 0;
        }

        return input.StableSequence < current.ProcessedCursor.MaxStableSequence;
    }

    private static ImmutableValueList<SetupLifecycleEventReceipt> AppendReceipt(
        ImmutableValueList<SetupLifecycleEventReceipt> existing,
        SetupLifecycleEventReceipt receipt)
    {
        List<SetupLifecycleEventReceipt> values = existing.ToList();
        values.Add(receipt);
        return new ImmutableValueList<SetupLifecycleEventReceipt>(values);
    }

    private static SetupLifecycleObservation MergeObservation(
        SetupLifecycleObservation current, string reason) => MergeObservation(current,
            null, null, reason);

    private static SetupLifecycleObservation MergeObservation(
        SetupLifecycleObservation current, string? shadowOutcome,
        string? outcome, string reason)
    {
        List<string> diagnostics = current.Diagnostics.ToList();
        if (!diagnostics.Contains(reason, StringComparer.Ordinal))
        {
            diagnostics.Add(reason);
        }

        return new SetupLifecycleObservation(shadowOutcome ?? current.ShadowOutcome,
            outcome ?? current.Outcome, new ImmutableValueList<string>(diagnostics));
    }

    private static SetupLifecycleTransition Reject(SetupLifecycleSnapshot current,
        SetupLifecycleInput input, string reason) => Transition(
            SetupLifecycleTransitionStatus.Rejected, current, current, input, reason,
            current.Observation);

    private static SetupLifecycleTransition Transition(
        SetupLifecycleTransitionStatus status, SetupLifecycleSnapshot previous,
        SetupLifecycleSnapshot next, SetupLifecycleInput input, string reason,
        SetupLifecycleObservation observation) => SetupLifecycleTransition.Create(status,
            previous, next, input, new ImmutableValueList<string>([reason]), observation);
}

/// <summary>
/// In-memory composition boundary that serializes the complete read-modify-write
/// transition for one instrument while allowing independent instruments to run in
/// parallel. Durable storage belongs to P08-M02.
/// </summary>
public sealed class InstrumentLifecycleProcessor
{
    private readonly SetupLifecycleConfiguration _configuration;
    private readonly SetupLifecycleEngine _engine;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SetupLifecycleSnapshot> _snapshots = new(StringComparer.Ordinal);

    public InstrumentLifecycleProcessor(SetupLifecycleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _engine = new SetupLifecycleEngine(configuration);
    }

    public SetupLifecycleSnapshot GetSnapshot(InstrumentId instrumentId)
    {
        ArgumentNullException.ThrowIfNull(instrumentId);
        return _snapshots.TryGetValue(instrumentId.Value, out SetupLifecycleSnapshot? snapshot)
            ? snapshot
            : throw new KeyNotFoundException($"No lifecycle snapshot exists for {instrumentId.Value}.");
    }

    public SetupLifecycleSnapshot GetOrCreateSnapshot(SetupLifecycleIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _snapshots.GetOrAdd(identity.InstrumentId.Value,
            _ => SetupLifecycleSnapshot.CreateInitial(identity, _configuration));
    }

    public SetupLifecycleTransition Process(SetupLifecycleInput input,
        Func<SetupLifecycleSnapshot, bool>? commit = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        object gate = _locks.GetOrAdd(input.Identity.InstrumentId.Value, _ => new object());
        lock (gate)
        {
            SetupLifecycleSnapshot current = _snapshots.TryGetValue(
                input.Identity.InstrumentId.Value, out SetupLifecycleSnapshot? existing)
                ? existing
                : SetupLifecycleSnapshot.CreateInitial(input.Identity, _configuration);
            if (existing is null)
            {
                // Publish the initial version inside the instrument gate so a
                // rejected commit still leaves the previous state observable.
                _snapshots[input.Identity.InstrumentId.Value] = current;
            }
            SetupLifecycleTransition transition = _engine.Apply(current, input);
            if (transition.Status is SetupLifecycleTransitionStatus.Applied
                or SetupLifecycleTransitionStatus.RecoveryApplied
                or SetupLifecycleTransitionStatus.IdentityConflict)
            {
                if (commit is not null && !commit(transition.NextSnapshot))
                {
                    return SetupLifecycleTransition.Create(
                        SetupLifecycleTransitionStatus.CommitRejected, current, current,
                        input, ["commit-rejected"], current.Observation);
                }

                _snapshots[input.Identity.InstrumentId.Value] = transition.NextSnapshot;
            }

            return transition;
        }
    }
}
