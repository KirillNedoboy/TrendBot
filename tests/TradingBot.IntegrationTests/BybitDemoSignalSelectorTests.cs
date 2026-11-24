using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoSignalSelectorTests
{
    private const string Stream = "binance-usdm-native-kline:BTCUSDT";
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectsCommittedActiveFlowSignalButNotStaleOrDisallowedSymbols()
    {
        using Fixture fixture = new();
        SignalJournalActive active = Assert.Single(fixture.Journal.ReadActiveSignals(Stream).Signals);
        SignalJournalSignal signal = active.Signal;
        SignalScenarioEpisode episode = active.Episode;
        SignalScenarioEvent alert = active.Alert;
        HashSet<string> allowed = new(StringComparer.Ordinal) { "BTCUSDT" };

        Assert.True(BybitDemoSignalSelector.TryCreate(signal, episode, alert, allowed, Now,
            TimeSpan.FromSeconds(60), out BybitDemoSignalCandidate candidate));
        Assert.Equal(signal.Signal.Identity.SignalId, candidate.SignalId);
        Assert.Equal(97m, candidate.Stop);
        Assert.Equal(103m, candidate.FirstTarget);
        Assert.Equal(106m, candidate.SecondTarget);
        Assert.False(BybitDemoSignalSelector.TryCreate(signal, episode, alert, allowed,
            Now.AddSeconds(61), TimeSpan.FromSeconds(60), out _));
        Assert.False(BybitDemoSignalSelector.TryCreate(signal, episode, alert,
            new HashSet<string>(StringComparer.Ordinal), Now, TimeSpan.FromSeconds(60), out _));
        Assert.False(BybitDemoSignalSelector.TryCreate(signal, episode, alert, allowed,
            Now.AddHours(1), TimeSpan.FromHours(2), out _));
    }

    [Fact]
    public void CanceledScenarioIsNotEligibleEvenIfSignalIsFresh()
    {
        using Fixture fixture = new();
        SignalJournalSnapshot before = fixture.Journal.Read(Stream);
        SignalScenarioEpisode active = Assert.Single(before.ScenarioSnapshot!.Episodes);
        SignalScenarioInput cancel = SignalScenarioInput.Invalidate(Stream, "cancel", active.Identity.SignalId,
            "exit", Timestamp(Now.AddSeconds(1)), Timestamp(Now.AddSeconds(1)), 3);
        SignalScenarioProjectionTransition transition = fixture.Engine.Apply(before.ScenarioSnapshot, cancel);
        SignalScenarioEpisode canceled = Assert.Single(transition.NextSnapshot.Episodes);
        Assert.Equal(SignalScenarioEpisodeState.Canceled, canceled.State);
        Assert.False(BybitDemoSignalSelector.TryCreate(Assert.Single(before.Signals), canceled,
            Assert.Single(before.ScenarioEvents), new HashSet<string>(StringComparer.Ordinal) { "BTCUSDT" },
            Now.AddSeconds(1), TimeSpan.FromSeconds(60), out _));
    }

    [Fact]
    public void ThreeJournalSourceSelectsFreshSignalAndReportsMissingStreams()
    {
        using Fixture fixture = new();
        string directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        string second = Path.Combine(directory, "donchian30.sqlite");
        string third = Path.Combine(directory, "donchian1h.sqlite");
        using SqliteSignalJournalStore secondJournal = SqliteSignalJournalStore.Open(second);
        using SqliteSignalJournalStore thirdJournal = SqliteSignalJournalStore.Open(third);
        string json = JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            mode = "bybit-demo",
            targetNotionalUsdt = 250,
            maximumPositions = 20,
            maximumPriceDifferencePercent = 10,
            maximumSlippagePercent = 10,
            maximumLeverage = 10,
            marginMode = "isolated",
            maximumSignalAgeSeconds = 60,
            maximumQuoteAgeSeconds = 5,
            privateStreamFreshnessSeconds = 30,
            receiveWindowMilliseconds = 5000,
            statePath = Path.Combine(directory, "demo.sqlite"),
            signalJournalPaths = new[] { fixture.DatabasePath, second, third }
        });
        BybitDemoSignalBatch batch = BybitDemoSignalSource.Read(BybitDemoConfiguration.Parse(json),
            new HashSet<string>(StringComparer.Ordinal) { "BTCUSDT" }, Now);
        BybitDemoSignalCandidate candidate = Assert.Single(batch.Candidates);
        Assert.Equal(SetupType.FlowAtrRecovery, candidate.Family);
        Assert.Equal("BTCUSDT", candidate.Symbol);
        Assert.Equal(3, batch.UnavailableStreams.Count);
    }

    [Fact]
    public void MissingCommittedAlertCannotProduceAnActiveCandidate()
    {
        using Fixture fixture = new();
        using (SqliteConnection connection = new($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM signal_journal_scenario_events WHERE stream_id = $stream;";
            command.Parameters.AddWithValue("$stream", Stream);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.Throws<InvalidDataException>(() => fixture.Journal.ReadActiveSignals(Stream));
    }

    private static UtcTimestamp Timestamp(DateTimeOffset value) => new(value);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bybit-signal-{Guid.NewGuid():N}");
        public SqliteSignalJournalStore Journal { get; }
        public SignalScenarioProjectionEngine Engine { get; }
        public string DatabasePath => Path.Combine(_directory, "signals.sqlite");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Journal = SqliteSignalJournalStore.Open(Path.Combine(_directory, "signals.sqlite"));
            SignalScenarioPolicySet policies = new(1,
                new SignalScenarioExpiryPolicy(1, "expiry-v1", TimeSpan.FromHours(1)),
                new SignalScenarioRearmPolicy(1, "rearm-v1", TimeSpan.FromMinutes(2), true),
                new SignalScenarioInvalidationPolicy(1, "invalidation-v1", true, true));
            Engine = new SignalScenarioProjectionEngine(policies);
            UtcTimestamp open = Timestamp(Now.AddMinutes(-15));
            UtcTimestamp close = Timestamp(Now);
            UtcTimestamp context = Timestamp(Now.AddMinutes(-75));
            SignalScenarioAnchor anchor = SignalScenarioAnchor.Create(new InstrumentId("BTCUSDT"),
                "BINANCE_USDM", "15m", Direction.Long, SetupType.FlowAtrRecovery,
                "profile-v1", "flow-v1", "anchor", open, 1);
            SignalScenarioInput input = SignalScenarioInput.Confirm(Stream, "confirm", anchor, close, close, 2);
            SignalScenarioProjectionTransition transition = Engine.Apply(Engine.CreateInitialSnapshot(), input);
            SignalProvenance provenance = SignalProvenance.Create("profile-v1", new string('a', 64),
                new CausalEventCursor(close, 2), new CausalEventCursor(context, 0),
                [new SignalInputReference("binance-public", "native-kline-v1", "anchor", Hash("anchor"),
                    SignalInputRole.Setup, "15m", open, open, 1),
                 new SignalInputReference("binance-public", "native-kline-v1", "context", Hash("context"),
                    SignalInputRole.HigherTimeframe, "1h", context, context, 0)]);
            SignalEvidenceRecord evidence = SignalEvidenceRecord.Create(anchor.Identity, provenance,
                SignalEvidenceState.Complete, ["fixture-reviewed"]);
            SignalPriceLevels levels = new(1, "fixture-levels", SignalPriceLevelPolicy.CurrentFormulaVersion,
                14, new SignalPriceLevelParameters(SetupType.FlowAtrRecovery, 1.5m, 0.1m, 1m, 2m),
                "15m", Direction.Long, "anchor", 100m, 99m, 2m, SignalPriceLevelsState.Ready,
                97m, 103m, 106m, null);
            SignalRecord signal = SignalRecord.Create(anchor.Identity, evidence,
                SignalAssessmentState.ConfirmedOnClosedBar, new SignalBarInterval(open, close), close,
                new SignalReferencePrice("binance-public", "native-kline-v1", "anchor", open, new Price(100m)),
                priceLevels: levels);
            SignalJournalCommit commit = new(Stream, 0, 1, input.EventId, Hash(input.EventId), close, close,
                2, input.InputHash, transition.NextSnapshot, transition.EmittedEvents, signal, evidence);
            Assert.True(Journal.Append(commit));
        }

        public void Dispose()
        {
            Journal.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
