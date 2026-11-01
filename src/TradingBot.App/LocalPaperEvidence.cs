using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>All causal timestamps retained for one local-paper observation.</summary>
public sealed record LocalPaperEvidenceCausalRecord(string StreamId, long StableSequence,
    string EventId, long EventTimeTicks, long? ExchangeTimeTicks, long ReceiveTimeTicks,
    long AvailabilityTimeTicks);

/// <summary>One immutable fill copied from the durable virtual-fill journal.</summary>
public sealed record LocalPaperEvidenceFill(string FillId, string InstrumentId,
    Direction Direction, bool IsEntry, string Role, decimal Quantity, decimal Price,
    decimal FeeAmount, decimal SlippageAmount, string Currency, long FilledAtTicks,
    long StableSequence, string EventId);

/// <summary>One observation together with its persisted decision and risk result.</summary>
public sealed record LocalPaperEvidenceObservation(string StreamId, long Sequence,
    string EventId, string EnvelopeJson, string? ApplicationJson, string ObservationIdentity,
    string ConfigurationIdentity, string? PlanIdentity, string? RiskInputIdentity,
    string? ModelIdentity, string Status, string? BlockReason, string? ResultIdentity,
    string? ResultJson, string? RiskJson, string? PlanJson, string? DecisionJson,
    string? FillAssumptionsJson, decimal? RemainingQuantity, string? ExitReason,
    IReadOnlyList<string> Diagnostics, decimal? TotalFees, decimal? TotalSlippage,
    LocalPaperEvidenceCausalRecord Causal);

/// <summary>
/// Immutable, content-addressed evidence for one run-scoped local-paper
/// journal. This report is read-only synthetic evidence and never grants
/// private, exchange, or real-order authority.
/// </summary>
public sealed class LocalPaperEvidence
{
    public const string CurrentFormatVersion = "local-paper-evidence-v1";

    private static readonly string[] DefaultExclusions =
    [
        "real-orders",
        "private-execution",
        "activation",
        "network",
        "credentials"
    ];

    private readonly ReadOnlyCollection<LocalPaperEvidenceObservation> _observations;
    private readonly ReadOnlyCollection<LocalPaperEvidenceFill> _fills;
    private readonly ReadOnlyCollection<string> _modelIdentities;
    private readonly ReadOnlyCollection<string> _planIdentities;
    private readonly ReadOnlyCollection<string> _provenance;
    private readonly ReadOnlyCollection<string> _exclusions;
    private readonly ReadOnlyCollection<string> _blockers;
    private readonly ReadOnlyCollection<string> _unknowns;
    private readonly bool _synthetic;

    private LocalPaperEvidence(string formatVersion, string identity, string streamId,
        string sourceIdentity, string configurationIdentity,
        IEnumerable<string> modelIdentities, IEnumerable<string> planIdentities,
        IEnumerable<LocalPaperEvidenceObservation> observations,
        IEnumerable<LocalPaperEvidenceFill> fills, LocalPaperLedgerSnapshot? ledger,
        IEnumerable<string> provenance, IEnumerable<string> exclusions,
        IEnumerable<string> blockers, IEnumerable<string> unknowns, string canonicalJson)
    {
        if (!StringComparer.Ordinal.Equals(formatVersion, CurrentFormatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        RequireSingleLine(sourceIdentity, nameof(sourceIdentity));
        RequireSingleLine(configurationIdentity, nameof(configurationIdentity));
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalJson);

        FormatVersion = formatVersion;
        Identity = identity;
        StreamId = streamId;
        SourceIdentity = sourceIdentity;
        ConfigurationIdentity = configurationIdentity;
        _modelIdentities = SnapshotText(modelIdentities, nameof(modelIdentities));
        _planIdentities = SnapshotText(planIdentities, nameof(planIdentities));
        _observations = SnapshotObservations(observations);
        _fills = SnapshotFills(fills);
        Ledger = ledger;
        _provenance = SnapshotText(provenance, nameof(provenance));
        _exclusions = SnapshotText(exclusions, nameof(exclusions));
        _blockers = SnapshotText(blockers, nameof(blockers));
        _unknowns = SnapshotText(unknowns, nameof(unknowns));
        _synthetic = true;
        CanonicalJson = canonicalJson;
    }

    public string FormatVersion { get; }

    /// <summary>SHA-256 identity of the canonical report body.</summary>
    public string Identity { get; }

    public string EvidenceIdentity => Identity;

    public string StreamId { get; }

    public string SourceIdentity { get; }

    /// <summary>The one configuration identity, or <c>unknown</c> when inconsistent.</summary>
    public string ConfigurationIdentity { get; }

    public IReadOnlyList<string> ModelIdentities => _modelIdentities;

    public IReadOnlyList<string> PlanIdentities => _planIdentities;

    public IReadOnlyList<LocalPaperEvidenceObservation> Observations => _observations;

    public IReadOnlyList<LocalPaperEvidenceFill> Fills => _fills;

    public LocalPaperLedgerSnapshot? Ledger { get; }

    public IReadOnlyList<string> Provenance => _provenance;

    public IReadOnlyList<string> Exclusions => _exclusions;

    public IReadOnlyList<string> Blockers => _blockers;

    public IReadOnlyList<string> Unknowns => _unknowns;

    /// <summary>Local-paper is explicitly synthetic and has no real orders.</summary>
    public long RealOrderCount => _synthetic ? 0 : 0;

    public bool HasExecutionAuthority => _synthetic && false;

    public bool IsBlocked => Blockers.Count != 0;

    public bool IsComplete => !IsBlocked && Unknowns.Count == 0 && Ledger is not null;

    public string CanonicalJson { get; }

    public string ContentHash => Hash(CanonicalJson);

    public string CanonicalSha256 => ContentHash;

    /// <summary>Captures only durable store state; it does not run simulation.</summary>
    public static LocalPaperEvidence Capture(LocalPaperStore store, string streamId,
        string sourceIdentity, IEnumerable<string>? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        RequireSingleLine(sourceIdentity, nameof(sourceIdentity));

        string[] sourceProvenance = NormalizeText(provenance ?? []);
        try
        {
            LocalPaperAuditSnapshot? audit = store.ReadAuditSnapshot(streamId);
            return audit is null
                ? CreateFailure(streamId, sourceIdentity, sourceProvenance,
                    "local-paper-stream-unavailable", "local-paper-journal-unknown")
                : CreateFromAudit(audit, sourceIdentity, sourceProvenance);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or InvalidOperationException or JsonException or OverflowException
            or ArgumentException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Evidence export is deliberately fail-closed. The caller receives
            // a canonical report with a blocker, never a partial or optimistic
            // ledger claim derived from corrupt state.
            return CreateFailure(streamId, sourceIdentity, sourceProvenance,
                "local-paper-store-corrupt", "local-paper-journal-and-ledger-unknown");
        }
    }

    public static LocalPaperEvidence Build(LocalPaperStore store, string streamId,
        string sourceIdentity, IEnumerable<string>? provenance = null) =>
        Capture(store, streamId, sourceIdentity, provenance);

    public static LocalPaperEvidence Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        EvidenceWire wire;
        try
        {
            wire = CoreJsonSerializer.Deserialize<EvidenceWire>(json);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or NotSupportedException or ArgumentException)
        {
            throw new JsonException("Local-paper evidence JSON is invalid.", exception);
        }

        if (wire.EvidenceIdentity is null || wire.StreamId is null
            || wire.SourceIdentity is null || wire.ConfigurationIdentity is null
            || wire.ModelIdentities is null || wire.PlanIdentities is null
            || wire.Observations is null || wire.Fills is null
            || wire.Provenance is null || wire.Exclusions is null
            || wire.Blockers is null || wire.Unknowns is null)
        {
            throw new JsonException("Local-paper evidence metadata is incomplete.");
        }

        LocalPaperEvidence evidence;
        try
        {
            evidence = Create(wire.StreamId, wire.SourceIdentity,
                wire.ConfigurationIdentity, wire.ModelIdentities, wire.PlanIdentities,
                wire.Observations, wire.Fills, FromWire(wire.Ledger), wire.Provenance,
                wire.Exclusions, wire.Blockers, wire.Unknowns);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException or InvalidOperationException)
        {
            throw new JsonException("Local-paper evidence rows are inconsistent.", exception);
        }

        if (!StringComparer.Ordinal.Equals(wire.EvidenceIdentity, evidence.Identity)
            || !StringComparer.Ordinal.Equals(json, evidence.CanonicalJson))
        {
            throw new JsonException("Local-paper evidence identity or canonical JSON is invalid.");
        }

        return evidence;
    }

    /// <summary>Projects the report into the existing promotion-evidence bundle.</summary>
    public PromotionEvidenceSnapshot ToPromotionEvidenceSnapshot()
    {
        List<PromotionEvidenceCausalField> causal =
        [
            new("streamId", StreamId),
            new("sourceIdentity", SourceIdentity),
            new("localPaperIdentity", Identity)
        ];
        foreach (LocalPaperEvidenceObservation observation in Observations)
        {
            string prefix = $"causal.{observation.Sequence}.";
            causal.Add(new(prefix + "eventId", observation.Causal.EventId));
            causal.Add(new(prefix + "eventTimeTicks",
                observation.Causal.EventTimeTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            causal.Add(new(prefix + "exchangeTimeTicks",
                observation.Causal.ExchangeTimeTicks?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            causal.Add(new(prefix + "receiveTimeTicks",
                observation.Causal.ReceiveTimeTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            causal.Add(new(prefix + "availabilityTimeTicks",
                observation.Causal.AvailabilityTimeTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return PromotionEvidenceSnapshot.FromArtifact(
            PromotionEvidenceCategory.LocalPaper,
            Identity,
            CanonicalJson,
            causalFields: causal,
            provenance: Provenance.Append("local-paper-sqlite-audit-v1"),
            exclusions: Exclusions,
            unknowns: Unknowns,
            blockers: Blockers.Append("execution-authority-not-granted"),
            referencedIdentities: [StreamId, SourceIdentity, ConfigurationIdentity]);
    }

    private static LocalPaperEvidence CreateFromAudit(LocalPaperAuditSnapshot audit,
        string sourceIdentity, IReadOnlyList<string> provenance)
    {
        List<LocalPaperEvidenceObservation> observations = [];
        foreach (LocalPaperObservationRecord row in audit.Observations
                     .OrderBy(static item => item.Sequence)
                     .ThenBy(static item => item.EventId, StringComparer.Ordinal))
        {
            ReplayEnvelope envelope;
            try
            {
                envelope = CoreJsonSerializer.Deserialize<ReplayEnvelope>(row.EnvelopeJson);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException
                or InvalidOperationException or NotSupportedException)
            {
                throw new InvalidDataException("A persisted paper envelope is corrupt.", exception);
            }

            if (!StringComparer.Ordinal.Equals(envelope.StreamId, audit.StreamId)
                || envelope.CausalEvent.StableSequence != row.Sequence
                || !StringComparer.Ordinal.Equals(envelope.CausalEvent.Payload.EventId,
                    row.EventId))
            {
                throw new InvalidDataException("A persisted paper envelope identity is inconsistent.");
            }

            ResultProjection result = ParseResult(row.ResultJson);
            LocalPaperEvidenceCausalRecord causal = new(audit.StreamId, row.Sequence, row.EventId,
                envelope.CausalEvent.Payload.EventTime.Value.UtcDateTime.Ticks,
                envelope.CausalEvent.ExchangeTime?.Value.UtcDateTime.Ticks,
                envelope.CausalEvent.ReceiveTime.Value.UtcDateTime.Ticks,
                envelope.CausalEvent.AvailabilityTime.Value.UtcDateTime.Ticks);
            observations.Add(new LocalPaperEvidenceObservation(audit.StreamId, row.Sequence,
                row.EventId, row.EnvelopeJson, row.ApplicationJson, row.ObservationIdentity,
                row.ConfigurationIdentity, row.PlanIdentity, row.RiskInputIdentity,
                row.ModelIdentity, row.Status, row.BlockReason, row.ResultIdentity,
                row.ResultJson, result.RiskJson, result.PlanJson, result.DecisionJson,
                result.FillAssumptionsJson, result.RemainingQuantity, result.ExitReason,
                result.Diagnostics, result.TotalFees, result.TotalSlippage, causal));
        }

        string[] configurationIdentities = observations
            .Select(static item => item.ConfigurationIdentity)
            .Where(static item => !StringComparer.Ordinal.Equals(item, "unknown"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string configurationIdentity = configurationIdentities.Length == 1
            ? configurationIdentities[0]
            : "unknown";
        List<string> blockers = [];
        if (observations.Count == 0)
        {
            blockers.Add("local-paper-journal-empty");
        }

        if (configurationIdentities.Length > 1)
        {
            blockers.Add("configuration-identity-changed");
        }

        foreach (LocalPaperEvidenceObservation observation in observations)
        {
            if (StringComparer.Ordinal.Equals(observation.Status, "blocked"))
            {
                blockers.Add($"blocked-observation:{observation.Sequence}:{observation.BlockReason ?? "unspecified"}");
            }
            else if (StringComparer.Ordinal.Equals(observation.Status, "pending"))
            {
                blockers.Add($"pending-observation:{observation.Sequence}");
            }

            if (StringComparer.Ordinal.Equals(observation.Status, "complete")
                && observation.ResultJson is null)
            {
                blockers.Add($"completed-observation-result-missing:{observation.Sequence}");
            }
        }

        if (audit.Ledger.RecoveryBlocked)
        {
            blockers.Add("paper-recovery-blocked");
        }

        List<string> unknowns = [];
        if (audit.Ledger.Positions.Count != 0)
        {
            unknowns.Add("open-virtual-position");
        }

        if (audit.Ledger.ReservedPositionSlots != 0)
        {
            unknowns.Add("active-capacity-reservation");
        }

        if (observations.Any(static item => item.ResultJson is null))
        {
            unknowns.Add("observation-result-not-yet-available");
        }

        string[] modelIdentities = observations.Select(static item => item.ModelIdentity)
            .Where(static item => item is not null)
            .Select(static item => item!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] planIdentities = observations.Select(static item => item.PlanIdentity)
            .Where(static item => item is not null)
            .Select(static item => item!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return Create(audit.StreamId, sourceIdentity, configurationIdentity, modelIdentities,
            planIdentities, observations, audit.Fills.Select(ToEvidenceFill), audit.Ledger,
            provenance, DefaultExclusions, blockers, unknowns);
    }

    private static LocalPaperEvidence CreateFailure(string streamId, string sourceIdentity,
        IReadOnlyList<string> provenance, string blocker, string unknown)
    {
        return Create(streamId, sourceIdentity, "unknown", [], [], [], [], null, provenance,
            DefaultExclusions, [blocker], [unknown]);
    }

    private static LocalPaperEvidence Create(string streamId, string sourceIdentity,
        string configurationIdentity, IEnumerable<string> modelIdentities,
        IEnumerable<string> planIdentities, IEnumerable<LocalPaperEvidenceObservation> observations,
        IEnumerable<LocalPaperEvidenceFill> fills, LocalPaperLedgerSnapshot? ledger,
        IEnumerable<string> provenance, IEnumerable<string> exclusions,
        IEnumerable<string> blockers, IEnumerable<string> unknowns)
    {
        string[] normalizedModels = NormalizeText(modelIdentities);
        string[] normalizedPlans = NormalizeText(planIdentities);
        LocalPaperEvidenceObservation[] normalizedObservations = SnapshotObservations(observations)
            .ToArray();
        LocalPaperEvidenceFill[] normalizedFills = SnapshotFills(fills).ToArray();
        string[] normalizedProvenance = NormalizeText(provenance);
        string[] normalizedExclusions = NormalizeText(exclusions);
        string[] normalizedBlockers = NormalizeText(blockers);
        string[] normalizedUnknowns = NormalizeText(unknowns);
        EvidenceBody body = new(CurrentFormatVersion, streamId, sourceIdentity,
            configurationIdentity, normalizedModels, normalizedPlans, normalizedObservations,
            normalizedFills, ToWire(ledger), normalizedProvenance, normalizedExclusions,
            normalizedBlockers, normalizedUnknowns, 0, false);
        string identity = Hash(CoreJsonSerializer.Serialize(body));
        string canonical = CoreJsonSerializer.Serialize(new EvidenceWire(CurrentFormatVersion,
            identity, streamId, sourceIdentity, configurationIdentity, normalizedModels,
            normalizedPlans, normalizedObservations, normalizedFills, ToWire(ledger),
            normalizedProvenance, normalizedExclusions, normalizedBlockers, normalizedUnknowns,
            0, false));
        return new LocalPaperEvidence(CurrentFormatVersion, identity, streamId, sourceIdentity,
            configurationIdentity, normalizedModels, normalizedPlans, normalizedObservations,
            normalizedFills, ledger, normalizedProvenance, normalizedExclusions,
            normalizedBlockers, normalizedUnknowns, canonical);
    }

    private static LocalPaperEvidenceFill ToEvidenceFill(LocalPaperFillRecord fill) =>
        new(fill.FillId, fill.InstrumentId, fill.Direction, fill.IsEntry, fill.Role,
            fill.Quantity, fill.Price, fill.FeeAmount, fill.SlippageAmount, fill.Currency,
            fill.FilledAtTicks, fill.StableSequence, fill.EventId);

    private static ResultProjection ParseResult(string? resultJson)
    {
        if (resultJson is null)
        {
            return ResultProjection.Empty;
        }

        using JsonDocument document = JsonDocument.Parse(resultJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A persisted paper result must be an object.");
        }

        string? planJson = OptionalString(root, "planJson");
        string? decisionJson = ExtractObject(planJson, "decision");
        return new ResultProjection(OptionalString(root, "riskJson"), planJson, decisionJson,
            OptionalString(root, "fillAssumptionsJson"), OptionalDecimal(root, "remainingQuantity"),
            OptionalString(root, "exitReason"), OptionalStrings(root, "diagnostics"),
            OptionalDecimal(root, "totalFees"), OptionalDecimal(root, "totalSlippage"));
    }

    private static string? ExtractObject(string? json, string property)
    {
        if (json is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Serialize(value)
            : null;
    }

    private static string? OptionalString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value)
            ? value.ValueKind == JsonValueKind.Null ? null : value.GetString()
            : null;

    private static decimal? OptionalDecimal(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value)
            ? value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal()
            : null;

    private static string[] OptionalStrings(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"The persisted paper result property '{property}' is invalid.");
        }

        return value.EnumerateArray().Select(static item => item.GetString()
                ?? throw new JsonException("A persisted paper diagnostic is null."))
            .ToArray();
    }

    private static LocalPaperLedgerSnapshot? FromWire(LedgerWire? wire)
    {
        if (wire is null)
        {
            return null;
        }

        if (wire.Positions is null)
        {
            throw new InvalidDataException("Local-paper ledger positions are missing.");
        }

        return new LocalPaperLedgerSnapshot(wire.StreamId, wire.Revision, wire.LastSequence,
            wire.LastEventId, wire.InitialEquity, wire.Currency, wire.Equity, wire.RealizedPnl,
            wire.TotalFees, wire.TotalSlippage, wire.Positions.Select(static position =>
                new LocalPaperPositionSnapshot(position.InstrumentId, position.Direction,
                    position.Quantity, position.AverageEntryPrice, position.RealizedPnl)),
            wire.ReservedRisk, wire.ReservedNotional, wire.ReservedPositionSlots,
            wire.RecoveryBlocked, wire.RecoveryBlockReason);
    }

    private static LedgerWire? ToWire(LocalPaperLedgerSnapshot? ledger) => ledger is null
        ? null
        : new LedgerWire(ledger.StreamId, ledger.Revision, ledger.LastSequence,
            ledger.LastEventId, ledger.InitialEquity, ledger.Currency, ledger.Equity,
            ledger.RealizedPnl, ledger.TotalFees, ledger.TotalSlippage,
            ledger.Positions.Select(static position => new PositionWire(position.InstrumentId,
                position.Direction, position.Quantity, position.AverageEntryPrice,
                position.RealizedPnl)).ToArray(), ledger.ReservedRisk, ledger.ReservedNotional,
            ledger.ReservedPositionSlots, ledger.RecoveryBlocked, ledger.RecoveryBlockReason);

    private static ReadOnlyCollection<LocalPaperEvidenceObservation> SnapshotObservations(
        IEnumerable<LocalPaperEvidenceObservation> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        LocalPaperEvidenceObservation[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null)
            || snapshot.Select(static value => value.Sequence).Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException("Local-paper observation sequences must be unique.",
                nameof(values));
        }

        return new ReadOnlyCollection<LocalPaperEvidenceObservation>(snapshot
            .OrderBy(static value => value.Sequence)
            .ThenBy(static value => value.EventId, StringComparer.Ordinal).ToArray());
    }

    private static ReadOnlyCollection<LocalPaperEvidenceFill> SnapshotFills(
        IEnumerable<LocalPaperEvidenceFill> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        LocalPaperEvidenceFill[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("Local-paper fills cannot contain null values.",
                nameof(values));
        }

        return new ReadOnlyCollection<LocalPaperEvidenceFill>(snapshot
            .OrderBy(static value => value.StableSequence)
            .ThenBy(static value => value.FilledAtTicks)
            .ThenBy(static value => value.FillId, StringComparer.Ordinal).ToArray());
    }

    private static ReadOnlyCollection<string> SnapshotText(IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return new ReadOnlyCollection<string>(NormalizeText(values));
    }

    private static string[] NormalizeText(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string[] snapshot = values.ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace)
            || snapshot.Any(static value => value.Contains('\r') || value.Contains('\n')))
        {
            throw new ArgumentException("Local-paper evidence text must be single-line.",
                nameof(values));
        }

        return snapshot.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static void RequireSingleLine(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Local-paper evidence text must be single-line.",
                parameterName);
        }
    }

    private static string Hash(string content) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private sealed record ResultProjection(string? RiskJson, string? PlanJson,
        string? DecisionJson, string? FillAssumptionsJson, decimal? RemainingQuantity,
        string? ExitReason, IReadOnlyList<string> Diagnostics, decimal? TotalFees,
        decimal? TotalSlippage)
    {
        public static ResultProjection Empty { get; } = new(null, null, null, null, null,
            null, [], null, null);
    }

    private sealed record PositionWire(string InstrumentId, Direction Direction, decimal Quantity,
        decimal AverageEntryPrice, decimal RealizedPnl);

    private sealed record LedgerWire(string StreamId, long Revision, long? LastSequence,
        string? LastEventId, decimal InitialEquity, string Currency, decimal Equity,
        decimal RealizedPnl, decimal TotalFees, decimal TotalSlippage,
        IReadOnlyList<PositionWire>? Positions, decimal ReservedRisk, decimal ReservedNotional,
        int ReservedPositionSlots, bool RecoveryBlocked, string? RecoveryBlockReason);

    private sealed record EvidenceBody(string FormatVersion, string StreamId,
        string SourceIdentity, string ConfigurationIdentity,
        IReadOnlyList<string> ModelIdentities, IReadOnlyList<string> PlanIdentities,
        IReadOnlyList<LocalPaperEvidenceObservation> Observations,
        IReadOnlyList<LocalPaperEvidenceFill> Fills, LedgerWire? Ledger,
        IReadOnlyList<string> Provenance, IReadOnlyList<string> Exclusions,
        IReadOnlyList<string> Blockers, IReadOnlyList<string> Unknowns,
        long RealOrderCount, bool HasExecutionAuthority);

    private sealed record EvidenceWire(string FormatVersion, string? EvidenceIdentity,
        string? StreamId, string? SourceIdentity, string? ConfigurationIdentity,
        IReadOnlyList<string>? ModelIdentities, IReadOnlyList<string>? PlanIdentities,
        IReadOnlyList<LocalPaperEvidenceObservation>? Observations,
        IReadOnlyList<LocalPaperEvidenceFill>? Fills, LedgerWire? Ledger,
        IReadOnlyList<string>? Provenance, IReadOnlyList<string>? Exclusions,
        IReadOnlyList<string>? Blockers, IReadOnlyList<string>? Unknowns,
        long RealOrderCount, bool HasExecutionAuthority);
}

/// <summary>Factory retained as the explicit audit-builder surface.</summary>
public static class LocalPaperEvidenceBuilder
{
    public static LocalPaperEvidence Capture(LocalPaperStore store, string streamId,
        string sourceIdentity, IEnumerable<string>? provenance = null) =>
        LocalPaperEvidence.Capture(store, streamId, sourceIdentity, provenance);

    public static LocalPaperEvidence Build(LocalPaperStore store, string streamId,
        string sourceIdentity, IEnumerable<string>? provenance = null) =>
        LocalPaperEvidence.Build(store, streamId, sourceIdentity, provenance);
}
