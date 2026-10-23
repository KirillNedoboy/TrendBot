using System.Text.Json;
using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class PromotionCriteriaTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void CriteriaAreManifestBoundImmutableAndDeterministic()
    {
        FrozenResearchManifest manifest = CreateManifest();
        List<string> evidence = ["canonical report", "causal replay trace"];
        List<PromotionCriterion> criteria =
        [
            new("criterion-b", "Was replay causal?", evidence),
            new("criterion-a", "Was input frozen?", ["manifest identity"])
        ];

        PredeclaredPromotionCriteria declaration = PredeclaredPromotionCriteria.Create(
            manifest, criteria, ["profitability evidence is unresolved"]);
        evidence.Clear();
        criteria.Clear();

        PredeclaredPromotionCriteria permuted = PredeclaredPromotionCriteria.Create(
            manifest,
            [
                new("criterion-a", "Was input frozen?", ["manifest identity"]),
                new("criterion-b", "Was replay causal?", ["causal replay trace",
                    "canonical report"])
            ], ["profitability evidence is unresolved"]);

        Assert.Equal(manifest.Identity, declaration.ManifestIdentity);
        Assert.Equal(declaration.Identity, permuted.Identity);
        Assert.False(declaration.IsReady);
        Assert.Single(declaration.UnresolvedBlockers);
        Assert.Equal(2, declaration.Criteria.Count);
    }

    [Fact]
    public void CriteriaRoundTripRejectsTamperingAndJournalIsWriteOnce()
    {
        FrozenResearchManifest manifest = CreateManifest();
        PredeclaredPromotionCriteria declaration = PredeclaredPromotionCriteria.Create(
            manifest, [new("criterion-a", "Was input frozen?", ["manifest identity"])], []);
        PredeclaredPromotionCriteria restored = PredeclaredPromotionCriteria.Deserialize(
            declaration.CanonicalJson);

        Assert.Equal(declaration.CanonicalJson, restored.CanonicalJson);
        Assert.Equal(declaration.Identity, restored.Identity);
        Assert.Throws<JsonException>(() => PredeclaredPromotionCriteria.Deserialize(
            declaration.CanonicalJson.Replace(declaration.Identity, new string('0', 64),
                StringComparison.Ordinal)));

        PredeclaredPromotionCriteriaJournal journal = new();
        journal.Register(declaration);
        journal.Register(declaration);
        Assert.Throws<InvalidOperationException>(() => journal.Register(
            PredeclaredPromotionCriteria.Create(manifest,
                [new("criterion-other", "A different predeclared question?",
                    ["different evidence"])], [])));
    }

    [Fact]
    public void EvaluationContextRequiresReadyManifestAndCriteriaBeforeReplay()
    {
        FrozenResearchManifest manifest = CreateManifest();
        ReplayEnvelope[] events = manifest.Events.ToArray();
        FrozenResearchEvaluationContext context =
            new(manifest);

        Assert.Throws<InvalidOperationException>(() =>
        {
            context.RunReplay("stream-a", events, static _ => { });
        });

        PredeclaredPromotionCriteria unresolved = PredeclaredPromotionCriteria.Create(
            manifest, [new("criterion-a", "Was input frozen?", ["manifest identity"])],
            ["required OOS evidence is unresolved"]);
        context.RegisterCriteria(unresolved);
        Assert.False(context.IsReady);
        Assert.Throws<InvalidOperationException>(() =>
        {
            context.RunReplay("stream-a", events, static _ => { });
        });

        PredeclaredPromotionCriteria ready = PredeclaredPromotionCriteria.Create(
            manifest, [new("criterion-a", "Was input frozen?", ["manifest identity"])], []);
        FrozenResearchEvaluationContext readyContext =
            new(manifest, ready);
        ReplayRunResult run = readyContext.RunReplay("stream-a", events,
            static value => value.Event.EventId);
        BacktestReport report = BacktestReport.Create("run-ready", run);
        FrozenResearchEvaluationSnapshot snapshot = readyContext.Capture(report);

        Assert.True(readyContext.IsReady);
        Assert.Equal(ready.Identity, snapshot.CriteriaIdentity);
        FrozenResearchManifest foreignManifest = FrozenResearchManifest.Create(
            "other-fixture", "dataset-v1", "synthetic fixture", ["bars"], ["none"],
            events, "config-v1", [new("dataset", "fixture-v1")],
            [new("selection", "frozen")], "risk-v1", "fill-v1",
            ["synthetic fixture only"]);
        PredeclaredPromotionCriteria foreignCriteria = PredeclaredPromotionCriteria.Create(
            foreignManifest, [new("criterion-a", "Was input frozen?", ["manifest identity"])],
            []);
        Assert.Throws<FrozenResearchManifestMismatchException>(() =>
        {
            FrozenResearchEvaluationSnapshot.Create(manifest, foreignCriteria, report);
        });
    }

    private static FrozenResearchManifest CreateManifest() => FrozenResearchManifest.Create(
        "oos-fixture", "dataset-v1", "synthetic fixture", ["bars"], ["none"],
        [Envelope("event-1", 0, 1), Envelope("event-2", 1, 2)], "config-v1",
        [new("dataset", "fixture-v1")], [new("selection", "frozen")],
        "risk-v1", "fill-v1", ["synthetic fixture only"]);

    private static ReplayEnvelope Envelope(string eventId, int minutes, long sequence) =>
        new("stream-a", new CausalMarketEvent(
            new Trade(eventId, Instrument, Utc(minutes), new Price(100m + sequence),
                new Quantity(1m), TradeSide.Buy), Utc(minutes), Utc(minutes), sequence));

    private static UtcTimestamp Utc(int minutes) =>
        new(BaseTime.AddMinutes(minutes));
}
