using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class SignalOutcomeObserverTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-09T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset At(int minute) => Origin.AddMinutes(minute);

    [Fact]
    public void PublicationInsideBarExcludesEntireBarAndRetainsIndependentDecisionOutcome()
    {
        var (signal, evidence) = Fixture();
        SignalOutcomePolicy policy = new("label-v1", At(45), null, null, 110m, 90m);
        SignalOutcomeBar[] bars = [Bar(15, 30, 100m, 120m, 80m, 100m),
            Bar(30, 45, 100m, 104m, 95m, 102m)];
        SignalObservedOutcome decision = SignalOutcomeObserver.Observe(signal, evidence,
            SignalObservationClock.Decision, null, null, policy, bars, At(46));
        SignalObservedOutcome published = SignalOutcomeObserver.Observe(signal, evidence,
            SignalObservationClock.ChannelPublished, "telegram", At(22), policy, bars, At(46));
        Assert.Equal(SignalPassage.Ambiguous, decision.Passage);
        Assert.Equal(SignalPassage.Neither, published.Passage);
        Assert.Equal(1, published.CoveredBars);
        Assert.Equal(-0.05m, published.Mae);
        Assert.Equal(0.04m, published.Mfe);
        Assert.Equal(0.02m, published.SignedReturn);
        Assert.NotEqual(decision.Identity, published.Identity);
    }

    [Fact]
    public void PublishedReturnUsesNextEligibleOpenNotDecisionReference()
    {
        var (signal, evidence) = Fixture();
        SignalOutcomePolicy policy = new("label-v1", At(45), null, null, null, null);
        SignalOutcomeBar[] bars = [Bar(15, 30, 100m, 120m, 100m, 120m),
            Bar(30, 45, 120m, 122m, 119m, 121m)];
        SignalObservedOutcome outcome = SignalOutcomeObserver.Observe(signal, evidence,
            SignalObservationClock.ChannelPublished, "telegram", At(22), policy,
            bars, At(46));
        Assert.Equal(120m, outcome.ObservationReferencePrice);
        Assert.Equal(At(30), outcome.ObservationReferenceAt);
        Assert.Equal(1m / 120m, outcome.SignedReturn);
        Assert.Equal(-1m / 120m, outcome.Mae);
    }

    [Fact]
    public void DetailedPathResolvesSameBarButUndetailedBarRetainsBounds()
    {
        var (signal, evidence) = Fixture();
        SignalOutcomePolicy policy = new("label-v1", At(30), null, null, 110m, 90m);
        SignalOutcomeBar ambiguous = Bar(15, 30, 100m, 112m, 88m, 101m);
        SignalObservedOutcome unknownOrder = Observe(signal, evidence, policy, [ambiguous]);
        Assert.Equal(SignalPassage.Ambiguous, unknownOrder.Passage);
        Assert.Null(unknownOrder.FirstPassageAt);
        Assert.Equal(At(15), unknownOrder.FirstPassageEarliest);
        Assert.Equal(At(30), unknownOrder.FirstPassageLatest);
        SignalOutcomeBar detailed = ambiguous with
        {
            DetailedPath =
            [new(At(15), 100m), new(At(18), 88m), new(At(22), 112m), new(At(30), 101m)]
        };
        SignalObservedOutcome resolved = Observe(signal, evidence, policy, [detailed]);
        Assert.Equal(SignalPassage.InvalidationFirst, resolved.Passage);
        Assert.Equal(At(18), resolved.FirstPassageAt);
        Assert.Equal(At(18), resolved.FirstPassageEarliest);
        Assert.Equal(At(18), resolved.FirstPassageLatest);
        Assert.NotEqual(unknownOrder.CanonicalHash, resolved.CanonicalHash);
    }

    [Fact]
    public void TieNoPathAndCensoringRemainExplicit()
    {
        var (signal, evidence) = Fixture();
        SignalOutcomePolicy policy = new("label-v1", At(45), null, null, 110m, 90m);
        SignalObservedOutcome tie = Observe(signal, evidence, policy,
            [Bar(15, 30, 100m, 103m, 97m, 100m), Bar(30, 45, 100m, 103m, 97m, 100m)]);
        Assert.Equal(0m, tie.SignedReturn);
        Assert.Equal(SignalPassage.Neither, tie.Passage);
        SignalObservedOutcome missing = Observe(signal, evidence, policy, []);
        Assert.Equal(SignalObservationState.MissingPath, missing.State);
        Assert.Null(missing.SignedReturn);
        SignalObservedOutcome gap = Observe(signal, evidence, policy,
            [Bar(15, 30, 100m, 103m, 97m, 100m)]);
        Assert.Equal(SignalObservationState.Censored, gap.State);
        Assert.Equal("path-ends-before-horizon", gap.Reason);
        Assert.Null(gap.SignedReturn);
        SignalObservedOutcome noReceipt = SignalOutcomeObserver.Observe(signal, evidence,
            SignalObservationClock.ChannelPublished, "telegram", null, policy,
            [Bar(15, 30, 100m, 103m, 97m, 100m)], At(46));
        Assert.Equal(SignalObservationState.UnknownPublication, noReceipt.State);
        Assert.Equal(SignalPassage.Unknown, noReceipt.Passage);
    }

    [Fact]
    public void ExpiryOverlapAndReopenDeduplicateWithoutOverwritingChangedPath()
    {
        var (first, firstEvidence) = Fixture();
        var (second, secondEvidence) = Fixture(SetupType.CenterRetest);
        SignalOutcomePolicy policy = new("label-v1", At(60), At(45), null, 110m, 90m);
        SignalOutcomeBar[] bars = [Bar(15, 30, 100m, 102m, 98m, 101m),
            Bar(30, 45, 101m, 103m, 97m, 99m)];
        SignalObservedOutcome a = Observe(first, firstEvidence, policy, bars);
        SignalObservedOutcome b = Observe(second, secondEvidence, policy, bars);
        Assert.Equal(At(45), a.EffectiveEnd);
        Assert.NotEqual(a.Identity, b.Identity);
        string path = Path.Combine(Path.GetTempPath(), "outcome-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(SignalOutcomeJournal.Append(path, a));
            Assert.True(SignalOutcomeJournal.Append(path, b));
            Assert.Equal(2, SignalOutcomeJournal.ReadAll(path).Count);
            Assert.False(SignalOutcomeJournal.Append(path, Observe(first, firstEvidence, policy, bars)));
            SignalOutcomeBar[] revised = [bars[0], bars[1] with { High = 104m }];
            Assert.Throws<InvalidOperationException>(() => SignalOutcomeJournal.Append(path,
                Observe(first, firstEvidence, policy, revised)));
            Assert.Equal(2, SignalOutcomeJournal.ReadAll(path).Count);
            string stored = Path.Combine(path, a.Identity + ".json");
            string original = File.ReadAllText(stored);
            File.WriteAllText(stored, original.Replace("\"coveredBars\":2",
                "\"coveredBars\":1", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => SignalOutcomeJournal.ReadAll(path));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    private static SignalObservedOutcome Observe(SignalRecord signal,
        SignalEvidenceRecord evidence, SignalOutcomePolicy policy,
        IEnumerable<SignalOutcomeBar> bars) => SignalOutcomeObserver.Observe(signal,
            evidence, SignalObservationClock.Decision, null, null, policy, bars, At(90));

    private static SignalOutcomeBar Bar(int open, int close, decimal o, decimal h,
        decimal l, decimal c) => new(At(open), At(close), At(close), o, h, l, c);

    private static (SignalRecord, SignalEvidenceRecord) Fixture(
        SetupType family = SetupType.BoundaryRejection)
    {
        SignalIdentity identity = SignalIdentity.Create(new InstrumentId("BTCUSDT"),
            "BINANCE_USDM", "15m", Direction.Long, family, "profile-v1",
            family == SetupType.BoundaryRejection ? "boundary-rejection-v1" : "center-retest-v1",
            "anchor-1");
        UtcTimestamp setup = new(At(15));
        UtcTimestamp context = new(At(0));
        SignalProvenance provenance = SignalProvenance.Create("profile-v1", new string('a', 64),
            new CausalEventCursor(setup, 20), new CausalEventCursor(context, 10),
            [new SignalInputReference("public", "native-v1", "anchor-1", new string('c', 64),
                SignalInputRole.Setup, "15m", setup, setup, 20),
             new SignalInputReference("public", "native-v1", "context-1", new string('d', 64),
                SignalInputRole.HigherTimeframe, "1h", context, context, 10)]);
        SignalEvidenceRecord evidence = SignalEvidenceRecord.Create(identity,
            provenance, SignalEvidenceState.Complete, ["fixture-quality"]);
        SignalRecord signal = SignalRecord.Create(identity, evidence,
            SignalAssessmentState.ConfirmedOnClosedBar,
            new SignalBarInterval(new UtcTimestamp(At(0)), setup), setup,
            new SignalReferencePrice("public", "native-v1", "anchor-1", setup, new Price(100m)));
        return (signal, evidence);
    }
}
