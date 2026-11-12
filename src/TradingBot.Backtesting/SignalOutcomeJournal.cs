using System.Text;
using System.Text.Json;

namespace TradingBot.Backtesting;

/// <summary>Separate append-only, content-addressed outcome ledger; never changes the signal journal.</summary>
public static class SignalOutcomeJournal
{
    public static bool Append(string directory, SignalObservedOutcome outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(outcome);
        Validate(outcome);
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, outcome.Identity + ".json");
        string temporary = Path.Combine(root, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.SequentialScan))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(
                    outcome.CanonicalHash + "\n" + outcome.CanonicalJson);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temporary, path);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                if (!StringComparer.Ordinal.Equals(Read(path).CanonicalJson,
                        outcome.CanonicalJson))
                    throw new InvalidOperationException(
                        "An outcome identity already exists with different observation content.");
                return false;
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static IReadOnlyList<SignalObservedOutcome> ReadAll(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json")
            .Order(StringComparer.Ordinal).Select(Read).ToArray();
    }

    private static SignalObservedOutcome Read(string path)
    {
        string envelope = File.ReadAllText(path, Encoding.UTF8);
        int separator = envelope.IndexOf('\n', StringComparison.Ordinal);
        if (separator != 64) throw new InvalidDataException("The outcome envelope is incomplete.");
        string json = envelope[(separator + 1)..];
        if (!StringComparer.Ordinal.Equals(envelope[..separator], SignalOutcomeObserver.Hash(json)))
            throw new InvalidDataException("The persisted outcome content hash is invalid.");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string identity = root.GetProperty("identity").GetString()!;
        JsonElement material = root.GetProperty("identityMaterial");
        if (!StringComparer.Ordinal.Equals(identity,
                SignalOutcomeObserver.Hash(material.GetRawText()))
            || !StringComparer.Ordinal.Equals(root.GetProperty("signalId").GetString(),
                material.GetProperty("signalId").GetString())
            || !StringComparer.Ordinal.Equals(root.GetProperty("signalRecordHash").GetString(),
                material.GetProperty("signalRecordHash").GetString())
            || !StringComparer.Ordinal.Equals(root.GetProperty("evidenceRecordHash").GetString(),
                material.GetProperty("evidenceRecordHash").GetString())
            || !StringComparer.Ordinal.Equals(root.GetProperty("clock").GetString(),
                material.GetProperty("clock").GetString())
            || !StringComparer.Ordinal.Equals(root.GetProperty("channel").GetString(),
                material.GetProperty("channel").GetString())
            || !StringComparer.Ordinal.Equals(root.GetProperty("boundary").GetRawText(),
                material.GetProperty("boundary").GetRawText()))
            throw new InvalidDataException("The persisted outcome identity is invalid.");
        if (!StringComparer.Ordinal.Equals(Path.GetFileName(path), identity + ".json"))
            throw new InvalidDataException("The outcome filename does not match its identity.");
        SignalObservedOutcome outcome = new(identity, SignalOutcomeObserver.Hash(json),
            root.GetProperty("signalId").GetString()!,
            root.GetProperty("signalRecordHash").GetString()!,
            root.GetProperty("evidenceRecordHash").GetString()!,
            Enum.Parse<SignalObservationClock>(root.GetProperty("clock").GetString()!),
            root.GetProperty("channel").GetString(),
            root.GetProperty("boundary").ValueKind == JsonValueKind.Null ? null
                : root.GetProperty("boundary").GetDateTimeOffset(),
            root.GetProperty("methodVersion").GetString()!,
            root.GetProperty("horizon").GetDateTimeOffset(),
            root.GetProperty("effectiveEnd").GetDateTimeOffset(),
            DecimalOrNull(root.GetProperty("observationReferencePrice")),
            root.GetProperty("observationReferenceAt").ValueKind == JsonValueKind.Null
                ? null : root.GetProperty("observationReferenceAt").GetDateTimeOffset(),
            Enum.Parse<SignalObservationState>(root.GetProperty("state").GetString()!),
            root.GetProperty("reason").GetString(),
            DecimalOrNull(root.GetProperty("signedReturn")),
            DecimalOrNull(root.GetProperty("mae")), DecimalOrNull(root.GetProperty("mfe")),
            Enum.Parse<SignalPassage>(root.GetProperty("passage").GetString()!),
            root.GetProperty("firstPassageAt").ValueKind == JsonValueKind.Null ? null
                : root.GetProperty("firstPassageAt").GetDateTimeOffset(),
            root.GetProperty("firstPassageEarliest").ValueKind == JsonValueKind.Null
                ? null : root.GetProperty("firstPassageEarliest").GetDateTimeOffset(),
            root.GetProperty("firstPassageLatest").ValueKind == JsonValueKind.Null
                ? null : root.GetProperty("firstPassageLatest").GetDateTimeOffset(),
            root.GetProperty("coveredBars").GetInt32(), json);
        Validate(outcome);
        return outcome;
    }

    private static decimal? DecimalOrNull(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal();

    private static void Validate(SignalObservedOutcome outcome)
    {
        if (outcome.Identity.Length != 64 || outcome.Identity.Any(static c =>
                c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || !StringComparer.Ordinal.Equals(outcome.CanonicalHash,
                SignalOutcomeObserver.Hash(outcome.CanonicalJson)))
            throw new InvalidDataException("The outcome content or identity is invalid.");
    }
}
