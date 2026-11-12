using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

internal static class SignalOnlyJournalRenderer
{
    private static readonly string[] ConfigurationUnavailableReasons =
        ["public-configuration-unavailable"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string RenderJson(string streamId, SignalJournalSnapshot snapshot) =>
        JsonSerializer.Serialize(CreateView(streamId, snapshot), JsonOptions);

    public static string RenderConsole(string streamId, SignalJournalSnapshot snapshot)
    {
        SignalOnlyJournalView view = CreateView(streamId, snapshot);
        StringBuilder output = new();
        output.AppendLine(CultureInfo.InvariantCulture,
            $"Committed signal journal: {streamId}");
        output.AppendLine(CultureInfo.InvariantCulture,
            $"Signals: {view.Signals.Length}; diagnostics: {view.Diagnostics.Length}; pending outputs: {view.PendingOutputCount}");

        foreach (SignalOnlySignalCard card in view.Signals)
        {
            output.AppendLine(CultureInfo.InvariantCulture,
                $"{card.Direction} {card.Family} {card.SetupTimeframe} — {card.AssessmentState}; {card.ResearchStatus}; statistical validation: {card.StatisticalValidation}");
            output.AppendLine(CultureInfo.InvariantCulture, $"  SignalId: {card.SignalId}");
            output.AppendLine(CultureInfo.InvariantCulture,
                $"  RecordHash: {card.RecordHash}; evidence: {card.EvidenceState} ({card.EvidenceHash})");
            output.AppendLine(CultureInfo.InvariantCulture,
                $"  Interval: [{card.Interval.OpenTime}, {card.Interval.CloseTime}]");
            output.AppendLine(CultureInfo.InvariantCulture,
                $"  Decision cut: {card.DecisionCut.AsOf} / seq {card.DecisionCut.MaxStableSequence}");
            output.AppendLine(CultureInfo.InvariantCulture,
                $"  HTF cut: {card.HigherTimeframeCut.AsOf} / seq {card.HigherTimeframeCut.MaxStableSequence}");
            output.AppendLine(CultureInfo.InvariantCulture,
                $"  Scenario anchor: {card.ScenarioAnchor.EventId}; alert: {card.Scenario?.AlertEventId ?? "unknown"}; state: {card.Scenario?.State ?? "unknown"}");
            output.AppendLine(CultureInfo.InvariantCulture,
                $"  Scenario reference only: {card.ReferencePrice.Price} from {card.ReferencePrice.SourceId}/{card.ReferencePrice.EventId} at {card.ReferencePrice.ObservedAt}");
            output.AppendLine(CultureInfo.InvariantCulture,
                $"  Quality reasons: {(card.QualityReasons.Length == 0 ? "none" : string.Join(", ", card.QualityReasons))}");
            output.AppendLine("  Output receipt: none; publishedAt: —");
        }

        foreach (SignalOnlyDiagnosticView diagnostic in view.Diagnostics)
        {
            output.AppendLine(CultureInfo.InvariantCulture,
                $"DIAGNOSTIC {diagnostic.Code} input={diagnostic.InputEventId}; reasons: {string.Join(", ", diagnostic.ReasonCodes)}");
        }

        foreach (SignalOnlyScenarioEventView scenarioEvent in view.ScenarioEvents)
        {
            output.AppendLine(CultureInfo.InvariantCulture,
                $"SCENARIO {scenarioEvent.Kind} signal={scenarioEvent.SignalId}; related={scenarioEvent.RelatedSignalId ?? "none"}; alert={scenarioEvent.EpisodeAlertEventId ?? "none"}; event={scenarioEvent.EventId}");
        }

        return output.ToString();
    }

    public static string ConfigurationUnavailableDiagnosticJson() =>
        JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            kind = "diagnostic",
            code = "public-configuration-unavailable",
            configurationState = "UnknownConfiguration",
            reasonCodes = ConfigurationUnavailableReasons,
            signal = (object?)null,
            direction = (string?)null,
            outputReceipt = (object?)null,
            publishedAt = (string?)null
        }, JsonOptions);

    private static SignalOnlyJournalView CreateView(string streamId,
        SignalJournalSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(snapshot);
        Dictionary<string, SignalScenarioEpisode> episodes =
            snapshot.ScenarioSnapshot is { } scenario
                ? scenario.Episodes.ToDictionary(
                    static episode => episode.Identity.SignalId,
                    StringComparer.Ordinal)
                : new Dictionary<string, SignalScenarioEpisode>(StringComparer.Ordinal);
        SignalOnlySignalCard[] signals = snapshot.Signals
            .Select(item => CreateCard(item, episodes))
            .OrderBy(static card => card.DecisionAt, StringComparer.Ordinal)
            .ThenBy(static card => card.SignalId, StringComparer.Ordinal)
            .ToArray();
        SignalOnlyDiagnosticView[] diagnostics = snapshot.Diagnostics
            .Select(static item => new SignalOnlyDiagnosticView(item.DiagnosticId,
                item.Code, item.InputEventId, item.InputHash,
                item.ReasonCodes.ToArray()))
            .ToArray();
        SignalOnlyScenarioEventView[] scenarioEvents = snapshot.ScenarioEvents
            .Select(CreateScenarioEvent)
            .ToArray();
        SignalOnlyCheckpointView? checkpoint = snapshot.Checkpoint is { } current
            ? new SignalOnlyCheckpointView(current.InputSequence, current.InputEventId,
                current.InputHash, Iso(current.EventTime), Iso(current.AvailabilityTime),
                current.StableSequence, current.CommitHash, current.ScenarioSnapshotHash)
            : null;
        return new SignalOnlyJournalView(1, streamId, checkpoint, signals,
            diagnostics, scenarioEvents, [], snapshot.Outbox.Count);
    }

    private static SignalOnlySignalCard CreateCard(SignalJournalSignal item,
        IReadOnlyDictionary<string, SignalScenarioEpisode> episodes)
    {
        SignalRecord signal = item.Signal;
        SignalIdentity identity = signal.Identity;
        SignalProvenance provenance = item.Evidence.Provenance;
        SignalScenarioEpisode? episode = episodes.GetValueOrDefault(identity.SignalId);
        return new SignalOnlySignalCard(identity.SignalId, signal.RecordHash,
            item.Evidence.RecordHash, identity.InstrumentId.Value, identity.Venue,
            identity.Direction.ToString(), identity.Family.ToString(),
            identity.SetupTimeframe, identity.ProfileVersion,
            identity.FamilyAlgorithmVersion, signal.AssessmentState.ToString(),
            item.Evidence.State.ToString(), "RESEARCH", "UNKNOWN",
            new SignalOnlyIntervalView(identity.SetupTimeframe,
                Iso(signal.BarInterval.OpenTime), Iso(signal.BarInterval.CloseTime)),
            Iso(signal.DecisionAt), CreateCut(provenance.DecisionCut),
            CreateCut(provenance.HigherTimeframeCut),
            new SignalOnlyScenarioAnchorView(identity.SetupEventId,
                FindAnchor(provenance, identity.SetupEventId)),
            new SignalOnlyReferenceView("scenario-reference-only",
                signal.ReferencePrice.SourceId, signal.ReferencePrice.SourceVersion,
                signal.ReferencePrice.EventId, Iso(signal.ReferencePrice.ObservedAt),
                signal.ReferencePrice.Price.Value),
            provenance.InputReferences.Select(CreateInputReference).ToArray(),
            item.Evidence.QualityReasons.ToArray(),
            episode is null ? null : CreateEpisode(episode), null, null);
    }

    private static SignalOnlyInputReferenceView FindAnchor(
        SignalProvenance provenance, string eventId)
    {
        SignalInputReference anchor = provenance.InputReferences.First(reference =>
            StringComparer.Ordinal.Equals(reference.EventId, eventId)
            && reference.Role == SignalInputRole.Setup);
        return CreateInputReference(anchor);
    }

    private static SignalOnlyInputReferenceView CreateInputReference(
        SignalInputReference reference) => new(reference.Role.ToString(),
        reference.Timeframe, reference.SourceId, reference.SourceVersion,
        reference.EventId, reference.PayloadHash, Iso(reference.EventTime),
        Iso(reference.AvailabilityTime), reference.StableSequence);

    private static SignalOnlyCausalCutView CreateCut(CausalEventCursor cut) =>
        new(Iso(cut.AsOf), cut.MaxStableSequence);

    private static SignalOnlyScenarioEpisodeView CreateEpisode(
        SignalScenarioEpisode episode) => new(episode.Identity.SignalId,
        episode.State.ToString(), episode.AlertEventId, Iso(episode.CreatedAt),
        Iso(episode.ExpiresAt), episode.TerminalAt is null ? null : Iso(episode.TerminalAt),
        episode.TerminalEventId);

    private static SignalOnlyScenarioEventView CreateScenarioEvent(
        SignalScenarioEvent scenarioEvent) => new(scenarioEvent.EventId,
        scenarioEvent.Kind.ToString(), scenarioEvent.SignalId,
        scenarioEvent.RelatedSignalId, scenarioEvent.EpisodeAlertEventId,
        scenarioEvent.RelatedEpisodeAlertEventId, scenarioEvent.CauseEventId,
        Iso(scenarioEvent.EventTime), Iso(scenarioEvent.AvailabilityTime),
        scenarioEvent.StableSequence,
        scenarioEvent.EffectiveAt is null ? null : Iso(scenarioEvent.EffectiveAt),
        scenarioEvent.ReasonCode);

    private static string Iso(UtcTimestamp timestamp) =>
        timestamp.Value.ToString("O", CultureInfo.InvariantCulture);
}

internal sealed record SignalOnlyJournalView(int FormatVersion, string StreamId,
    SignalOnlyCheckpointView? Checkpoint, SignalOnlySignalCard[] Signals,
    SignalOnlyDiagnosticView[] Diagnostics,
    SignalOnlyScenarioEventView[] ScenarioEvents,
    SignalOnlyDeliveryReceiptView[] OutputReceipts, int PendingOutputCount);

internal sealed record SignalOnlyCheckpointView(long InputSequence,
    string InputEventId, string InputHash, string EventTime,
    string AvailabilityTime, long StableSequence, string CommitHash,
    string ScenarioSnapshotHash);

internal sealed record SignalOnlySignalCard(string SignalId, string RecordHash,
    string EvidenceHash, string Instrument, string Venue, string Direction,
    string Family, string SetupTimeframe, string ProfileVersion,
    string FamilyAlgorithmVersion, string AssessmentState, string EvidenceState,
    string ResearchStatus, string StatisticalValidation,
    SignalOnlyIntervalView Interval, string DecisionAt,
    SignalOnlyCausalCutView DecisionCut, SignalOnlyCausalCutView HigherTimeframeCut,
    SignalOnlyScenarioAnchorView ScenarioAnchor,
    SignalOnlyReferenceView ReferencePrice,
    SignalOnlyInputReferenceView[] InputReferences, string[] QualityReasons,
    SignalOnlyScenarioEpisodeView? Scenario,
    SignalOnlyDeliveryReceiptView? OutputReceipt, string? PublishedAt);

internal sealed record SignalOnlyIntervalView(string Timeframe, string OpenTime,
    string CloseTime);

internal sealed record SignalOnlyCausalCutView(string AsOf, long MaxStableSequence);

internal sealed record SignalOnlyScenarioAnchorView(string EventId,
    SignalOnlyInputReferenceView CausalInput);

internal sealed record SignalOnlyReferenceView(string Kind, string SourceId,
    string SourceVersion, string EventId, string ObservedAt, decimal Price);

internal sealed record SignalOnlyInputReferenceView(string Role, string Timeframe,
    string SourceId, string SourceVersion, string EventId, string PayloadHash,
    string EventTime, string AvailabilityTime, long StableSequence);


internal sealed record SignalOnlyScenarioEpisodeView(string SignalId, string State,
    string AlertEventId, string CreatedAt, string ExpiresAt, string? TerminalAt,
    string? TerminalEventId);

internal sealed record SignalOnlyDiagnosticView(string DiagnosticId, string Code,
    string InputEventId, string InputHash, string[] ReasonCodes);

internal sealed record SignalOnlyScenarioEventView(string EventId, string Kind,
    string SignalId, string? RelatedSignalId, string? EpisodeAlertEventId,
    string? RelatedEpisodeAlertEventId, string CauseEventId, string EventTime,
    string AvailabilityTime, long StableSequence, string? EffectiveAt,
    string? ReasonCode);

internal sealed record SignalOnlyDeliveryReceiptView(string ReceiptId,
    string SignalId, string SignalRecordHash, string Channel, string Status,
    string OccurredAt);
