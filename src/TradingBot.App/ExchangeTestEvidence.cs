using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

public enum ExchangeTestEvidenceProvenance
{
    SyntheticFixture,
    VerifiedTestEnvironment,
    Unknown
}

/// <summary>One durable exchange-test lifecycle row in the redacted evidence.</summary>
public sealed record ExchangeTestEvidenceLifecycle(string IntentId, string ClientOrderId,
    string State, decimal IntentQuantity, decimal FilledQuantity, int DispatchAttempt,
    string? ExchangeOrderId, bool RequiresReconciliation,
    long ExpectedReconciliationRevision, IReadOnlyList<string> ObservationReceiptIds);

/// <summary>One normalized fill with all causal fields retained.</summary>
public sealed record ExchangeTestEvidenceFill(string FillId, string IntentId,
    string InstrumentId, decimal Quantity, decimal Price, DateTimeOffset EventTime,
    DateTimeOffset ReceiveTime, DateTimeOffset AvailabilityTime, long StableSequence,
    DateTimeOffset? ExchangeTime, decimal? FeeAmount, string? FeeCurrency);

/// <summary>One normalized private-stream event; raw frames are deliberately absent.</summary>
public sealed record ExchangeTestEvidenceEvent(string Identity, string IntentId,
    string EventType, long StableSequence, DateTimeOffset EventTime,
    DateTimeOffset ReceiveTime, DateTimeOffset AvailabilityTime,
    string? ExchangeOrderId, decimal? FillQuantity, string Lifecycle,
    bool RequiresReconciliation, string Fingerprint);

/// <summary>One reservation identity and its release state.</summary>
public sealed record ExchangeTestEvidenceReservation(string ReservationId,
    string PortfolioId, string IntentId, decimal RiskAmount, string RiskCurrency,
    decimal NotionalExposureAmount, string NotionalExposureCurrency, int PositionSlots,
    bool Released);

/// <summary>One checkpoint projection retained for reconciliation evidence.</summary>
public sealed record ExchangeTestEvidenceCheckpoint(string PortfolioId, long Revision,
    string Status, IReadOnlyList<string> OpenIntentIds, IReadOnlyList<string> OpenPositionIds,
    IReadOnlyList<string> UnknownMarkers, IReadOnlyList<string> ManualPositionIds);

/// <summary>Explicit restart/reconciliation/idempotency claims in the report.</summary>
public sealed record ExchangeTestEvidenceRecovery(bool RestartVerified,
    bool ReconciliationEvidencePresent, bool IdempotencyVerified,
    IReadOnlyList<string> ReconciledIntentIds, IReadOnlyList<string> ReopenedIdentities);

/// <summary>
/// Immutable, redacted, content-addressed evidence for one exchange-test run.
/// It is read-only and never grants real-money execution authority.
/// </summary>
public sealed class ExchangeTestEvidence
{
    public const string CurrentFormatVersion = "exchange-test-evidence-v1";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly string[] DefaultExclusions =
    [
        "local-paper-evidence",
        "real-money-execution",
        "production-endpoints",
        "production-credentials",
        "activation-path",
        "raw-private-frames",
        "raw-credential-bearing-payloads",
        "listen-keys",
        "signatures"
    ];

    private ExchangeTestEvidence(string formatVersion, string identity, string runId,
        string configurationId, string environmentId, ExchangeTestEvidenceProvenance provenance,
        IEnumerable<string> provenanceDetails, IEnumerable<ExchangeTestEvidenceLifecycle> lifecycles,
        IEnumerable<ExchangeTestEvidenceEvent> events,
        IEnumerable<ExchangeTestEvidenceFill> fills,
        IEnumerable<ExchangeTestEvidenceReservation> reservations,
        IEnumerable<ExchangeTestEvidenceCheckpoint> checkpoints,
        ExchangeTestEvidenceRecovery recovery, IEnumerable<string> blockers,
        IEnumerable<string> unknowns, IEnumerable<string> exclusions, bool redactionVerified,
        string canonicalJson)
    {
        if (!StringComparer.Ordinal.Equals(formatVersion, CurrentFormatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        RequireIdentity(identity, nameof(identity));
        RequireIdentity(runId, nameof(runId));
        RequireIdentity(configurationId, nameof(configurationId));
        RequireIdentity(environmentId, nameof(environmentId));
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalJson);

        FormatVersion = formatVersion;
        Identity = identity;
        RunId = runId;
        ConfigurationId = configurationId;
        EnvironmentId = environmentId;
        Provenance = provenance;
        ProvenanceDetails = SnapshotText(provenanceDetails, nameof(provenanceDetails));
        Lifecycles = Snapshot(lifecycles, nameof(lifecycles));
        Events = Snapshot(events, nameof(events));
        Fills = Snapshot(fills, nameof(fills));
        Reservations = Snapshot(reservations, nameof(reservations));
        Checkpoints = Snapshot(checkpoints, nameof(checkpoints));
        Recovery = recovery;
        Blockers = SnapshotText(blockers, nameof(blockers));
        Unknowns = SnapshotText(unknowns, nameof(unknowns));
        Exclusions = SnapshotText(exclusions, nameof(exclusions));
        RedactionVerified = redactionVerified;
        CanonicalJson = canonicalJson;
    }

    public string FormatVersion { get; }
    public string Identity { get; }
    public string EvidenceIdentity => Identity;
    public string RunId { get; }
    public string ConfigurationId { get; }
    public string EnvironmentId { get; }
    public ExchangeTestEvidenceProvenance Provenance { get; }
    public IReadOnlyList<string> ProvenanceDetails { get; }
    public IReadOnlyList<ExchangeTestEvidenceLifecycle> Lifecycles { get; }
    public IReadOnlyList<ExchangeTestEvidenceEvent> Events { get; }
    public IReadOnlyList<ExchangeTestEvidenceFill> Fills { get; }
    public IReadOnlyList<ExchangeTestEvidenceReservation> Reservations { get; }
    public IReadOnlyList<ExchangeTestEvidenceCheckpoint> Checkpoints { get; }
    public ExchangeTestEvidenceRecovery Recovery { get; }
    public IReadOnlyList<string> Blockers { get; }
    public IReadOnlyList<string> Unknowns { get; }
    public IReadOnlyList<string> Exclusions { get; }
    public bool RedactionVerified { get; }
    public long RealOrderCount => FormatVersion.Length == 0 ? 1 : 0;
    public bool HasExecutionAuthority => IsSynthetic && false;
    public bool ActivationAvailable => IsSynthetic && false;
    public bool IsSynthetic => Provenance == ExchangeTestEvidenceProvenance.SyntheticFixture;
    public bool IsComplete => Blockers.Count == 0 && Unknowns.Count == 0;
    public string CanonicalJson { get; }
    public string ContentHash => Hash(CanonicalJson);
    public string CanonicalSha256 => ContentHash;

    /// <summary>Builds evidence from one consistent persisted exchange-test snapshot.</summary>
    public static ExchangeTestEvidence Capture(SqliteExchangeTestStore exchangeStore,
        SqlitePortfolioOperationalStore portfolioStore, PortfolioId portfolioId,
        IEnumerable<string>? intentIds = null,
        ExchangeTestEvidenceCaptureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(exchangeStore);
        ArgumentNullException.ThrowIfNull(portfolioStore);
        ArgumentNullException.ThrowIfNull(portfolioId);
        ExchangeTestPersistedSnapshot snapshot =
            exchangeStore.ReadPersistedSnapshot(portfolioStore, portfolioId, intentIds);
        return Build(snapshot, options);
    }

    public static ExchangeTestEvidence Build(ExchangeTestPersistedSnapshot snapshot,
        ExchangeTestEvidenceCaptureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new ExchangeTestEvidenceCaptureOptions();
        ExchangeTestEvidenceProvenance provenance = options.Provenance;
        string[] provenanceDetails = NormalizeProvenanceDetails(options.ProvenanceDetails);
        if (snapshot.PrivateEvents.Any(receipt =>
                !StringComparer.Ordinal.Equals(receipt.RunId, snapshot.RunMetadata.RunId)))
        {
            throw new InvalidDataException(
                "The persisted exchange-test event run identity is inconsistent.");
        }

        if (provenance == ExchangeTestEvidenceProvenance.SyntheticFixture
            && !provenanceDetails.Contains("synthetic-fixture", StringComparer.Ordinal))
        {
            provenanceDetails = [.. provenanceDetails, "synthetic-fixture"];
        }

        ExchangeTestEvidenceLifecycle[] lifecycles = snapshot.Lifecycles
            .OrderBy(static value => value.IntentId, StringComparer.Ordinal)
            .Select(static value => new ExchangeTestEvidenceLifecycle(value.IntentId,
                value.ClientOrderId, value.State.ToString(), value.IntentQuantity,
                value.FilledQuantity, value.DispatchAttempt, value.ExchangeOrderId,
                value.RequiresReconciliation, value.ExpectedReconciliationRevision,
                value.Receipts.Select(static receipt => receipt.ReceiptId)
                    .Order(StringComparer.Ordinal).ToArray()))
            .ToArray();
        ExchangeTestEvidenceEvent[] events = snapshot.PrivateEvents
            .OrderBy(static value => value.StableSequence)
            .ThenBy(static value => value.Identity, StringComparer.Ordinal)
            .Select(static value => new ExchangeTestEvidenceEvent(value.Identity,
                value.IntentId, value.EventType, value.StableSequence, value.EventTime,
                value.ReceiveTime, value.AvailabilityTime, value.ExchangeOrderId,
                value.FillQuantity, value.Lifecycle, value.RequiresReconciliation,
                value.Fingerprint))
            .ToArray();
        ExchangeTestEvidenceFill[] fills = snapshot.Fills
            .OrderBy(static value => value.FillId, StringComparer.Ordinal)
            .Select(ToEvidenceFill)
            .ToArray();
        ExchangeTestEvidenceReservation[] reservations = snapshot.Reservations
            .OrderBy(static value => value.ReservationId, StringComparer.Ordinal)
            .Select(static value => new ExchangeTestEvidenceReservation(value.ReservationId,
                value.PortfolioId, value.IntentId, value.Reservation.Risk.Amount,
                value.Reservation.Risk.Currency, value.Reservation.NotionalExposure.Amount,
                value.Reservation.NotionalExposure.Currency, value.Reservation.PositionSlots,
                value.Released))
            .ToArray();
        ExchangeTestEvidenceCheckpoint[] checkpoints = snapshot.Checkpoints
            .OrderBy(static value => value.Revision)
            .Select(static value => new ExchangeTestEvidenceCheckpoint(value.PortfolioId.Value,
                value.Revision, value.Status.ToString(), value.OpenIntentIds.ToArray(),
                value.OpenPositionIds.ToArray(), value.UnknownMarkers.ToArray(),
                value.ManualPositionIds.ToArray()))
            .ToArray();

        List<string> blockers = [];
        List<string> unknowns = [];
        if (lifecycles.Length == 0)
        {
            blockers.Add("lifecycle-unavailable");
        }

        foreach (ExchangeTestEvidenceLifecycle lifecycle in lifecycles)
        {
            if (lifecycle.RequiresReconciliation)
            {
                blockers.Add($"reconciliation-required:{lifecycle.IntentId}");
            }
        }

        if (checkpoints.Length == 0)
        {
            unknowns.Add("reconciliation-checkpoint-unavailable");
        }
        else if (checkpoints.Any(static checkpoint => checkpoint.Status !=
                     nameof(ReconciliationStatus.Acknowledged)))
        {
            blockers.Add("reconciliation-unacknowledged");
        }

        if (snapshot.Positions.Any(static position => position.Origin is PositionOrigin.Manual
                or PositionOrigin.Unknown))
        {
            blockers.Add("manual-or-unknown-position");
        }

        if (!options.RestartVerified)
        {
            unknowns.Add("restart-not-verified");
        }

        if (!options.IdempotencyVerified)
        {
            unknowns.Add("idempotency-not-verified");
        }

        if (provenance == ExchangeTestEvidenceProvenance.Unknown)
        {
            unknowns.Add("provenance-unknown");
        }

        if (provenance == ExchangeTestEvidenceProvenance.SyntheticFixture)
        {
            unknowns.Add("external-network-not-verified");
        }

        string[] reconciled = lifecycles
            .Where(static lifecycle => lifecycle.RequiresReconciliation)
            .Select(static lifecycle => lifecycle.IntentId)
            .Order(StringComparer.Ordinal).ToArray();
        string[] reopened =
        [
            .. events.Select(static item => item.Identity),
            .. fills.Select(static item => item.FillId),
            .. lifecycles.Select(static item => item.IntentId)
        ];
        ExchangeTestEvidenceRecovery recovery = new(options.RestartVerified,
            checkpoints.Length != 0, options.IdempotencyVerified, reconciled,
            reopened.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        return Create(snapshot.RunMetadata.RunId, snapshot.RunMetadata.ConfigurationId,
            snapshot.RunMetadata.EnvironmentId, provenance, provenanceDetails, lifecycles,
            events, fills, reservations, checkpoints, recovery, blockers, unknowns,
            DefaultExclusions, redactionVerified: true);
    }

    public static ExchangeTestEvidence Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        EvidenceWire wire;
        try
        {
            wire = JsonSerializer.Deserialize<EvidenceWire>(json, JsonOptions)
                ?? throw new JsonException("Exchange-test evidence is empty.");
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or NotSupportedException)
        {
            throw new JsonException("Exchange-test evidence is invalid.", exception);
        }

        if (!StringComparer.Ordinal.Equals(wire.FormatVersion, CurrentFormatVersion)
            || string.IsNullOrWhiteSpace(wire.Identity)
            || string.IsNullOrWhiteSpace(wire.RunId)
            || string.IsNullOrWhiteSpace(wire.ConfigurationId)
            || !StringComparer.Ordinal.Equals(wire.EnvironmentId, "binance-usdm-test")
            || wire.ProvenanceDetails is null || wire.Lifecycles is null
            || wire.Events is null || wire.Fills is null || wire.Reservations is null
            || wire.Checkpoints is null || wire.Recovery is null
            || wire.Blockers is null || wire.Unknowns is null || wire.Exclusions is null)
        {
            throw new JsonException("Exchange-test evidence metadata is incomplete.");
        }

        ExchangeTestEvidence evidence = Create(wire.RunId, wire.ConfigurationId,
            wire.EnvironmentId, wire.Provenance, wire.ProvenanceDetails, wire.Lifecycles,
            wire.Events, wire.Fills, wire.Reservations, wire.Checkpoints, wire.Recovery,
            wire.Blockers, wire.Unknowns, wire.Exclusions, wire.RedactionVerified);
        if (!StringComparer.Ordinal.Equals(wire.Identity, evidence.Identity)
            || !StringComparer.Ordinal.Equals(json, evidence.CanonicalJson))
        {
            throw new JsonException("Exchange-test evidence identity or canonical JSON is invalid.");
        }

        return evidence;
    }

    /// <summary>Projects only the ExchangeTest promotion category.</summary>
    public PromotionEvidenceSnapshot ToPromotionEvidenceSnapshot()
    {
        string[] blockers = Blockers.Append("execution-authority-not-granted")
            .Append("real-money-activation-not-authorized")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return PromotionEvidenceSnapshot.FromArtifact(
            PromotionEvidenceCategory.ExchangeTest, Identity, CanonicalJson,
            causalFields:
            [
                new PromotionEvidenceCausalField("runId", RunId),
                new PromotionEvidenceCausalField("configurationId", ConfigurationId),
                new PromotionEvidenceCausalField("environmentId", EnvironmentId),
                new PromotionEvidenceCausalField("exchangeTestIdentity", Identity)
            ],
            provenance: ProvenanceDetails.Append(Provenance.ToString()),
            exclusions: Exclusions, unknowns: Unknowns, blockers: blockers,
            referencedIdentities: [RunId, ConfigurationId, EnvironmentId]);
    }

    private static ExchangeTestEvidenceFill ToEvidenceFill(Fill fill) =>
        new(fill.FillId, fill.IntentId, fill.InstrumentId.Value, fill.Quantity.Value,
            fill.Price.Value, fill.EventTime.Value, fill.ReceiveTime.Value,
            fill.AvailabilityTime.Value, fill.StableSequence,
            fill.ExchangeTime?.Value, fill.Fee?.Amount, fill.Fee?.Currency);

    private static ExchangeTestEvidence Create(string runId, string configurationId,
        string environmentId, ExchangeTestEvidenceProvenance provenance,
        IEnumerable<string> provenanceDetails, IEnumerable<ExchangeTestEvidenceLifecycle> lifecycles,
        IEnumerable<ExchangeTestEvidenceEvent> events, IEnumerable<ExchangeTestEvidenceFill> fills,
        IEnumerable<ExchangeTestEvidenceReservation> reservations,
        IEnumerable<ExchangeTestEvidenceCheckpoint> checkpoints,
        ExchangeTestEvidenceRecovery recovery, IEnumerable<string> blockers,
        IEnumerable<string> unknowns, IEnumerable<string> exclusions, bool redactionVerified)
    {
        string[] normalizedProvenance = NormalizeProvenanceDetails(provenanceDetails);
        ExchangeTestEvidenceLifecycle[] normalizedLifecycles = Snapshot(lifecycles,
            nameof(lifecycles)).OrderBy(static value => value.IntentId, StringComparer.Ordinal)
            .ToArray();
        ExchangeTestEvidenceEvent[] normalizedEvents = Snapshot(events, nameof(events))
            .OrderBy(static value => value.StableSequence)
            .ThenBy(static value => value.Identity, StringComparer.Ordinal).ToArray();
        ExchangeTestEvidenceFill[] normalizedFills = Snapshot(fills, nameof(fills))
            .OrderBy(static value => value.FillId, StringComparer.Ordinal).ToArray();
        ExchangeTestEvidenceReservation[] normalizedReservations = Snapshot(reservations,
            nameof(reservations)).OrderBy(static value => value.ReservationId,
                StringComparer.Ordinal).ToArray();
        ExchangeTestEvidenceCheckpoint[] normalizedCheckpoints = Snapshot(checkpoints,
            nameof(checkpoints)).OrderBy(static value => value.Revision).ToArray();
        string[] normalizedBlockers = NormalizeText(blockers);
        string[] normalizedUnknowns = NormalizeText(unknowns);
        string[] normalizedExclusions = NormalizeText(exclusions);
        EvidenceBody body = new(CurrentFormatVersion, runId, configurationId, environmentId,
            provenance, normalizedProvenance, normalizedLifecycles, normalizedEvents,
            normalizedFills, normalizedReservations, normalizedCheckpoints, recovery,
            normalizedBlockers, normalizedUnknowns, normalizedExclusions, redactionVerified);
        string identity = Hash(JsonSerializer.Serialize(body, JsonOptions));
        string canonical = JsonSerializer.Serialize(new EvidenceWire(CurrentFormatVersion,
            identity, runId, configurationId, environmentId, provenance, normalizedProvenance,
            normalizedLifecycles, normalizedEvents, normalizedFills, normalizedReservations,
            normalizedCheckpoints, recovery, normalizedBlockers, normalizedUnknowns,
            normalizedExclusions, redactionVerified), JsonOptions);
        return new ExchangeTestEvidence(CurrentFormatVersion, identity, runId,
            configurationId, environmentId, provenance, normalizedProvenance,
            normalizedLifecycles, normalizedEvents, normalizedFills, normalizedReservations,
            normalizedCheckpoints, recovery, normalizedBlockers, normalizedUnknowns,
            normalizedExclusions, redactionVerified, canonical);
    }

    private static ReadOnlyCollection<T> Snapshot<T>(IEnumerable<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        T[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("Evidence rows cannot be null.", parameterName);
        }

        return new ReadOnlyCollection<T>(snapshot);
    }

    private static ReadOnlyCollection<string> SnapshotText(IEnumerable<string> values,
        string parameterName) => new(NormalizeText(values, parameterName));

    private static string[] NormalizeText(IEnumerable<string>? values,
        string parameterName = "values")
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        string[] snapshot = values.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToArray();
        if (snapshot.Any(static value => value.Contains('\r') || value.Contains('\n')))
        {
            throw new ArgumentException("Evidence text cannot contain line breaks.", parameterName);
        }

        return snapshot.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] NormalizeProvenanceDetails(IEnumerable<string>? values)
    {
        string[] normalized = NormalizeText(values, nameof(values));
        if (normalized.Any(ContainsCredentialBearingMetadata))
        {
            throw new ArgumentException(
                "Evidence provenance cannot contain credential-bearing metadata.",
                nameof(values));
        }

        return normalized;
    }

    private static bool ContainsCredentialBearingMetadata(string value)
    {
        string normalized = value.ToLowerInvariant();
        return normalized.Contains("apikey=", StringComparison.Ordinal)
            || normalized.Contains("api-key=", StringComparison.Ordinal)
            || normalized.Contains("api_key=", StringComparison.Ordinal)
            || normalized.Contains("apisecret=", StringComparison.Ordinal)
            || normalized.Contains("api-secret=", StringComparison.Ordinal)
            || normalized.Contains("api_secret=", StringComparison.Ordinal)
            || normalized.Contains("credential=", StringComparison.Ordinal)
            || normalized.Contains("credential:", StringComparison.Ordinal)
            || normalized.Contains("secret=", StringComparison.Ordinal)
            || normalized.Contains("secret:", StringComparison.Ordinal)
            || normalized.Contains("token=", StringComparison.Ordinal)
            || normalized.Contains("token:", StringComparison.Ordinal)
            || normalized.Contains("bearer ", StringComparison.Ordinal)
            || normalized.Contains("listenkey=", StringComparison.Ordinal)
            || normalized.Contains("listen-key=", StringComparison.Ordinal)
            || normalized.Contains("signature=", StringComparison.Ordinal)
            || normalized.Contains("signature:", StringComparison.Ordinal)
            || normalized.Contains("privatekey=", StringComparison.Ordinal)
            || normalized.Contains("private-key=", StringComparison.Ordinal);
    }

    private static void RequireIdentity(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 256 || value.Any(char.IsControl)
            || value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Evidence identities are not valid.", parameterName);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private sealed record EvidenceBody(string FormatVersion, string RunId,
        string ConfigurationId, string EnvironmentId, ExchangeTestEvidenceProvenance Provenance,
        IReadOnlyList<string> ProvenanceDetails,
        IReadOnlyList<ExchangeTestEvidenceLifecycle> Lifecycles,
        IReadOnlyList<ExchangeTestEvidenceEvent> Events,
        IReadOnlyList<ExchangeTestEvidenceFill> Fills,
        IReadOnlyList<ExchangeTestEvidenceReservation> Reservations,
        IReadOnlyList<ExchangeTestEvidenceCheckpoint> Checkpoints,
        ExchangeTestEvidenceRecovery Recovery, IReadOnlyList<string> Blockers,
        IReadOnlyList<string> Unknowns, IReadOnlyList<string> Exclusions,
        bool RedactionVerified);

    private sealed record EvidenceWire(string FormatVersion, string? Identity,
        string RunId, string ConfigurationId, string EnvironmentId,
        ExchangeTestEvidenceProvenance Provenance, IReadOnlyList<string>? ProvenanceDetails,
        IReadOnlyList<ExchangeTestEvidenceLifecycle>? Lifecycles,
        IReadOnlyList<ExchangeTestEvidenceEvent>? Events,
        IReadOnlyList<ExchangeTestEvidenceFill>? Fills,
        IReadOnlyList<ExchangeTestEvidenceReservation>? Reservations,
        IReadOnlyList<ExchangeTestEvidenceCheckpoint>? Checkpoints,
        ExchangeTestEvidenceRecovery? Recovery, IReadOnlyList<string>? Blockers,
        IReadOnlyList<string>? Unknowns, IReadOnlyList<string>? Exclusions,
        bool RedactionVerified);
}

public sealed record ExchangeTestEvidenceCaptureOptions
{
    public ExchangeTestEvidenceProvenance Provenance { get; init; } =
        ExchangeTestEvidenceProvenance.SyntheticFixture;

    public IReadOnlyList<string> ProvenanceDetails { get; init; } =
        Array.Empty<string>();

    public bool RestartVerified { get; init; }
    public bool IdempotencyVerified { get; init; }
}

public static class ExchangeTestEvidenceExporter
{
    public static bool Export(ExchangeTestEvidence evidence, string path)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string target = Path.GetFullPath(path);
        if (IsDatabaseLikeDestination(target))
        {
            throw new InvalidOperationException(
                "An exchange-test evidence export cannot overwrite a SQLite database or sidecar.");
        }

        byte[] content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(evidence.CanonicalJson);
        if (File.Exists(target))
        {
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(content))
            {
                return false;
            }

            throw new InvalidOperationException("Export destination contains different evidence.");
        }

        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, target, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(target))
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(content))
                {
                    return false;
                }

                throw new InvalidOperationException(
                    "A concurrent export contains different evidence.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
                // Preserve the original export result or failure.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original export result or failure.
            }
        }
    }

    private static bool IsDatabaseLikeDestination(string target)
    {
        string lower = target.ToLowerInvariant();
        if (lower.EndsWith("-wal", StringComparison.Ordinal)
            || lower.EndsWith("-shm", StringComparison.Ordinal)
            || Path.GetExtension(lower) is ".db" or ".db3" or ".sqlite" or ".sqlite3"
                or ".wal" or ".shm")
        {
            return true;
        }

        if (!File.Exists(target))
        {
            return false;
        }

        using FileStream stream = new(target, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 16, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[16];
        int read = stream.Read(header);
        return read == 16 && header.SequenceEqual("SQLite format 3\0"u8);
    }
}
