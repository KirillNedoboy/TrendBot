using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Backtesting;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class SignalOnlyJournalOutputTests
{
    private const string StreamId = "binance-usdm-native-kline:BTCUSDT";

    [Fact]
    public async Task ActualCliRendersCommittedSignalsDiagnosticsAndLinkedScenarioEvents()
    {
        using TemporaryDatabase database = new();
        string firstAlertEventId;
        string firstSignalId;
        string centerAlertEventId;
        string centerSignalId;

        using (SqliteSignalJournalStore journal =
            SqliteSignalJournalStore.Open(database.Path))
        {
            JournalBuilder builder = new(journal);
            ScenarioCommit first = builder.Confirm("confirm-boundary-long",
                "anchor-boundary-long", SetupType.BoundaryRejection, Direction.Long,
                anchorMinute: 0, anchorSequence: 1, eventMinute: 15, sequence: 3);
            firstAlertEventId = Assert.Single(first.Transition.EmittedEvents,
                item => item.Kind == SignalScenarioEventKind.AlertCreated).EventId;
            firstSignalId = first.Anchor.Identity.SignalId;
            Assert.Equal(SignalScenarioTransitionStatus.Duplicate,
                builder.Engine.Apply(builder.Snapshot, first.Input).Status);
            Assert.False(journal.Append(first.Commit));

            ScenarioCommit repeated = builder.Confirm("continue-boundary-long",
                "anchor-boundary-long", SetupType.BoundaryRejection,
                Direction.Long, anchorMinute: 0, anchorSequence: 1,
                eventMinute: 16, sequence: 4);
            SignalScenarioEvent continued = Assert.Single(
                repeated.Transition.EmittedEvents,
                item => item.Kind == SignalScenarioEventKind.Continued);
            Assert.Equal(firstSignalId, continued.SignalId);
            Assert.Equal(firstAlertEventId, continued.EpisodeAlertEventId);
            ScenarioCommit center = builder.Confirm("confirm-center-long",
                "anchor-center-long", SetupType.CenterRetest, Direction.Long,
                anchorMinute: 2, anchorSequence: 5, eventMinute: 17, sequence: 6);
            centerAlertEventId = Assert.Single(center.Transition.EmittedEvents,
                item => item.Kind == SignalScenarioEventKind.AlertCreated).EventId;
            centerSignalId = center.Anchor.Identity.SignalId;
            builder.Confirm("cancel-boundary-long", "anchor-boundary-short",
                SetupType.BoundaryRejection, Direction.Short, anchorMinute: 3,
                anchorSequence: 7, eventMinute: 18, sequence: 8);
            builder.Confirm("expire-center-long", "anchor-center-short",
                SetupType.CenterRetest, Direction.Short, anchorMinute: 4,
                anchorSequence: 9, eventMinute: 22, sequence: 10);
            builder.CommitUnknownConfigurationDiagnostic();
        }

        CliResult jsonResult = await RunCliAsync(database.Path, "json",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, jsonResult.ExitCode);
        Assert.Empty(jsonResult.Error);
        using JsonDocument document = JsonDocument.Parse(jsonResult.Output);
        JsonElement root = document.RootElement;
        JsonElement cards = root.GetProperty("signals");
        Assert.Equal(4, cards.GetArrayLength());
        JsonElement firstCard = cards.EnumerateArray().Single(card =>
            card.GetProperty("signalId").GetString() == firstSignalId);
        Assert.Equal("BTCUSDT", firstCard.GetProperty("instrument").GetString());
        Assert.Equal("BINANCE_USDM", firstCard.GetProperty("venue").GetString());
        Assert.Equal("Long", firstCard.GetProperty("direction").GetString());
        Assert.Equal("BoundaryRejection", firstCard.GetProperty("family").GetString());
        Assert.Equal("ConfirmedOnClosedBar",
            firstCard.GetProperty("assessmentState").GetString());
        Assert.Equal("RESEARCH", firstCard.GetProperty("researchStatus").GetString());
        Assert.Equal("UNKNOWN",
            firstCard.GetProperty("statisticalValidation").GetString());
        JsonElement interval = firstCard.GetProperty("interval");
        Assert.Equal("15m", interval.GetProperty("timeframe").GetString());
        Assert.Equal(AtMinute(0).Value.ToString("O"),
            interval.GetProperty("openTime").GetString());
        Assert.Equal(AtMinute(15).Value.ToString("O"),
            interval.GetProperty("closeTime").GetString());
        Assert.Equal(AtMinute(15).Value.ToString("O"),
            firstCard.GetProperty("decisionAt").GetString());
        JsonElement decisionCut = firstCard.GetProperty("decisionCut");
        Assert.Equal(AtMinute(15).Value.ToString("O"),
            decisionCut.GetProperty("asOf").GetString());
        Assert.Equal(3, decisionCut.GetProperty("maxStableSequence").GetInt64());
        JsonElement higherTimeframeCut = firstCard.GetProperty("higherTimeframeCut");
        Assert.Equal(AtMinute(-60).Value.ToString("O"),
            higherTimeframeCut.GetProperty("asOf").GetString());
        Assert.Equal(0, higherTimeframeCut.GetProperty("maxStableSequence")
            .GetInt64());
        Assert.Equal("scenario-reference-only", firstCard.GetProperty("referencePrice")
            .GetProperty("kind").GetString());
        Assert.Equal("anchor-boundary-long", firstCard.GetProperty("scenarioAnchor")
            .GetProperty("eventId").GetString());
        JsonElement anchorInput = firstCard.GetProperty("scenarioAnchor")
            .GetProperty("causalInput");
        Assert.Equal("binance-public", anchorInput.GetProperty("sourceId").GetString());
        Assert.Equal("native-kline-v1",
            anchorInput.GetProperty("sourceVersion").GetString());
        Assert.Equal("anchor-boundary-long",
            anchorInput.GetProperty("eventId").GetString());
        Assert.Equal("15m", anchorInput.GetProperty("timeframe").GetString());
        JsonElement reference = firstCard.GetProperty("referencePrice");
        Assert.Equal("binance-public", reference.GetProperty("sourceId").GetString());
        Assert.Equal("native-kline-v1",
            reference.GetProperty("sourceVersion").GetString());
        Assert.Equal("anchor-boundary-long",
            reference.GetProperty("eventId").GetString());
        Assert.Equal(100m, reference.GetProperty("price").GetDecimal());
        Assert.Equal("Canceled", firstCard.GetProperty("scenario")
            .GetProperty("state").GetString());
        Assert.Null(firstCard.GetProperty("publishedAt").GetString());
        Assert.Equal(JsonValueKind.Null,
            firstCard.GetProperty("outputReceipt").ValueKind);
        Assert.Contains("fixture-quality-reviewed",
            firstCard.GetProperty("qualityReasons").EnumerateArray()
                .Select(static item => item.GetString()));
        Assert.Empty(root.GetProperty("outputReceipts").EnumerateArray());

        JsonElement diagnostics = root.GetProperty("diagnostics");
        JsonElement missingConfiguration = Assert.Single(diagnostics.EnumerateArray());
        Assert.Equal("unknown-configuration",
            missingConfiguration.GetProperty("code").GetString());
        Assert.Contains("profile-configuration-unknown",
            missingConfiguration.GetProperty("reasonCodes").EnumerateArray()
                .Select(static item => item.GetString()));
        Assert.False(missingConfiguration.TryGetProperty("direction", out _));

        JsonElement events = root.GetProperty("scenarioEvents");
        JsonElement overlap = events.EnumerateArray().Single(item =>
        {
            string signalId = item.GetProperty("signalId").GetString()!;
            string? relatedSignalId =
                item.GetProperty("relatedSignalId").GetString();
            return item.GetProperty("kind").GetString() == "OverlapLinked"
                && (signalId == firstSignalId && relatedSignalId == centerSignalId
                    || signalId == centerSignalId && relatedSignalId == firstSignalId);
        });
        if (overlap.GetProperty("signalId").GetString() == firstSignalId)
        {
            Assert.Equal(firstAlertEventId,
                overlap.GetProperty("episodeAlertEventId").GetString());
            Assert.Equal(centerAlertEventId,
                overlap.GetProperty("relatedEpisodeAlertEventId").GetString());
        }
        else
        {
            Assert.Equal(centerAlertEventId,
                overlap.GetProperty("episodeAlertEventId").GetString());
            Assert.Equal(firstAlertEventId,
                overlap.GetProperty("relatedEpisodeAlertEventId").GetString());
        }
        JsonElement continuedEvent = events.EnumerateArray().Single(item =>
            item.GetProperty("kind").GetString() == "Continued");
        Assert.Equal(firstSignalId,
            continuedEvent.GetProperty("signalId").GetString());
        Assert.Equal(firstAlertEventId,
            continuedEvent.GetProperty("episodeAlertEventId").GetString());
        JsonElement canceled = events.EnumerateArray().Single(item =>
            item.GetProperty("kind").GetString() == "Canceled");
        Assert.Equal(firstSignalId, canceled.GetProperty("signalId").GetString());
        Assert.Equal(firstAlertEventId,
            canceled.GetProperty("episodeAlertEventId").GetString());
        JsonElement expired = events.EnumerateArray().Single(item =>
            item.GetProperty("kind").GetString() == "Expired");
        Assert.Equal(centerSignalId, expired.GetProperty("signalId").GetString());
        Assert.Equal(centerAlertEventId,
            expired.GetProperty("episodeAlertEventId").GetString());
        Assert.Equal(AtMinute(22).Value.ToString("O"),
            expired.GetProperty("effectiveAt").GetString());

        CliResult consoleResult = await RunCliAsync(database.Path, "console",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, consoleResult.ExitCode);
        Assert.Contains("Scenario reference only:", consoleResult.Output,
            StringComparison.Ordinal);
        Assert.Contains("statistical validation: UNKNOWN", consoleResult.Output,
            StringComparison.Ordinal);
        Assert.Contains("Output receipt: none; publishedAt:", consoleResult.Output,
            StringComparison.Ordinal);
        Assert.Contains("DIAGNOSTIC unknown-configuration", consoleResult.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CommittedSignalOutcomeSurvivesObserverJournalReopen()
    {
        using TemporaryDatabase database = new();
        string outcomeDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"tradingbot-outcome-fixture-{Guid.NewGuid():N}");
        try
        {
            using (SqliteSignalJournalStore writer = SqliteSignalJournalStore.Open(database.Path))
            {
                JournalBuilder builder = new(writer);
                builder.Confirm("observer-confirm", "observer-anchor",
                    SetupType.BoundaryRejection, Direction.Long, 0, 1, 15, 3);
            }
            using SqliteSignalJournalStore reader =
                SqliteSignalJournalStore.OpenReadOnly(database.Path);
            SignalJournalSignal committed = Assert.Single(reader.Read(StreamId).Signals);
            SignalOutcomePolicy policy = new("fixture-label-v1",
                BaseTimeValue.AddMinutes(45), null, null, 110m, 90m);
            SignalOutcomeBar[] bars =
            [
                new(BaseTimeValue.AddMinutes(15), BaseTimeValue.AddMinutes(30),
                    BaseTimeValue.AddMinutes(30), 100m, 115m, 95m, 101m),
                new(BaseTimeValue.AddMinutes(30), BaseTimeValue.AddMinutes(45),
                    BaseTimeValue.AddMinutes(45), 101m, 105m, 97m, 102m)
            ];
            SignalObservedOutcome outcome = SignalOutcomeObserver.Observe(
                committed.Signal, committed.Evidence, SignalObservationClock.ChannelPublished,
                "local-output", BaseTimeValue.AddMinutes(22), policy, bars,
                BaseTimeValue.AddMinutes(46));
            Assert.True(SignalOutcomeJournal.Append(outcomeDirectory, outcome));
            Assert.False(SignalOutcomeJournal.Append(outcomeDirectory, outcome));
            SignalObservedOutcome restored = Assert.Single(
                SignalOutcomeJournal.ReadAll(outcomeDirectory));
            Assert.Equal(outcome.CanonicalJson, restored.CanonicalJson);
            Assert.Equal(1, restored.CoveredBars);
            Assert.Equal(SignalPassage.Neither, restored.Passage);
            Assert.Equal(102m / 101m - 1m, restored.SignedReturn);
        }
        finally
        {
            if (Directory.Exists(outcomeDirectory))
                Directory.Delete(outcomeDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingConfigurationIsAJsonDiagnosticAndNeverAReadySignal()
    {
        string missingPath = Path.Combine(Path.GetTempPath(),
            $"tradingbot-missing-config-{Guid.NewGuid():N}.json");
        using StringWriter output = new();
        using StringWriter error = new();

        int result = await SignalOnlyCommand.RunAsync(
            ["signal-only", "--config", missingPath], output, error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement diagnostic = document.RootElement;
        Assert.Equal("diagnostic", diagnostic.GetProperty("kind").GetString());
        Assert.Equal("UnknownConfiguration",
            diagnostic.GetProperty("configurationState").GetString());
        Assert.Equal("public-configuration-unavailable",
            diagnostic.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("signal").ValueKind);
        Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("direction").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            diagnostic.GetProperty("outputReceipt").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            diagnostic.GetProperty("publishedAt").ValueKind);
        Assert.False(File.Exists(missingPath));
    }

    private static async Task<CliResult> RunCliAsync(string journalPath,
        string format, CancellationToken cancellationToken)
    {
        string executableName = OperatingSystem.IsWindows()
            ? "TradingBot.App.exe" : "TradingBot.App";
        string executablePath = Path.Combine(AppContext.BaseDirectory,
            executableName);
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("signal-only");
        startInfo.ArgumentList.Add("render");
        startInfo.ArgumentList.Add("--journal");
        startInfo.ArgumentList.Add(journalPath);
        startInfo.ArgumentList.Add("--symbol");
        startInfo.ArgumentList.Add("BTCUSDT");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add(format);

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start TradingBot.App.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CliResult(process.ExitCode, await outputTask, await errorTask);
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private static UtcTimestamp AtMinute(int minute) =>
        new(BaseTimeValue.AddMinutes(minute));

    private static DateTimeOffset BaseTimeValue =>
        new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class JournalBuilder
    {
        private readonly SqliteSignalJournalStore _journal;
        private readonly SignalScenarioPolicySet _policies = new(
            SignalScenarioPolicySet.CurrentFormatVersion,
            new SignalScenarioExpiryPolicy(
                SignalScenarioExpiryPolicy.CurrentFormatVersion, "expiry-fixture-v1",
                TimeSpan.FromMinutes(5)),
            new SignalScenarioRearmPolicy(
                SignalScenarioRearmPolicy.CurrentFormatVersion, "rearm-fixture-v1",
                TimeSpan.FromMinutes(2), true),
            new SignalScenarioInvalidationPolicy(
                SignalScenarioInvalidationPolicy.CurrentFormatVersion,
                "invalidation-fixture-v1", true, true));
        private readonly SignalScenarioProjectionEngine _engine;
        private long _inputSequence;

        public SignalScenarioProjectionEngine Engine => _engine;

        public SignalScenarioProjectionSnapshot Snapshot { get; private set; }

        public JournalBuilder(SqliteSignalJournalStore journal)
        {
            _journal = journal;
            _engine = new SignalScenarioProjectionEngine(_policies);
            Snapshot = _engine.CreateInitialSnapshot();
        }


        public ScenarioCommit Confirm(string eventId, string anchorEventId,
            SetupType family, Direction direction, int anchorMinute,
            long anchorSequence, int eventMinute, long sequence)
        {
            string familyVersion = family == SetupType.BoundaryRejection
                ? "boundary-rejection-v1" : "center-retest-v1";
            SignalScenarioAnchor anchor = SignalScenarioAnchor.Create(
                new InstrumentId("BTCUSDT"), "BINANCE_USDM", "15m", direction,
                family, "profile-v1", familyVersion, anchorEventId,
                AtMinute(anchorMinute), anchorSequence);
            SignalScenarioInput input = SignalScenarioInput.Confirm(StreamId,
                eventId, anchor, AtMinute(eventMinute), AtMinute(eventMinute), sequence);
            SignalScenarioProjectionTransition transition = _engine.Apply(Snapshot, input);
            SignalScenarioEvent? alert = transition.EmittedEvents.FirstOrDefault(item =>
                item.Kind == SignalScenarioEventKind.AlertCreated);
            (SignalRecord Signal, SignalEvidenceRecord Evidence)? signal = alert is null
                ? null : CreateSignal(anchor, input);
            string inputHash = Hash(eventId);
            SignalJournalCommit commit = new(StreamId, _inputSequence,
                checked(_inputSequence + 1), eventId, inputHash, input.EventTime,
                input.AvailabilityTime, input.StableSequence, input.InputHash,
                transition.NextSnapshot, transition.EmittedEvents,
                signal?.Signal, signal?.Evidence);
            Assert.True(_journal.Append(commit));
            _inputSequence++;
            Snapshot = transition.NextSnapshot;
            return new ScenarioCommit(anchor, input, transition, commit);
        }

        public void CommitUnknownConfigurationDiagnostic()
        {
            const string eventId = "missing-profile-config";
            const long sequence = 11;
            SignalJournalDiagnostic diagnostic = SignalJournalDiagnostic.Create(
                StreamId, eventId, Hash(eventId), "unknown-configuration",
                ["profile-configuration-unknown"]);
            SignalJournalCommit commit = new(StreamId, _inputSequence,
                checked(_inputSequence + 1), eventId, Hash(eventId), AtMinute(23),
                AtMinute(23), sequence, null, Snapshot, [], diagnostic: diagnostic);
            Assert.True(_journal.Append(commit));
            _inputSequence++;
        }

        private static (SignalRecord Signal, SignalEvidenceRecord Evidence) CreateSignal(
            SignalScenarioAnchor anchor, SignalScenarioInput input)
        {
            SignalInputReference setup = new("binance-public", "native-kline-v1",
                anchor.AnchorEventId, Hash(anchor.AnchorEventId), SignalInputRole.Setup,
                "15m", anchor.AnchorEventTime, anchor.AnchorEventTime,
                anchor.AnchorStableSequence);
            SignalInputReference higherTimeframe = new("binance-public",
                "native-kline-v1", "htf-context", Hash("htf-context"),
                SignalInputRole.HigherTimeframe, "1h", AtMinute(-60),
                AtMinute(-60), 0);
            CausalEventCursor decisionCut = new(input.EventTime,
                input.StableSequence);
            CausalEventCursor higherTimeframeCut = new(AtMinute(-60), 0);
            SignalProvenance provenance = SignalProvenance.Create("profile-v1",
                new string('a', 64), decisionCut, higherTimeframeCut,
                [setup, higherTimeframe]);
            SignalEvidenceRecord evidence = SignalEvidenceRecord.Create(
                anchor.Identity, provenance, SignalEvidenceState.Complete,
                ["fixture-quality-reviewed"]);
            SignalRecord signal = SignalRecord.Create(anchor.Identity, evidence,
                SignalAssessmentState.ConfirmedOnClosedBar,
                new SignalBarInterval(anchor.AnchorEventTime,
                    new UtcTimestamp(anchor.AnchorEventTime.Value.AddMinutes(15))),
                input.EventTime,
                new SignalReferencePrice(setup.SourceId, setup.SourceVersion,
                    setup.EventId, setup.EventTime, new Price(100m)));
            return (signal, evidence);
        }
    }

    private sealed record ScenarioCommit(SignalScenarioAnchor Anchor,
        SignalScenarioInput Input, SignalScenarioProjectionTransition Transition,
        SignalJournalCommit Commit);

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tradingbot-signal-output-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
    }
}
