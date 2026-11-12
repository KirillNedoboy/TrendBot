namespace TradingBot.Core;

/// <summary>
/// Pure, event-time-driven projection of passive signal episodes. It does not
/// publish alerts, persist checkpoints, evaluate fills, or create positions.
/// </summary>
public sealed class SignalScenarioProjectionEngine
{
    private readonly SignalScenarioPolicySet _policies;

    public SignalScenarioProjectionEngine(SignalScenarioPolicySet policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = policies;
    }

    public SignalScenarioProjectionSnapshot CreateInitialSnapshot() =>
        SignalScenarioProjectionSnapshot.CreateInitial(_policies);

    public SignalScenarioProjectionTransition Apply(
        SignalScenarioProjectionSnapshot current, SignalScenarioInput input)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(input);
        if (current.Policies != _policies)
        {
            return Result(SignalScenarioTransitionStatus.Rejected,
                "scenario-policy-set-mismatch", current, []);
        }

        if (!_policies.IsReady)
        {
            return Result(SignalScenarioTransitionStatus.UnknownPolicy,
                string.Join(";", _policies.UnknownReasons), current, []);
        }

        SignalScenarioProcessedInput? prior = current.ProcessedInputs.FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.StreamId, input.StreamId)
            && StringComparer.Ordinal.Equals(item.EventId, input.EventId));
        if (prior is not null)
        {
            return StringComparer.Ordinal.Equals(prior.InputHash, input.InputHash)
                ? Result(SignalScenarioTransitionStatus.Duplicate, "duplicate-event",
                    current, [])
                : Result(SignalScenarioTransitionStatus.IdentityConflict,
                    "event-identity-conflict", current, []);
        }

        if (IsLate(current, input))
        {
            return Result(SignalScenarioTransitionStatus.Late, "late-event", current, []);
        }

        List<SignalScenarioEpisode> episodes = current.Episodes.ToList();
        List<SignalScenarioEvent> events = current.Events.ToList();
        List<SignalScenarioEvent> emitted = [];
        ExpireDueEpisodes(episodes, events, emitted, input);

        (SignalScenarioTransitionStatus status, string reason) = input.Kind switch
        {
            SignalScenarioInputKind.Confirm => Confirm(episodes, events, emitted, input),
            SignalScenarioInputKind.Invalidate => Invalidate(episodes, events,
                emitted, input),
            _ => (SignalScenarioTransitionStatus.Rejected, "unsupported-input-kind")
        };

        List<SignalScenarioProcessedInput> processed = current.ProcessedInputs.ToList();
        processed.Add(SignalScenarioProcessedInput.From(input));
        SignalScenarioProjectionSnapshot next = new(
            SignalScenarioProjectionSnapshot.CurrentFormatVersion,
            SignalScenarioProjectionSnapshot.CurrentProjectionVersion,
            _policies, new CausalEventCursor(input.EventTime, input.StableSequence),
            input.AvailabilityTime,
            new ImmutableValueList<SignalScenarioProcessedInput>(processed),
            new ImmutableValueList<SignalScenarioEpisode>(episodes),
            new ImmutableValueList<SignalScenarioEvent>(events));
        return Result(status, reason, next, emitted);
    }

    public ImmutableValueList<SignalScenarioProjectionTransition> Replay(
        SignalScenarioProjectionSnapshot initial,
        IEnumerable<SignalScenarioInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(inputs);
        List<SignalScenarioProjectionTransition> transitions = [];
        SignalScenarioProjectionSnapshot current = initial;
        foreach (SignalScenarioInput input in inputs)
        {
            SignalScenarioProjectionTransition transition = Apply(current, input);
            transitions.Add(transition);
            current = transition.NextSnapshot;
        }

        return new ImmutableValueList<SignalScenarioProjectionTransition>(transitions);
    }

    private void ExpireDueEpisodes(List<SignalScenarioEpisode> episodes,
        List<SignalScenarioEvent> events, List<SignalScenarioEvent> emitted,
        SignalScenarioInput input)
    {
        foreach (SignalScenarioEpisode episode in episodes
            .Where(item => item.State == SignalScenarioEpisodeState.Active
                && item.ExpiresAt.Value <= input.EventTime.Value)
            .OrderBy(item => item.Identity.SignalId, StringComparer.Ordinal)
            .ToArray())
        {
            SignalScenarioEvent expired = SignalScenarioEvent.Create(
                SignalScenarioEventKind.Expired, episode.Identity.SignalId, null,
                episode.AlertEventId, null, input, "expiry-boundary-reached",
                episode.ExpiresAt, _policies);
            int index = episodes.FindIndex(item =>
                StringComparer.Ordinal.Equals(item.Identity.SignalId,
                    episode.Identity.SignalId));
            episodes[index] = episode.Close(SignalScenarioEpisodeState.Expired,
                episode.ExpiresAt, input.StableSequence, expired.EventId);
            events.Add(expired);
            emitted.Add(expired);
        }
    }

    private (SignalScenarioTransitionStatus Status, string Reason) Confirm(
        List<SignalScenarioEpisode> episodes, List<SignalScenarioEvent> events,
        List<SignalScenarioEvent> emitted, SignalScenarioInput input)
    {
        SignalScenarioAnchor anchor = input.Anchor!;
        SignalIdentity identity = anchor.Identity;
        SignalScenarioEpisode? sameEpisode = episodes.FirstOrDefault(episode =>
            StringComparer.Ordinal.Equals(episode.Identity.SignalId, identity.SignalId));
        if (sameEpisode is not null)
        {
            if (sameEpisode.State != SignalScenarioEpisodeState.Active)
            {
                return (SignalScenarioTransitionStatus.Rejected,
                    "rearm-requires-new-anchor");
            }

            SignalScenarioEvent continued = SignalScenarioEvent.Create(
                SignalScenarioEventKind.Continued, identity.SignalId, null,
                sameEpisode.AlertEventId, null, input, null, null, _policies);
            events.Add(continued);
            emitted.Add(continued);
            return (SignalScenarioTransitionStatus.Continued,
                "same-logical-scenario");
        }

        SignalScenarioEpisode[] sameDirection = episodes.Where(episode =>
            episode.Identity.InstrumentId == identity.InstrumentId
            && episode.Identity.Family == identity.Family
            && episode.Identity.Direction == identity.Direction).ToArray();
        if (sameDirection.Any(episode =>
            episode.State == SignalScenarioEpisodeState.Active))
        {
            return (SignalScenarioTransitionStatus.Rejected,
                "active-episode-prevents-rearm");
        }

        SignalScenarioEpisode? lastTerminal = sameDirection
            .Where(episode => episode.TerminalAt is not null)
            .OrderByDescending(episode => episode.TerminalAt!.Value)
            .ThenByDescending(episode => episode.TerminalStableSequence)
            .FirstOrDefault();
        if (lastTerminal is not null)
        {
            if (StringComparer.Ordinal.Equals(lastTerminal.Anchor.AnchorEventId,
                anchor.AnchorEventId))
            {
                return (SignalScenarioTransitionStatus.Rejected,
                    "rearm-requires-new-anchor");
            }

            bool anchorFollowsTerminal = anchor.AnchorEventTime.Value
                > lastTerminal.TerminalAt!.Value
                || (anchor.AnchorEventTime.Value == lastTerminal.TerminalAt.Value
                    && anchor.AnchorStableSequence
                        > lastTerminal.TerminalStableSequence!.Value);
            if (!anchorFollowsTerminal)
            {
                return (SignalScenarioTransitionStatus.Rejected,
                    "rearm-anchor-does-not-follow-terminal");
            }

            DateTimeOffset earliestRearm;
            try
            {
                earliestRearm = lastTerminal.TerminalAt!.Value.Add(
                    _policies.Rearm!.MinimumAfterTerminal!.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                return (SignalScenarioTransitionStatus.Rejected,
                    "rearm-boundary-out-of-range");
            }

            if (input.EventTime.Value < earliestRearm)
            {
                return (SignalScenarioTransitionStatus.Rejected,
                    "rearm-delay-not-elapsed");
            }
        }

        if (!SignalScenarioProjectionSnapshot.TryAdd(input.EventTime,
            _policies.Expiry!.MaximumAge!.Value, out UtcTimestamp? expiresAt))
        {
            return (SignalScenarioTransitionStatus.Rejected,
                "scenario-expiry-out-of-range");
        }

        CancelOppositeEpisodes(episodes, events, emitted, input, identity);
        SignalScenarioEvent alert = SignalScenarioEvent.Create(
            SignalScenarioEventKind.AlertCreated, identity.SignalId, null, null,
            null, input, null, null, _policies);
        episodes.Add(new SignalScenarioEpisode(identity, anchor,
            SignalScenarioEpisodeState.Active, alert.EventId, input.EventTime,
            expiresAt!, null, null, null));
        events.Add(alert);
        emitted.Add(alert);
        LinkOverlappingFamilies(episodes, events, emitted, input, identity,
            expiresAt!, alert);
        return (SignalScenarioTransitionStatus.Applied, "alert-created");
    }

    private void CancelOppositeEpisodes(List<SignalScenarioEpisode> episodes,
        List<SignalScenarioEvent> events, List<SignalScenarioEvent> emitted,
        SignalScenarioInput input, SignalIdentity identity)
    {
        if (!_policies.Invalidation!.CancelOnOppositeDirection)
        {
            return;
        }

        foreach (SignalScenarioEpisode episode in episodes
            .Where(item => item.State == SignalScenarioEpisodeState.Active
                && item.Identity.InstrumentId == identity.InstrumentId
                && item.Identity.Family == identity.Family
                && item.Identity.Direction != identity.Direction)
            .OrderBy(item => item.Identity.SignalId, StringComparer.Ordinal)
            .ToArray())
        {
            SignalScenarioEvent canceled = SignalScenarioEvent.Create(
                SignalScenarioEventKind.Canceled, episode.Identity.SignalId, null,
                episode.AlertEventId, null, input, "opposite-direction-confirmed",
                input.EventTime, _policies);
            int index = episodes.FindIndex(item =>
                StringComparer.Ordinal.Equals(item.Identity.SignalId,
                    episode.Identity.SignalId));
            episodes[index] = episode.Close(SignalScenarioEpisodeState.Canceled,
                input.EventTime, input.StableSequence, canceled.EventId);
            events.Add(canceled);
            emitted.Add(canceled);
        }
    }

    private void LinkOverlappingFamilies(List<SignalScenarioEpisode> episodes,
        List<SignalScenarioEvent> events, List<SignalScenarioEvent> emitted,
        SignalScenarioInput input, SignalIdentity identity, UtcTimestamp expiresAt,
        SignalScenarioEvent newAlert)
    {
        SignalScenarioEpisode[] overlapping = episodes.Where(episode =>
            episode.State == SignalScenarioEpisodeState.Active
            && episode.Identity.InstrumentId == identity.InstrumentId
            && episode.Identity.Direction == identity.Direction
            && episode.Identity.Family != identity.Family
            && input.EventTime.Value < episode.ExpiresAt.Value
            && expiresAt.Value > episode.CreatedAt.Value)
            .OrderBy(episode => episode.Identity.SignalId, StringComparer.Ordinal)
            .ToArray();

        foreach (SignalScenarioEpisode episode in overlapping)
        {
            bool newFirst = StringComparer.Ordinal.Compare(identity.SignalId,
                episode.Identity.SignalId) < 0;
            string firstSignalId = newFirst ? identity.SignalId : episode.Identity.SignalId;
            string secondSignalId = newFirst ? episode.Identity.SignalId : identity.SignalId;
            string firstAlertId = newFirst ? newAlert.EventId : episode.AlertEventId;
            string secondAlertId = newFirst ? episode.AlertEventId : newAlert.EventId;
            bool alreadyLinked = events.Any(item =>
                item.Kind == SignalScenarioEventKind.OverlapLinked
                && item.SignalId == firstSignalId
                && item.RelatedSignalId == secondSignalId);
            if (alreadyLinked)
            {
                continue;
            }

            SignalScenarioEvent link = SignalScenarioEvent.Create(
                SignalScenarioEventKind.OverlapLinked, firstSignalId,
                secondSignalId, firstAlertId, secondAlertId, input, null, null,
                _policies);
            events.Add(link);
            emitted.Add(link);
        }
    }

    private (SignalScenarioTransitionStatus Status, string Reason) Invalidate(
        List<SignalScenarioEpisode> episodes, List<SignalScenarioEvent> events,
        List<SignalScenarioEvent> emitted, SignalScenarioInput input)
    {
        SignalScenarioEpisode? episode = episodes.FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.Identity.SignalId,
                input.TargetSignalId));
        if (episode is null || episode.State != SignalScenarioEpisodeState.Active)
        {
            return (SignalScenarioTransitionStatus.Rejected,
                "active-scenario-not-found");
        }

        if (!_policies.Invalidation!.CancelOnExplicitInvalidation)
        {
            return (SignalScenarioTransitionStatus.Applied,
                "explicit-invalidation-retained-by-policy");
        }

        SignalScenarioEvent canceled = SignalScenarioEvent.Create(
            SignalScenarioEventKind.Canceled, episode.Identity.SignalId, null,
            episode.AlertEventId, null, input, input.ReasonCode,
            input.EventTime, _policies);
        int index = episodes.FindIndex(item =>
            StringComparer.Ordinal.Equals(item.Identity.SignalId,
                episode.Identity.SignalId));
        episodes[index] = episode.Close(SignalScenarioEpisodeState.Canceled,
            input.EventTime, input.StableSequence, canceled.EventId);
        events.Add(canceled);
        emitted.Add(canceled);
        return (SignalScenarioTransitionStatus.Applied, "scenario-canceled");
    }

    private static bool IsLate(SignalScenarioProjectionSnapshot current,
        SignalScenarioInput input)
    {
        if (current.ProcessedInputs.Count == 0)
        {
            return false;
        }

        return input.EventTime.Value < current.ProcessedCursor.AsOf.Value
            || input.AvailabilityTime.Value < current.ProcessedAvailabilityTime.Value
            || input.StableSequence <= current.ProcessedCursor.MaxStableSequence;
    }

    private static SignalScenarioProjectionTransition Result(
        SignalScenarioTransitionStatus status, string reason,
        SignalScenarioProjectionSnapshot snapshot,
        IEnumerable<SignalScenarioEvent> emitted) =>
        new(status, reason, snapshot, emitted);
}
