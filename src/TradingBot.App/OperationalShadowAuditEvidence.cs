using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>All durable health fields retained in one auditable shadow row.</summary>
public sealed record OperationalShadowAuditHealth(
    string RunId,
    long Sequence,
    DateTimeOffset CheckedAt,
    string CanonicalJson,
    string? CausalCheckpointLink,
    OperationalShadowHealthStatus Status,
    IReadOnlyList<OperationalShadowFeedHealth> Feeds,
    bool DataStale,
    bool SequenceGap,
    bool Reconnecting,
    bool RecoveryComplete,
    bool PersistenceHealthy,
    bool KillSwitchKnown,
    bool KillSwitchEnabled,
    bool GateOpen,
    bool CoverageKnown,
    bool CoverageApplicable,
    int ExpectedFeedCount,
    int CoveredFeedCount,
    double? CoverageRatio,
    IReadOnlyList<OperationalShadowMissingStreamReason> MissingStreamReasons,
    IReadOnlyList<string> GateReasons,
    OperationalShadowReconciliationStatus ReconciliationStatus,
    long RealOrderCount);

/// <summary>One journal row referenced by a shadow health snapshot.</summary>
public sealed record OperationalShadowAuditEntry(
    string StreamId,
    string EventId,
    long StableSequence,
    DateTimeOffset EventTime,
    DateTimeOffset? ExchangeTime,
    DateTimeOffset ReceiveTime,
    DateTimeOffset AvailabilityTime,
    string CausalEventJson,
    string DerivedStateJson,
    string OutcomeJson,
    string CheckpointJson,
    long RealOrderCount);

/// <summary>
/// Immutable, content-addressed evidence for one operational shadow run.
/// The identity is SHA-256 over the canonical body without the identity field.
/// </summary>
public sealed class OperationalShadowAuditEvidence
{
    public const string CurrentFormatVersion = "operational-shadow-audit-v1";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public string RunId { get; }
    public string Identity { get; }
    public string CanonicalJson { get; }
    public string ContentHash => Hash(CanonicalJson);
    public string CanonicalSha256 => ContentHash;
    public IReadOnlyList<OperationalShadowAuditHealth> HealthSnapshots { get; }
    public IReadOnlyList<OperationalShadowAuditEntry> JournalEntries { get; }
    public IReadOnlyList<string> Blockers { get; }
    public long RealOrderCount => HealthSnapshots.Sum(static item => item.RealOrderCount)
        + JournalEntries.Sum(static item => item.RealOrderCount);

    private OperationalShadowAuditEvidence(string runId, string identity, string canonical,
        OperationalShadowAuditHealth[] health, OperationalShadowAuditEntry[] entries,
        string[] blockers)
    {
        RunId = runId;
        Identity = identity;
        CanonicalJson = canonical;
        HealthSnapshots = Array.AsReadOnly(health);
        JournalEntries = Array.AsReadOnly(entries);
        Blockers = Array.AsReadOnly(blockers);
    }

    public static OperationalShadowAuditEvidence Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        AuditWire wire;
        try
        {
            wire = JsonSerializer.Deserialize<AuditWire>(json, JsonOptions)
                ?? throw new JsonException("Shadow audit evidence is empty.");
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is NotSupportedException
            or InvalidOperationException)
        {
            throw new JsonException("Shadow audit evidence is invalid.", exception);
        }

        if (!StringComparer.Ordinal.Equals(wire.Format, CurrentFormatVersion)
            || string.IsNullOrWhiteSpace(wire.Identity)
            || string.IsNullOrWhiteSpace(wire.RunId)
            || wire.HealthSnapshots is null
            || wire.JournalEntries is null
            || wire.Blockers is null)
        {
            throw new JsonException("Shadow audit evidence metadata is invalid.");
        }

        OperationalShadowAuditHealth[] health = wire.HealthSnapshots.ToArray();
        OperationalShadowAuditEntry[] entries = wire.JournalEntries.ToArray();
        string[] blockers = NormalizeBlockers(wire.Blockers);
        ValidateRows(wire.RunId, health, entries);

        CanonicalDocument expected = BuildCanonical(wire.RunId, health, entries, blockers);
        if (!StringComparer.Ordinal.Equals(wire.Identity, expected.Identity)
            || !StringComparer.Ordinal.Equals(json, expected.Json))
        {
            throw new JsonException("Shadow audit identity or canonical JSON does not match.");
        }

        return new OperationalShadowAuditEvidence(wire.RunId, expected.Identity, expected.Json,
            NormalizeHealth(health), NormalizeEntries(entries), blockers);
    }

    public PromotionEvidenceSnapshot ToPromotionEvidenceSnapshot()
    {
        string[] blockers = Blockers
            .Append("execution-authority-not-granted")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return PromotionEvidenceSnapshot.FromArtifact(
            PromotionEvidenceCategory.OperationalShadow,
            Identity,
            CanonicalJson,
            causalFields:
            [
                new PromotionEvidenceCausalField("runId", RunId),
                new PromotionEvidenceCausalField("auditIdentity", Identity)
            ],
            provenance: ["operational-shadow-store", "read-only-single-transaction-audit"],
            exclusions: ["private-reconciliation-not-applicable-in-shadow", "real-orders"],
            blockers: blockers,
            referencedIdentities: [RunId]);
    }

    internal static OperationalShadowAuditEvidence Create(string runId,
        OperationalShadowAuditHealth[] health, OperationalShadowAuditEntry[] entries,
        string[] blockers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ValidateRows(runId, health, entries);
        string[] normalizedBlockers = NormalizeBlockers(blockers);
        CanonicalDocument canonical = BuildCanonical(runId, health, entries,
            normalizedBlockers);
        return new OperationalShadowAuditEvidence(runId, canonical.Identity, canonical.Json,
            NormalizeHealth(health), NormalizeEntries(entries), normalizedBlockers);
    }

    private static CanonicalDocument BuildCanonical(string runId,
        IReadOnlyList<OperationalShadowAuditHealth> health,
        IReadOnlyList<OperationalShadowAuditEntry> entries,
        IReadOnlyList<string> blockers)
    {
        OperationalShadowAuditHealth[] normalizedHealth = NormalizeHealth(health);
        OperationalShadowAuditEntry[] normalizedEntries = NormalizeEntries(entries);
        string[] normalizedBlockers = NormalizeBlockers(blockers);
        string body = JsonSerializer.Serialize(new AuditBody(CurrentFormatVersion, runId,
            normalizedHealth, normalizedEntries, normalizedBlockers), JsonOptions);
        string identity = Hash(body);
        string json = JsonSerializer.Serialize(new AuditWire(CurrentFormatVersion, identity,
            runId, normalizedHealth, normalizedEntries, normalizedBlockers), JsonOptions);
        return new CanonicalDocument(identity, json);
    }

    private static void ValidateRows(string runId,
        OperationalShadowAuditHealth[] health,
        OperationalShadowAuditEntry[] entries)
    {
        if (health.Length == 0)
        {
            throw new InvalidDataException("A shadow audit must contain health evidence.");
        }

        if (health.Any(item => !StringComparer.Ordinal.Equals(item.RunId, runId)
                || item.Sequence < 0
                || string.IsNullOrWhiteSpace(item.CanonicalJson)
                || item.RealOrderCount != 0
                || item.ReconciliationStatus
                    != OperationalShadowReconciliationStatus.NotApplicableInShadow)
            || health.Select(static item => item.Sequence).Distinct().Count() != health.Length
            || entries.Any(item => item.StableSequence < 0 || item.RealOrderCount != 0))
        {
            throw new InvalidDataException("Shadow audit evidence is inconsistent.");
        }
    }

    private static OperationalShadowAuditHealth[] NormalizeHealth(
        IEnumerable<OperationalShadowAuditHealth> values) => values
        .OrderBy(static item => item.Sequence)
        .ToArray();

    private static OperationalShadowAuditEntry[] NormalizeEntries(
        IEnumerable<OperationalShadowAuditEntry> values) => values
        .OrderBy(static item => item.StreamId, StringComparer.Ordinal)
        .ThenBy(static item => item.StableSequence)
        .ThenBy(static item => item.EventId, StringComparer.Ordinal)
        .ToArray();

    private static string[] NormalizeBlockers(IEnumerable<string> values) => values
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record CanonicalDocument(string Identity, string Json);

    private sealed record AuditBody(
        string Format,
        string RunId,
        IReadOnlyList<OperationalShadowAuditHealth> HealthSnapshots,
        IReadOnlyList<OperationalShadowAuditEntry> JournalEntries,
        IReadOnlyList<string> Blockers);

    private sealed record AuditWire(
        string Format,
        string Identity,
        string RunId,
        IReadOnlyList<OperationalShadowAuditHealth>? HealthSnapshots,
        IReadOnlyList<OperationalShadowAuditEntry>? JournalEntries,
        IReadOnlyList<string>? Blockers);
}

public static class OperationalShadowAuditEvidenceBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static OperationalShadowAuditEvidence Build(OperationalShadowAuditSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        OperationalShadowAuditHealth[] health = snapshot.HealthEvidence.Select(item =>
        {
            if (item.EvidenceVersion != OperationalShadowHealthEvidence.CurrentEvidenceVersion
                || !StringComparer.Ordinal.Equals(item.RunId, snapshot.RunId))
            {
                throw new InvalidDataException("Persisted health row metadata is unsupported.");
            }

            using JsonDocument json = ParseObject(item.HealthJson,
                "Persisted health evidence is not valid JSON.");
            JsonElement root = json.RootElement;
            JsonElement state = RequiredObject(root, "snapshot");
            if (root.GetProperty("healthSequence").GetInt64() != item.HealthSequence
                || !StringComparer.Ordinal.Equals(
                    NullableString(root, "causalCheckpointLink"), item.CausalCheckpointLink)
                || !StringComparer.Ordinal.Equals(root.GetProperty("runId").GetString(),
                    item.RunId)
                || !StringComparer.Ordinal.Equals(state.GetProperty("runId").GetString(),
                    item.RunId)
                || state.GetProperty("healthSequence").GetInt64() != item.HealthSequence
                || !StringComparer.Ordinal.Equals(
                    NullableString(state, "causalCheckpointLink"), item.CausalCheckpointLink))
            {
                throw new InvalidDataException("Persisted health row metadata does not match JSON.");
            }

            OperationalShadowHealthStatus status = RequiredEnum<OperationalShadowHealthStatus>(
                state, "status");
            OperationalShadowReconciliationStatus reconciliation =
                RequiredEnum<OperationalShadowReconciliationStatus>(state,
                    "reconciliationStatus");
            long realOrderCount = RequiredInt64(state, "realOrderCount");
            return new OperationalShadowAuditHealth(
                item.RunId,
                item.HealthSequence,
                item.CheckedAt.Value,
                item.HealthJson,
                item.CausalCheckpointLink,
                status,
                DeserializeList<OperationalShadowFeedHealth>(state, "feeds"),
                RequiredBoolean(state, "dataStale"),
                RequiredBoolean(state, "sequenceGap"),
                RequiredBoolean(state, "reconnecting"),
                RequiredBoolean(state, "recoveryComplete"),
                RequiredBoolean(state, "persistenceHealthy"),
                RequiredBoolean(state, "killSwitchKnown"),
                RequiredBoolean(state, "killSwitchEnabled"),
                RequiredBoolean(state, "canCreateHypotheticalEntry"),
                RequiredBoolean(state, "coverageKnown"),
                RequiredBoolean(state, "coverageApplicable"),
                RequiredInt32(state, "expectedFeedCount"),
                RequiredInt32(state, "coveredFeedCount"),
                NullableDouble(state, "coverageRatio"),
                DeserializeList<OperationalShadowMissingStreamReason>(state,
                    "missingStreamReasons"),
                DeserializeList<string>(state, "blockReasons"),
                reconciliation,
                realOrderCount);
        }).ToArray();

        OperationalShadowAuditEntry[] entries = snapshot.JournalEntries.Select(item =>
        {
            using JsonDocument outcome = ParseObject(item.OutcomeJson,
                "Persisted shadow outcome is not valid JSON.");
            long realOrderCount = RequiredInt64(outcome.RootElement, "realOrderCount");
            OperationalShadowAuditEntry audit = new(
                item.StreamId,
                item.EventId,
                item.StableSequence,
                item.EventTime.Value,
                item.ExchangeTime?.Value,
                item.ReceiveTime.Value,
                item.AvailabilityTime.Value,
                item.CausalEventJson,
                item.DerivedStateJson,
                item.OutcomeJson,
                item.CheckpointJson,
                realOrderCount);
            ValidateEntryMetadata(audit);
            return audit;
        }).ToArray();

        if (health.Any(static item => item.RealOrderCount != 0)
            || entries.Any(static item => item.RealOrderCount != 0))
        {
            throw new InvalidDataException("A shadow audit cannot contain real orders.");
        }

        List<string> blockers = [];
        long[] sequences = health.Select(static item => item.Sequence).Order().ToArray();
        if (sequences.Where((sequence, index) => sequence != index).Any())
        {
            blockers.Add("health-sequence-gap");
        }

        HashSet<string> links = entries.Select(CreateCheckpointLink)
            .ToHashSet(StringComparer.Ordinal);
        if (health.Any(item => item.CausalCheckpointLink is { } link
                && !links.Contains(link)))
        {
            blockers.Add("causal-checkpoint-unresolved");
        }

        if (health.Any(static item => !item.CoverageKnown
                || (item.CoverageApplicable
                    && item.CoveredFeedCount != item.ExpectedFeedCount)))
        {
            blockers.Add("data-quality-incomplete-or-unknown");
        }

        if (health.Any(static item => item.Status != OperationalShadowHealthStatus.Healthy
                || !item.GateOpen || !item.PersistenceHealthy))
        {
            blockers.Add("shadow-gate-blocked-or-degraded");
        }

        return OperationalShadowAuditEvidence.Create(snapshot.RunId, health, entries,
            [.. blockers]);
    }

    private static string CreateCheckpointLink(OperationalShadowAuditEntry entry) =>
        $"{entry.StreamId}/{entry.StableSequence}/{entry.EventId}";

    private static void ValidateEntryMetadata(OperationalShadowAuditEntry entry)
    {
        using JsonDocument causal = ParseObject(entry.CausalEventJson,
            "Persisted causal event is not valid JSON.");
        JsonElement root = causal.RootElement;
        if (!StringComparer.Ordinal.Equals(root.GetProperty("streamId").GetString(),
                entry.StreamId)
            || !StringComparer.Ordinal.Equals(root.GetProperty("eventId").GetString(),
                entry.EventId)
            || root.GetProperty("stableSequence").GetInt64() != entry.StableSequence
            || !SameUtc(RequiredDateTime(root, "eventTime"), entry.EventTime)
            || !SameUtc(RequiredDateTime(root, "receiveTime"), entry.ReceiveTime)
            || !SameUtc(RequiredDateTime(root, "availabilityTime"), entry.AvailabilityTime)
            || !SameNullableUtc(root, "exchangeTime", entry.ExchangeTime))
        {
            throw new InvalidDataException("Persisted causal event metadata is inconsistent.");
        }

        using JsonDocument state = ParseObject(entry.DerivedStateJson,
            "Persisted shadow state is not valid JSON.");
        using JsonDocument checkpoint = ParseObject(entry.CheckpointJson,
            "Persisted shadow checkpoint is not valid JSON.");
        JsonElement checkpointRoot = checkpoint.RootElement;
        if (!StringComparer.Ordinal.Equals(checkpointRoot.GetProperty("streamId").GetString(),
                entry.StreamId)
            || !StringComparer.Ordinal.Equals(checkpointRoot.GetProperty("eventId").GetString(),
                entry.EventId)
            || checkpointRoot.GetProperty("stableSequence").GetInt64() != entry.StableSequence)
        {
            throw new InvalidDataException("Persisted shadow checkpoint metadata is inconsistent.");
        }
    }

    private static DateTimeOffset RequiredDateTime(JsonElement parent, string propertyName) =>
        parent.GetProperty(propertyName).GetDateTimeOffset();

    private static bool SameNullableUtc(JsonElement parent, string propertyName,
        DateTimeOffset? expected)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return expected is null;
        }

        return expected is { } actual && SameUtc(value.GetDateTimeOffset(), actual);
    }

    private static bool SameUtc(DateTimeOffset left, DateTimeOffset right) =>
        left.UtcDateTime.Ticks == right.UtcDateTime.Ticks;

    private static JsonDocument ParseObject(string json, string message)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidDataException(message);
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(message, exception);
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Health property '{propertyName}' is not an object.");
        }

        return value;
    }

    private static T RequiredEnum<T>(JsonElement parent, string propertyName)
        where T : struct, Enum
    {
        JsonElement value = parent.GetProperty(propertyName);
        T parsed;
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()
                ?? throw new InvalidDataException($"Health property '{propertyName}' is null.");
            if (!Enum.TryParse(text, ignoreCase: false, out parsed))
            {
                throw new InvalidDataException($"Health property '{propertyName}' is unknown.");
            }
        }
        else if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int numeric))
        {
            parsed = (T)Enum.ToObject(typeof(T), numeric);
        }
        else
        {
            throw new InvalidDataException($"Health property '{propertyName}' is not an enum.");
        }

        return Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException($"Health property '{propertyName}' is unknown.");
    }

    private static bool RequiredBoolean(JsonElement parent, string propertyName) =>
        parent.GetProperty(propertyName).ValueKind is JsonValueKind.True
            or JsonValueKind.False
            ? parent.GetProperty(propertyName).GetBoolean()
            : throw new InvalidDataException($"Health property '{propertyName}' is not boolean.");

    private static int RequiredInt32(JsonElement parent, string propertyName) =>
        parent.GetProperty(propertyName).GetInt32();

    private static long RequiredInt64(JsonElement parent, string propertyName) =>
        parent.GetProperty(propertyName).GetInt64();

    private static double? NullableDouble(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
    }

    private static string? NullableString(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static T[] DeserializeList<T>(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Health property '{propertyName}' is not an array.");
        }

        return JsonSerializer.Deserialize<T[]>(value.GetRawText(), JsonOptions) ?? [];
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public static class OperationalShadowAuditExporter
{
    public static bool Export(OperationalShadowAuditEvidence evidence, string path)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string target = Path.GetFullPath(path);
        if (IsDatabaseLikeDestination(target))
        {
            throw new InvalidOperationException(
                "An audit export cannot overwrite a SQLite database or sidecar.");
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

            File.Move(temporary, target, overwrite: false);
            return true;
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

        using FileStream stream = new(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 16, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[16];
        int read = stream.Read(header);
        return read == 16 && header.SequenceEqual("SQLite format 3\0"u8);
    }
}
