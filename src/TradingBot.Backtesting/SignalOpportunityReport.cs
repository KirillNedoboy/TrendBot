using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Backtesting;

public enum SignalOpportunityState { NoSignal, Blocked, Unknown, Signaled }

/// <summary>One predeclared eligible symbol/time cell, including absence and quality state.</summary>
public sealed record SignalOpportunityInput(string Venue, string Symbol,
    DateTimeOffset Open, DateTimeOffset Close, bool Covered,
    SignalOpportunityState State, string? Reason, IReadOnlyList<string> SignalIds,
    string AvailabilityModel, IReadOnlyList<string>? ReasonCodes = null);

public sealed record SignalOpportunityReport(string Identity, string CanonicalJson);

public sealed record SignalDirectionalMetrics(int Complete, int Positive, int Negative,
    int Ties, decimal? MeanSignedReturn, string? UnknownReason);

public static class SignalOpportunityReporter
{
    public const string FormatVersion = "signal-opportunity-report-v1";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SignalOpportunityReport Create(SignalStudyManifest manifest,
        string datasetId, string runId, string datasetHash, string universeHash,
        string configurationHash, IEnumerable<SignalOpportunityInput> cells,
        IEnumerable<string> journalSignalIds, IEnumerable<string> unmatchedPublicInputIds,
        IEnumerable<SignalObservedOutcome> outcomes, string? captureConfigurationHash = null,
        string? signalJournalHash = null, string? scenarioPolicyHash = null,
        string? captureReportHash = null, string? replayDeclarationHash = null,
        string? journalFreezeHash = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(journalSignalIds);
        ArgumentNullException.ThrowIfNull(unmatchedPublicInputIds);
        if (captureConfigurationHash is not null)
            SignalStudyHashDeclaration.RequireHash(captureConfigurationHash, nameof(captureConfigurationHash));
        if (signalJournalHash is not null)
            SignalStudyHashDeclaration.RequireHash(signalJournalHash, nameof(signalJournalHash));
        if (scenarioPolicyHash is not null)
            SignalStudyHashDeclaration.RequireHash(scenarioPolicyHash, nameof(scenarioPolicyHash));
        if (captureReportHash is not null)
            SignalStudyHashDeclaration.RequireHash(captureReportHash, nameof(captureReportHash));
        if (replayDeclarationHash is not null)
            SignalStudyHashDeclaration.RequireHash(replayDeclarationHash, nameof(replayDeclarationHash));
        if (journalFreezeHash is not null)
            SignalStudyHashDeclaration.RequireHash(journalFreezeHash, nameof(journalFreezeHash));
        RequireInputHash(manifest, SignalStudyInputKind.Dataset, datasetHash);
        RequireInputHash(manifest, SignalStudyInputKind.PointInTimeUniverse, universeHash);
        RequireInputHash(manifest, SignalStudyInputKind.Configuration, configurationHash);
        SignalOpportunityInput[] ordered = cells.OrderBy(static cell => cell.Venue,
                StringComparer.Ordinal).ThenBy(static cell => cell.Symbol,
                StringComparer.Ordinal).ThenBy(static cell => cell.Open).ToArray();
        if (ordered.Any(static cell => string.IsNullOrWhiteSpace(cell.Venue)
                || string.IsNullOrWhiteSpace(cell.Symbol)
                || string.IsNullOrWhiteSpace(cell.AvailabilityModel) || cell.Close <= cell.Open
                || cell.Open.Offset != TimeSpan.Zero || cell.Close.Offset != TimeSpan.Zero
                || !Enum.IsDefined(cell.State)
                || !cell.Covered && cell.State != SignalOpportunityState.Unknown
                || cell.State is SignalOpportunityState.Blocked or SignalOpportunityState.Unknown
                    && string.IsNullOrWhiteSpace(cell.Reason)
                || cell.State == SignalOpportunityState.Signaled && cell.SignalIds.Count == 0
                || cell.State != SignalOpportunityState.Signaled && cell.SignalIds.Count != 0)
            || ordered.GroupBy(static cell => (cell.Venue, cell.Symbol, cell.Open)).Any(
                static group => group.Count() != 1))
            throw new ArgumentException("Opportunity cells must be unique eligible intervals with explicit state.");
        SignalObservedOutcome[] observations = outcomes.OrderBy(static item => item.SignalId,
                StringComparer.Ordinal).ThenBy(static item => item.Clock)
            .ThenBy(static item => item.Channel, StringComparer.Ordinal).ToArray();
        if (observations.GroupBy(static item => item.Identity).Any(static group =>
                group.Select(static item => item.CanonicalHash).Distinct(StringComparer.Ordinal).Count() != 1)
            || observations.Any(static item => item.CanonicalHash != Hash(item.CanonicalJson)))
            throw new ArgumentException("Outcome identities or content conflict.");
        observations = observations.DistinctBy(static item => item.Identity).ToArray();
        string[] allSignalIds = ordered.SelectMany(static item => item.SignalIds).ToArray();
        if (allSignalIds.Length != allSignalIds.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("One logical signal cannot occupy multiple opportunity cells.");
        string[] signalIds = allSignalIds;
        string[] unmatchedSignals = journalSignalIds.Distinct(StringComparer.Ordinal)
            .Except(signalIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        SignalObservedOutcome[] unmatched = observations.Where(item =>
            !signalIds.Contains(item.SignalId, StringComparer.Ordinal)).ToArray();
        var rows = ordered.Select(cell => new
        {
            cell.Venue,
            cell.Symbol,
            cell.Open,
            cell.Close,
            cell.Covered,
            cell.State,
            cell.Reason,
            reasonCodes = cell.ReasonCodes?.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray() ?? [],
            cell.AvailabilityModel,
            signalIds = cell.SignalIds.Order(StringComparer.Ordinal).ToArray(),
            overlap = cell.SignalIds.Distinct(StringComparer.Ordinal).Count() > 1,
            outcomes = observations.Where(item => cell.SignalIds.Contains(item.SignalId))
                .Select(ToMetric).ToArray()
        }).ToArray();
        SignalObservedOutcome[] complete = observations.Where(item =>
            signalIds.Contains(item.SignalId, StringComparer.Ordinal)
            && item.State == SignalObservationState.Complete && item.SignedReturn is not null)
            .ToArray();
        int coveredCount = ordered.Count(static cell => cell.Covered);
        bool coveredDecisionUnknown = ordered.Any(static cell =>
            cell.Covered && cell.State == SignalOpportunityState.Unknown);
        var metrics = new
        {
            expected = ordered.Length,
            covered = coveredCount,
            coverageFraction = ordered.Length == 0 ? (decimal?)null
                : (decimal)coveredCount / ordered.Length,
            signalFrequencyPerCoveredInterval = coveredCount == 0 || coveredDecisionUnknown
                ? (decimal?)null : (decimal)signalIds.Length / coveredCount,
            signalFrequencyUnknownReason = coveredCount == 0 ? "no-covered-intervals"
                : coveredDecisionUnknown ? "unresolved-covered-intervals" : null,
            coverageByAvailabilityModel = ordered.GroupBy(static cell => cell.AvailabilityModel)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new
                {
                    model = group.Key,
                    expected = group.Count(),
                    covered = group.Count(static cell => cell.Covered)
                }).ToArray(),
            noSignal = ordered.Count(static cell => cell.State == SignalOpportunityState.NoSignal),
            blocked = ordered.Count(static cell => cell.State == SignalOpportunityState.Blocked),
            unknown = ordered.Count(static cell => cell.State == SignalOpportunityState.Unknown),
            signaledCells = ordered.Count(static cell => cell.State == SignalOpportunityState.Signaled),
            uniqueSignals = signalIds.Length,
            overlapCells = rows.Count(static row => row.overlap),
            unmatchedSignals = unmatchedSignals.Length,
            unmatchedOutcomes = unmatched.Length,
            decision = Summarize(complete, SignalObservationClock.Decision),
            published = Summarize(complete, SignalObservationClock.ChannelPublished),
            publicationDelaysSeconds = observations.Where(static item =>
                    item.Clock == SignalObservationClock.ChannelPublished && item.Boundary is not null)
                .SelectMany(item => ordered.Where(cell => cell.SignalIds.Contains(item.SignalId))
                    .Select(cell => (decimal)(item.Boundary!.Value - cell.Close).TotalSeconds))
                .ToArray()
        };
        var body = new
        {
            formatVersion = FormatVersion,
            manifestIdentity = manifest.Identity,
            manifestState = manifest.State,
            manifestUnknownReasons = manifest.UnknownReasons,
            datasetId,
            runId,
            datasetHash,
            universeHash,
            configurationHash,
            captureConfigurationHash,
            captureReportHash,
            signalJournalHash,
            scenarioPolicyHash,
            replayDeclarationHash,
            journalFreezeHash,
            signalJournalUnknownReason = signalJournalHash is null ? "signal-journal-unavailable" : null,
            denominators = metrics,
            frequencyDenominator = metrics.covered,
            exclusions = rows.Where(static row => !row.Covered || row.State is
                    SignalOpportunityState.Blocked or SignalOpportunityState.Unknown)
                .Select(static row => new { row.Symbol, row.Open, row.Reason, row.reasonCodes }).ToArray(),
            rows,
            unmatchedSignalIds = unmatchedSignals,
            unmatchedPublicInputIds = unmatchedPublicInputIds.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
            unmatchedEvents = unmatched.Select(ToMetric).ToArray(),
            controls = new[]
            {
                new { name = "MA-only", status = "not-evaluated", requirement =
                    "Frozen MA kind, period, seed, warmup and common-universe labels required." },
                new { name = "price-volume", status = "not-evaluated", requirement =
                    "Frozen price/volume formula, source, normalization and common-universe labels required." }
            },
            uncertainty = "No OOS comparison or clustered confidence interval computed; overlapping signals are not independent.",
            hypotheticalNetExpectancy = (decimal?)null,
            hypotheticalNetUnknownReason = "usable-cost-latency-funding-and-nonfill-inputs-unavailable; not-real-profit"
        };
        string json = JsonSerializer.Serialize(body, Options);
        return new SignalOpportunityReport(Hash(json), json);
    }

    private static SignalDirectionalMetrics Summarize(
        IEnumerable<SignalObservedOutcome> observations, SignalObservationClock clock)
    {
        SignalObservedOutcome[] selected = observations.Where(item => item.Clock == clock).ToArray();
        return new SignalDirectionalMetrics(selected.Length,
            selected.Count(static item => item.SignedReturn > 0),
            selected.Count(static item => item.SignedReturn < 0),
            selected.Count(static item => item.SignedReturn == 0),
            selected.Length == 0 ? null : selected.Average(static item => item.SignedReturn!.Value),
            selected.Length == 0 ? "no-complete-directional-outcomes" : null);
    }

    private static object ToMetric(SignalObservedOutcome item) => new
    {
        item.SignalId,
        item.Clock,
        item.Channel,
        item.Boundary,
        item.State,
        item.Reason,
        item.ObservationReferencePrice,
        item.ObservationReferenceAt,
        item.SignedReturn,
        item.Mae,
        item.Mfe,
        item.Passage,
        item.FirstPassageAt,
        item.FirstPassageEarliest,
        item.FirstPassageLatest,
        item.Horizon,
        item.EffectiveEnd,
        item.CoveredBars,
        item.Identity,
        item.CanonicalHash
    };

    private static void RequireInputHash(SignalStudyManifest manifest,
        SignalStudyInputKind kind, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        SignalStudyHashDeclaration declaration = manifest.InputHashes.Single(item => item.Kind == kind);
        if (!StringComparer.OrdinalIgnoreCase.Equals(declaration.ContentHash, hash))
            throw new ArgumentException($"The {kind} hash differs from the frozen manifest.");
    }

    private static string Hash(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
