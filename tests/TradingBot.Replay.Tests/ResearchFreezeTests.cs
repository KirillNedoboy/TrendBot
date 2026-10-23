using System.Text.Json;
using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class ResearchFreezeTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void FrozenManifestSnapshotsInputsAndCanonicalizesEquivalentOrderings()
    {
        ReplayEnvelope first = Envelope("event-1", 0, 1);
        ReplayEnvelope second = Envelope("event-2", 1, 2);
        List<ReplayEnvelope> source = [second, first, first];
        List<BacktestConfigurationEntry> configuration =
        [
            new("profile", "research-v1"),
            new("dataset", "fixture-v1")
        ];

        FrozenResearchManifest manifest = FrozenResearchManifest.Create(
            "oos-fixture", "dataset-v1", "synthetic fixture", ["bars"], ["none"],
            source, "config-v1", configuration, [new("selection", "frozen")],
            "risk-v1", "fill-v1", ["OHLCV intrabar ordering remains unknown"]);

        source.Clear();
        configuration.Clear();

        FrozenResearchManifest permuted = FrozenResearchManifest.Create(
            "oos-fixture", "dataset-v1", "synthetic fixture", ["bars"], ["none"],
            [first, second, first], "config-v1",
            [new("dataset", "fixture-v1"), new("profile", "research-v1")],
            [new("selection", "frozen")], "risk-v1", "fill-v1",
            ["OHLCV intrabar ordering remains unknown"]);

        Assert.Equal(3, manifest.Events.Count);
        Assert.Equal(manifest.Identity, permuted.Identity);
        Assert.Equal(manifest.CanonicalJson, permuted.CanonicalJson);
        Assert.Equal(manifest.DatasetContentHash, permuted.DatasetContentHash);
        Assert.Equal(manifest.ConfigurationHash, permuted.ConfigurationHash);
    }

    [Fact]
    public void FrozenManifestRoundTripsAndRejectsTamperedIdentity()
    {
        FrozenResearchManifest manifest = CreateManifest();

        FrozenResearchManifest restored = FrozenResearchManifest.Deserialize(
            manifest.CanonicalJson);

        Assert.Equal(manifest.Identity, restored.Identity);
        Assert.Equal(manifest.CanonicalJson, restored.CanonicalJson);
        Assert.Equal(manifest.Events, restored.Events);
        Assert.Throws<JsonException>(() => FrozenResearchManifest.Deserialize(
            manifest.CanonicalJson.Replace(manifest.Identity, new string('0', 64),
                StringComparison.Ordinal)));
    }

    [Fact]
    public void FrozenManifestValidatesReplayInputsBeforeHandlerRuns()
    {
        FrozenResearchManifest manifest = CreateManifest();
        int calls = 0;

        ReplayRunResult result = manifest.RunReplay(
            "stream-a", manifest.Events, _ => calls++);

        Assert.True(result.Succeeded);
        Assert.Equal(2, calls);

        ReplayEnvelope changed = Envelope("event-2", 1, 3);
        Assert.Throws<FrozenResearchManifestMismatchException>(() => manifest.RunReplay(
            "stream-a", [manifest.Events[0], changed, manifest.Events[2]], _ => calls++));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ManifestJournalIsWriteOnceAndEvaluationSnapshotRetainsCanonicalReport()
    {
        FrozenResearchManifest manifest = CreateManifest();
        FrozenResearchManifestJournal journal = new();
        journal.Register(manifest);
        journal.Register(manifest);
        Assert.Throws<InvalidOperationException>(() => journal.Register(
            FrozenResearchManifest.Create("different", "dataset-v1", "synthetic fixture",
                ["bars"], ["none"], manifest.Events, "config-v1",
                [new("dataset", "fixture-v1"), new("profile", "research-v1")],
                [new("selection", "frozen")], "risk-v1", "fill-v1",
                ["OHLCV intrabar ordering remains unknown"])));

        ReplayRunResult run = manifest.RunReplay("stream-a", manifest.Events,
            static context => context.Event.EventId);
        BacktestReport report = BacktestReport.Create("run-1", run);
        Assert.Throws<FrozenResearchManifestMismatchException>(() =>
        {
            FrozenResearchEvaluationSnapshot.Create(manifest,
                report with { Events = Array.Empty<BacktestEventReference>() });
        });
        FrozenResearchEvaluationSnapshot snapshot =
            FrozenResearchEvaluationSnapshot.Create(manifest, report);
        string canonical = snapshot.CanonicalJson;

        report = report with { RunId = "mutated-after-capture" };

        Assert.Equal(canonical, snapshot.CanonicalJson);
        Assert.Equal(manifest.Identity, snapshot.ManifestIdentity);
        Assert.Equal(report.StreamId, snapshot.StreamId);
    }

    private static FrozenResearchManifest CreateManifest() => FrozenResearchManifest.Create(
        "oos-fixture", "dataset-v1", "synthetic fixture", ["bars"], ["none"],
        [Envelope("event-1", 0, 1), Envelope("event-2", 1, 2),
            Envelope("event-1", 0, 1)], "config-v1",
        [new("dataset", "fixture-v1"), new("profile", "research-v1")],
        [new("selection", "frozen")], "risk-v1", "fill-v1",
        ["OHLCV intrabar ordering remains unknown"]);

    private static ReplayEnvelope Envelope(string eventId, int minutes, long sequence) =>
        new("stream-a", new CausalMarketEvent(
            new Trade(eventId, Instrument, Utc(minutes), new Price(100m + sequence),
                new Quantity(1m), TradeSide.Buy), Utc(minutes), Utc(minutes), sequence));

    private static UtcTimestamp Utc(int minutes) =>
        new(BaseTime.AddMinutes(minutes));
}
