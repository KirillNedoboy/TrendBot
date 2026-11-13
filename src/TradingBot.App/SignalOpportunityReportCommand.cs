using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>Read-only composition of frozen public capture, signal journal and outcome ledger.</summary>
public static class SignalOpportunityReportCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length is not (8 or 18 or 20)
            || !StringComparer.Ordinal.Equals(args[0], "signal-only")
            || !StringComparer.Ordinal.Equals(args[1], "opportunity-report")
            || !StringComparer.Ordinal.Equals(args[2], "--capture")
            || !StringComparer.Ordinal.Equals(args[4], "--ledger")
            || !StringComparer.Ordinal.Equals(args[6], "--manifest")
            || args.Length >= 18 && (!StringComparer.Ordinal.Equals(args[8], "--journal")
                || !StringComparer.Ordinal.Equals(args[10], "--symbol")
                || !StringComparer.Ordinal.Equals(args[12], "--profile")
                || !StringComparer.Ordinal.Equals(args[14], "--declaration")
                || !StringComparer.Ordinal.Equals(args[16], "--freeze"))
            || args.Length == 20 && !StringComparer.Ordinal.Equals(args[18], "--outcomes")
            || args.Any(string.IsNullOrWhiteSpace))
        {
            error.WriteLine("Usage: signal-only opportunity-report --capture <coverage-report.json> --ledger <native-bars.sqlite> --manifest <signal-study.json> [--journal <signal-journal.sqlite> --symbol <symbol> --profile <profile.json> --declaration <predeclared-run.json> --freeze <frozen-journal.json> [--outcomes <directory>]]");
            return 2;
        }
        try
        {
            SignalOpportunityReport report = Build(args);
            output.WriteLine(report.CanonicalJson);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or SqliteException or JsonException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or OverflowException or FormatException)
        {
            error.WriteLine($"signal-only opportunity-report: unavailable ({exception.GetType().Name})");
            return 1;
        }
    }

    private static SignalOpportunityReport Build(string[] args)
    {
        SignalStudyManifest manifest = SignalStudyManifestJournal.Read(args[7]);
        string manifestHash = HashFile(args[7]);
        if (!StringComparer.Ordinal.Equals(manifestHash, HashText(manifest.CanonicalJson)))
            throw new InvalidDataException("The frozen study manifest is noncanonical.");
        string datasetHash = HashFrozenSqlite(args[5]);
        byte[] captureBytes = File.ReadAllBytes(args[3]);
        string captureReportHash = Convert.ToHexString(SHA256.HashData(captureBytes)).ToLowerInvariant();
        using JsonDocument capture = JsonDocument.Parse(captureBytes);
        JsonElement root = capture.RootElement.GetProperty("report");
        if (!root.GetProperty("succeeded").GetBoolean()
            || !root.GetProperty("pointInTimeUniverse").GetProperty("eligible").GetBoolean())
            throw new InvalidDataException("The public capture did not establish eligibility.");
        string symbol = root.GetProperty("pointInTimeUniverse").GetProperty("symbol").GetString()!;
        if (args.Length >= 18 && !StringComparer.Ordinal.Equals(symbol, args[11]))
            throw new InvalidDataException("The signal journal symbol differs from the captured universe.");
        string streamId = $"binance-usdm-native-kline:{symbol}";
        Dictionary<long, AcceptedBar> accepted = ReadAccepted(args[5], streamId,
            out Dictionary<string, AcceptedBar> acceptedRevisions);
        SignalJournalSnapshot? journal = null;
        string captureConfigurationHash = root.GetProperty("configurationSha256").GetString()!.ToLowerInvariant();
        string configurationHash = captureConfigurationHash;
        string? journalHash = null;
        string? scenarioPolicyHash = null;
        if (args.Length >= 18)
        {
            string profileJson = File.ReadAllText(args[13]);
            PublicSignalProfileConfiguration profile =
                CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>(profileJson);
            if (!profile.IsReady || !StringComparer.Ordinal.Equals(profileJson, CoreJsonSerializer.Serialize(profile)))
                throw new InvalidDataException("The frozen public signal profile is incomplete or noncanonical.");
            configurationHash = HashText(profileJson);
            if (!StringComparer.Ordinal.Equals(configurationHash, HashFile(args[13])))
                throw new InvalidDataException("The frozen public signal profile bytes are noncanonical.");
            journalHash = HashFrozenSqlite(args[9]);
            using SqliteSignalJournalStore signalStore = SqliteSignalJournalStore.OpenReadOnly(args[9]);
            journal = signalStore.Read(streamId);
            if (journal.Signals.Any(item => !StringComparer.Ordinal.Equals(
                    item.Evidence.Provenance.ProfileConfigurationHash, configurationHash)))
                throw new InvalidDataException("A committed signal uses a different public signal profile.");
            foreach (SignalJournalSignal item in journal.Signals)
            {
                if (!StringComparer.Ordinal.Equals(item.Signal.Identity.InstrumentId.Value, symbol)
                    || !accepted.TryGetValue(item.Signal.BarInterval.OpenTime.Value.ToUnixTimeMilliseconds(),
                        out AcceptedBar? signalBar))
                    continue;
                if (!StringComparer.Ordinal.Equals(item.Signal.ReferencePrice.EventId, signalBar.EventId)
                    || item.Evidence.Provenance.InputReferences.Any(reference =>
                        !acceptedRevisions.TryGetValue(reference.EventId, out AcceptedBar? source)
                        || !StringComparer.Ordinal.Equals(reference.PayloadHash, source.RevisionHash)
                        || !StringComparer.Ordinal.Equals(reference.Timeframe, source.Timeframe)
                        || reference.StableSequence != source.StableSequence
                        || reference.EventTime.Value != source.EventTime
                        || reference.AvailabilityTime.Value != source.AvailabilityTime))
                    throw new InvalidDataException("A committed signal refers to different public input provenance.");
            }
            if (journal.ScenarioSnapshot is not { } scenario || !scenario.Policies.IsReady)
                throw new InvalidDataException("The committed scenario policy is unavailable or incomplete.");
            scenarioPolicyHash = HashText(CoreJsonSerializer.Serialize(scenario.Policies));
        }
        Dictionary<string, List<SignalJournalDiagnostic>>? diagnostics = null;
        if (journal is not null)
        {
            diagnostics = new Dictionary<string, List<SignalJournalDiagnostic>>(StringComparer.Ordinal);
            foreach (SignalJournalDiagnostic diagnostic in journal.Diagnostics)
            {
                string sourceEventId = SignalOnlyInstrumentConsumer.SourceEventId(diagnostic.InputEventId);
                if (acceptedRevisions.TryGetValue(sourceEventId, out AcceptedBar? source)
                    && !StringComparer.Ordinal.Equals(diagnostic.InputHash, source.RevisionHash))
                    throw new InvalidDataException("A signal diagnostic refers to a different public input revision.");
                if (!diagnostics.TryGetValue(sourceEventId, out List<SignalJournalDiagnostic>? group))
                {
                    group = [];
                    diagnostics.Add(sourceEventId, group);
                }
                group.Add(diagnostic);
            }
        }
        SignalObservedOutcome[] outcomes = args.Length == 20
            ? SignalOutcomeJournal.ReadAll(args[19]).ToArray() : [];
        if (journal is not null)
        {
            Dictionary<string, SignalJournalSignal> signalRecords = journal.Signals.ToDictionary(
                static item => item.Signal.Identity.SignalId, StringComparer.Ordinal);
            if (outcomes.Any(item => signalRecords.TryGetValue(item.SignalId, out SignalJournalSignal? signal)
                && (!StringComparer.Ordinal.Equals(item.SignalRecordHash, signal.Signal.RecordHash)
                    || !StringComparer.Ordinal.Equals(item.EvidenceRecordHash, signal.Evidence.RecordHash))))
                throw new InvalidDataException("An outcome refers to a different signal or evidence revision.");
        }
        List<SignalOpportunityInput> cells = [];
        foreach (JsonElement section in root.GetProperty("coverage").EnumerateArray())
        {
            if (!StringComparer.Ordinal.Equals(section.GetProperty("timeframe").GetString(), "15m"))
                continue;
            long interval = section.GetProperty("intervalMilliseconds").GetInt64();
            HashSet<long> covered = section.GetProperty("coveredOpenTimeMilliseconds")
                .EnumerateArray().Select(static value => value.GetInt64()).ToHashSet();
            foreach (long openMs in section.GetProperty("expectedOpenTimeMilliseconds")
                .EnumerateArray().Select(static value => value.GetInt64()))
            {
                bool isCovered = covered.Contains(openMs) && accepted.ContainsKey(openMs);
                DateTimeOffset open = DateTimeOffset.FromUnixTimeMilliseconds(openMs);
                DateTimeOffset close = open.AddMilliseconds(interval);
                string[] ids = journal?.Signals.Where(item =>
                        item.Signal.BarInterval.OpenTime.Value == open
                        && StringComparer.Ordinal.Equals(item.Signal.Identity.InstrumentId.Value, symbol))
                    .Select(static item => item.Signal.Identity.SignalId).ToArray() ?? [];
                IReadOnlyList<SignalJournalDiagnostic> matchingDiagnostics = Array.Empty<SignalJournalDiagnostic>();
                if (diagnostics is not null && accepted.TryGetValue(openMs, out AcceptedBar? bar)
                    && diagnostics.TryGetValue(bar.EventId, out List<SignalJournalDiagnostic>? group))
                    matchingDiagnostics = group;
                if (matchingDiagnostics.Count > 1 && matchingDiagnostics.Any(static item =>
                    StringComparer.Ordinal.Equals(item.Code, "no-confirmed-setup")))
                    throw new InvalidDataException("A no-setup decision conflicts with another diagnostic.");
                SignalJournalDiagnostic? diagnostic = matchingDiagnostics.Count switch
                {
                    0 => null,
                    1 => matchingDiagnostics[0],
                    _ => matchingDiagnostics.OrderByDescending(IsUnknownDiagnostic)
                        .ThenBy(static item => item.Code, StringComparer.Ordinal).First()
                };
                SignalOpportunityState state = !isCovered ? SignalOpportunityState.Unknown
                    : ids.Length != 0 ? SignalOpportunityState.Signaled
                    : diagnostic is null ? SignalOpportunityState.Unknown
                    : StringComparer.Ordinal.Equals(diagnostic.Code, "no-confirmed-setup")
                        ? SignalOpportunityState.NoSignal
                        : IsUnknownDiagnostic(diagnostic)
                            ? SignalOpportunityState.Unknown : SignalOpportunityState.Blocked;
                string? reason = state == SignalOpportunityState.Unknown
                    ? !isCovered ? "missing-or-unaccepted-public-bar"
                        : journal is null ? "signal-journal-unavailable"
                        : diagnostic?.Code ?? "no-committed-decision-for-interval"
                    : state == SignalOpportunityState.Blocked ? diagnostic!.Code : null;
                string[] reasonCodes = matchingDiagnostics.Count == 0 ? [] :
                    matchingDiagnostics.SelectMany(static item => item.ReasonCodes)
                        .Concat(matchingDiagnostics.Where(item => state == SignalOpportunityState.Signaled
                            || item != diagnostic).Select(static item => item.Code))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                cells.Add(new SignalOpportunityInput("BINANCE_USDM", symbol, open, close,
                    isCovered, state, reason, ids,
                    section.GetProperty("availabilityModel").GetString()!, reasonCodes));
            }
            if (!covered.IsSubsetOf(section.GetProperty("expectedOpenTimeMilliseconds")
                    .EnumerateArray().Select(static value => value.GetInt64()).ToHashSet()))
                throw new InvalidDataException("Capture coverage extends beyond the frozen universe.");
        }
        string universeHash = root.GetProperty("pointInTimeUniverse")
            .GetProperty("snapshotSha256").GetString()!;
        string runId = root.GetProperty("runId").GetString()!;
        HashSet<long> expected = cells.Select(static cell => cell.Open.ToUnixTimeMilliseconds())
            .ToHashSet();
        string? replayDeclarationHash = null;
        string? journalFreezeHash = null;
        if (journalHash is not null && scenarioPolicyHash is not null)
            (replayDeclarationHash, journalFreezeHash) = ValidateReplayFreeze(args[15], args[17],
                new ReplayFreezeInputs(datasetHash, captureReportHash, configurationHash,
                    scenarioPolicyHash, journalHash, manifestHash));
        if (!StringComparer.Ordinal.Equals(datasetHash, HashFrozenSqlite(args[5]))
            || journalHash is not null && !StringComparer.Ordinal.Equals(journalHash, HashFrozenSqlite(args[9])))
            throw new InvalidDataException("A frozen SQLite input changed during report construction.");
        return SignalOpportunityReporter.Create(manifest, datasetHash, runId,
            datasetHash, universeHash, configurationHash, cells,
            journal?.Signals.Select(static item => item.Signal.Identity.SignalId) ?? [],
            accepted.Where(pair => !expected.Contains(pair.Key)).Select(static pair => pair.Value.EventId),
            outcomes, captureConfigurationHash, journalHash, scenarioPolicyHash, captureReportHash,
            replayDeclarationHash, journalFreezeHash);
    }

    private static bool IsUnknownDiagnostic(SignalJournalDiagnostic diagnostic) =>
        diagnostic.Code.Contains("unknown", StringComparison.OrdinalIgnoreCase)
        || diagnostic.ReasonCodes.Any(static reason =>
            reason.Contains("unknown", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<long, AcceptedBar> ReadAccepted(string ledgerPath, string streamId,
        out Dictionary<string, AcceptedBar> revisions)
    {
        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = Path.GetFullPath(ledgerPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using SqliteConnection connection = new(connectionString.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, revision_hash, payload_json, payload_hash, intake_sequence FROM native_bar_input_receipts WHERE stream_id = $stream COLLATE BINARY AND status = 'accepted' ORDER BY intake_sequence";
        command.Parameters.AddWithValue("$stream", streamId);
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<long, AcceptedBar> accepted = [];
        revisions = new Dictionary<string, AcceptedBar>(StringComparer.Ordinal);
        while (reader.Read())
        {
            string eventId = reader.GetString(0);
            string revisionHash = reader.GetString(1);
            string payload = reader.GetString(2);
            if (!SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))
                    .SequenceEqual((byte[])reader[3]))
                throw new InvalidDataException("A captured public input payload hash is invalid.");
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement kline = document.RootElement.GetProperty("kline");
            string timeframe = kline.GetProperty("timeframe").GetString()!;
            long interval = timeframe switch
            {
                "15m" => 900_000,
                "1h" => 3_600_000,
                _ => throw new InvalidDataException("A captured timeframe is unsupported.")
            };
            long open = kline.GetProperty("openTimeMilliseconds").GetInt64();
            JsonElement metadata = document.RootElement.GetProperty("sourceMetadata");
            long sequence = reader.GetInt64(4);
            if (metadata.GetProperty("stableSequence").GetInt64() != sequence)
                throw new InvalidDataException("A captured input has an inconsistent stable sequence.");
            DateTimeOffset available = metadata.GetProperty("availabilityTime")
                .GetProperty("value").GetDateTimeOffset();
            AcceptedBar bar = new(eventId, revisionHash, timeframe, sequence,
                DateTimeOffset.FromUnixTimeMilliseconds(checked(open + interval)), available);
            if (!revisions.TryAdd(eventId, bar))
                throw new InvalidDataException("The public intake contains conflicting accepted event identities.");
            if (timeframe != "15m")
                continue;
            if (!accepted.TryAdd(open, bar))
                throw new InvalidDataException("The public intake contains conflicting accepted intervals.");
        }
        return accepted;
    }

    private static string HashFrozenSqlite(string path)
    {
        string walPath = path + "-wal";
        if (File.Exists(walPath) && new FileInfo(walPath).Length != 0)
            throw new InvalidDataException("A frozen SQLite input has an uncheckpointed WAL.");
        return HashFile(path);
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static (string DeclarationHash, string FreezeHash) ValidateReplayFreeze(
        string declarationPath, string freezePath, ReplayFreezeInputs inputs)
    {
        byte[] declarationBytes = File.ReadAllBytes(declarationPath);
        byte[] freezeBytes = File.ReadAllBytes(freezePath);
        string declarationHash = Convert.ToHexString(SHA256.HashData(declarationBytes)).ToLowerInvariant();
        string freezeHash = Convert.ToHexString(SHA256.HashData(freezeBytes)).ToLowerInvariant();
        using JsonDocument declaration = JsonDocument.Parse(declarationBytes);
        using JsonDocument freeze = JsonDocument.Parse(freezeBytes);
        JsonElement declared = declaration.RootElement;
        JsonElement frozen = freeze.RootElement;
        RequireFrozenField(declared, "formatVersion", "t017-research-freeze-v1");
        RequireFrozenField(declared, "datasetHash", inputs.DatasetHash);
        RequireFrozenField(declared, "coverageHash", inputs.CoverageHash);
        RequireFrozenField(declared, "profileHash", inputs.ProfileHash);
        RequireFrozenField(declared, "scenarioPolicyHash", inputs.ScenarioPolicyHash);
        RequireFrozenField(frozen, "formatVersion", "t017-journal-freeze-v1");
        RequireFrozenField(frozen, "predeclaredRunSha256", declarationHash);
        RequireFrozenField(frozen, "studyManifestSha256", inputs.ManifestHash);
        RequireFrozenField(frozen, "datasetSha256", inputs.DatasetHash);
        RequireFrozenField(frozen, "coverageSha256", inputs.CoverageHash);
        RequireFrozenField(frozen, "profileSha256", inputs.ProfileHash);
        RequireFrozenField(frozen, "scenarioPolicySha256", inputs.ScenarioPolicyHash);
        RequireFrozenField(frozen, "signalJournalSha256", inputs.JournalHash);
        return (declarationHash, freezeHash);
    }

    private static void RequireFrozenField(JsonElement root, string field, string expected)
    {
        if (!StringComparer.Ordinal.Equals(root.GetProperty(field).GetString(), expected))
            throw new InvalidDataException("Frozen replay evidence does not match its inputs.");
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed record ReplayFreezeInputs(string DatasetHash, string CoverageHash, string ProfileHash,
        string ScenarioPolicyHash, string JournalHash, string ManifestHash);

    private sealed record AcceptedBar(string EventId, string RevisionHash, string Timeframe,
        long StableSequence, DateTimeOffset EventTime, DateTimeOffset AvailabilityTime);
}
