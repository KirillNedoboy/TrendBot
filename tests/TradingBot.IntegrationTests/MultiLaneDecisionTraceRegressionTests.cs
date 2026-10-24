using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class MultiLaneDecisionTraceRegressionTests
{
    [Fact]
    public void ReplayResultExposesReadinessReasonsForDownstreamQualityGate()
    {
        Assert.NotNull(typeof(MultiSourceReplayResult).GetProperty("ReadinessReasons"));
    }

    [Fact]
    public async Task PersistedDatasetProducesTheSameFullDecisionTraceAndChangedInputChangesIt()
    {
        MultiLaneTrace first = await RunTrace(0.01m, degraded: false);
        MultiLaneTrace repeated = await RunTrace(0.01m, degraded: false);
        MultiLaneTrace changed = await RunTrace(-0.01m, degraded: false);

        Assert.True(first.DecisionOutcome == DecisionOutcome.Allow,
            $"outcome={first.DecisionOutcome}; lifecycle={first.LifecycleState}; "
                + $"laneB={first.LaneBState}; laneC={first.LaneCState}; "
                + $"feature={first.FeatureState}:{string.Join(',', first.FeatureReasons)}; "
                + $"reasons={string.Join(',', first.DecisionReasons)}");
        Assert.Equal(RiskDecisionOutcome.Approved, first.RiskOutcome);
        Assert.Equal(first.CanonicalJson, repeated.CanonicalJson);
        Assert.NotEqual(first.LaneBIdentity, changed.LaneBIdentity);
        Assert.NotEqual(first.DecisionOutcome, changed.DecisionOutcome);
        Assert.Equal(RiskMathVersion, first.RiskMathVersion);
        Assert.NotEmpty(first.ResearchFills);
    }

    [Fact]
    public async Task DegradedCoverageAndUnavailableFeaturesRemainBlockedWithReasons()
    {
        MultiLaneTrace trace = await RunTrace(0.01m, degraded: true);

        Assert.Equal(DecisionOutcome.Block, trace.DecisionOutcome);
        Assert.Contains("source-coverage:capture:Partial", trace.Reasons);
        Assert.Contains("source-quality:capture:Degraded:capture-quality-degraded",
            trace.Reasons);
        Assert.Contains(trace.DecisionReasons,
            reason => reason.Contains("lane-c-unknown", StringComparison.Ordinal));
        Assert.Equal(LaneCFeatureState.Ready.ToString(), trace.FeatureState);
    }

    private const string RiskMathVersion = "linear-perpetual-risk-v1";
    private static readonly DateTimeOffset BaseTime =
        TradingBot.Core.Tests.LaneASetupFixtures.BaseTime.AddMinutes(3);
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly PortfolioId Portfolio = new("fixture-portfolio");

    private static async Task<MultiLaneTrace> RunTrace(decimal fundingRate, bool degraded)
    {
        using TemporaryDirectory directory = new();
        CaptureEnvelope[] captures = Captures();
        CapturePersistenceOptions persistence = new() { StreamId = "capture" };
        await using (ParquetCaptureStore store = ParquetCaptureStore.Open(
            Path.Combine(directory.Path, "capture.db"),
            Path.Combine(directory.Path, "segments"), persistence))
        {
            foreach (CaptureEnvelope capture in captures)
            {
                await store.AppendAsync(capture, TestContext.Current.CancellationToken);
            }
            await store.FlushAsync(TestContext.Current.CancellationToken);
        }

        await using ParquetCaptureStore reopened = ParquetCaptureStore.Open(
            Path.Combine(directory.Path, "capture.db"),
            Path.Combine(directory.Path, "segments"), persistence);
        CaptureEnvelope[] restoredCaptures = (await reopened.ReadAllAsync(
            TestContext.Current.CancellationToken)).ToArray();
        Assert.Equal(captures, restoredCaptures);

        LaneBFundingObservation funding = new("funding", new FundingUpdate("funding-1",
            Instrument, Utc(0), fundingRate), "rate", "periodic", "fixed-fixture",
            Utc(0), Utc(0), 0);
        MultiSourceDatasetManifest manifest = CreateManifest(restoredCaptures, funding,
            degraded);
        MultiSourceDatasetManifestStore manifestStore = new(
            Path.Combine(directory.Path, "dataset.manifest.json"));
        await manifestStore.PersistAsync(manifest, TestContext.Current.CancellationToken);
        manifest = await manifestStore.ReadAsync(TestContext.Current.CancellationToken);

        Dictionary<(string SourceId, string EventId), MultiSourceReplayOrderEntry> order =
            manifest.Ordering.ToDictionary(static entry => (entry.SourceId, entry.EventId));
        MultiSourceReplayRecord[] records = restoredCaptures.Select(capture =>
                new MultiSourceReplayRecord(order[("capture", capture.Payload!.EventId)], capture))
            .Append(new MultiSourceReplayRecord(order[("funding", funding.EventId)], funding))
            .ToArray();
        List<MultiSourceReplayStepContext> steps = [];
        MultiSourceReplayResult replay = MultiSourceReplayRunner.Run(manifest,
            records.Reverse(), steps.Add);
        Assert.True(replay.Succeeded);
        Assert.Equal(records.Length, steps.Count);

        CausalEventCursor cursor = new(new UtcTimestamp(BaseTime), 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(new LaneCFeatureInput(
            Instrument, restoredCaptures, cursor, "persisted-multilane-fixture"),
            new LaneCFeatureConfiguration("fixture-formulas-v1", TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 1, 16));
        LaneCShadowResult laneC = LaneCShadowEvaluator.Evaluate(features,
            new LaneCFeatureRuleTable("fixture-lane-c-rules-v1", [
                new LaneCFeatureRule("positive-book", "fixture-rule-v1",
                    [new LaneCFeatureRuleCondition("book-positive",
                        LaneCFeatureMetric.BookImbalance,
                        LaneCComparisonOperator.GreaterThan, 0m)],
                    LaneCShadowStatus.Confirm)]));

        LaneBInput laneBInput = new(Instrument, [], [funding], "persisted-fixture");
        LaneBInputConfiguration laneBInputConfiguration = new(
            "fixture-lane-b-input-v1", null, "rate", "periodic", false, true, false);
        LaneBRuleTable laneBRules = new("fixture-lane-b-rules-v1", [
            new LaneBRule("positive-funding", SetupType.BoundaryRejection,
                Direction.Long, "fixture-rule-v1", [new LaneBRuleCondition("rate-positive",
                    LaneBMetricKind.FundingRate, LaneBComparisonOperator.GreaterThan, 0m)],
                LaneBShadowStatus.Confirm)]);
        LaneBShadowResult laneB = new LaneBEvaluator(laneBRules).Evaluate(laneBInput, cursor,
            laneBInputConfiguration, [], [], SetupType.BoundaryRejection, Direction.Long);

        SetupEvidenceSnapshot laneA = TradingBot.Core.Tests.LifecycleInputFixtures.CreateEvidence(
            TradingBot.Core.Tests.LaneASetupFixtures.Cases.Single(static fixture =>
                fixture.Name == "both-long-ready"), boundary: true, Direction.Long);
        SetupLifecycleConfiguration lifecycleConfiguration = new("fixture-lifecycle-v1",
            "fixture-lifecycle-config-v1", requireExternalPlan: false,
            requireConfirmationWindow: false);
        SetupLifecycleIdentity lifecycleIdentity = SetupLifecycleIdentity.Create(Instrument,
            SetupType.BoundaryRejection, Direction.Long, "fixture-anchor",
            new UtcTimestamp(BaseTime), "fixture-context-v1");
        SetupLifecycleEngine lifecycleEngine = new(lifecycleConfiguration);
        SetupLifecycleSnapshot lifecycle = SetupLifecycleSnapshot.CreateInitial(
            lifecycleIdentity, lifecycleConfiguration);
        List<SetupLifecycleTransition> transitions = [];
        lifecycle = Apply(SetupLifecycleFactKind.ContextReady, 10, lifecycle, null, null);
        lifecycle = Apply(SetupLifecycleFactKind.StructureFormed, 10, lifecycle, null, null);
        lifecycle = Apply(SetupLifecycleFactKind.SetupEvidence, 10, lifecycle, laneA, null);
        SetupLifecycleConfirmation confirmation = new(SetupConfirmationLane.LaneB,
            SetupConfirmationStatus.Confirm, LaneBIdentity(laneB), laneB.AlgorithmVersion,
            cursor, "fixture-lane-b");
        lifecycle = Apply(SetupLifecycleFactKind.Confirmation, 10, lifecycle, null,
            confirmation);
        Assert.Equal(laneA.Cursor, lifecycle.ProcessedCursor);
        Assert.Equal(cursor, lifecycle.ProcessedCursor);

        DecisionEvidence laneBEvidence = new(DecisionLane.LaneB, laneB.EvidenceStatus,
            laneB.RuleId ?? "lane-b-unknown", laneB.AlgorithmVersion, cursor,
            "persisted-fixture");
        DecisionEvidence laneCEvidence = replay.DecisionReadinessConfirmed
            ? DecisionEvidence.FromLaneCShadow(laneC, "persisted-fixture")
            : new DecisionEvidence(DecisionLane.LaneC, DecisionEvidenceStatus.Unknown,
                "capture-quality-block", "dataset-quality-v1", cursor,
                string.Join(";", replay.ReadinessReasons));
        DecisionEngineConfiguration decisionConfiguration = new("fixture-decision-v1",
            "fixture-decision-config-v1", DecisionEngineProfile.Allow,
            requireLaneB: true, requireLaneC: true, allowNeutralLaneB: false,
            allowNeutralLaneC: false);
        DecisionEngineResult decision = DecisionEngine.Evaluate(new DecisionEngineInput(
            $"decision-{fundingRate}", lifecycle, laneA, [laneBEvidence, laneCEvidence],
            "persisted-multilane-fixture"), decisionConfiguration);

        DecisionProjectionSnapshot projection = DecisionProjectionSnapshot.Create(decision,
            lifecycleConfiguration, decisionConfiguration);
        RiskMathInput math = new(Instrument, Direction.Long, new Money(1_000m, "USDT"),
            0.01m, new Price(100m), new Price(95m),
            new MoneyPerQuantityUnit(0m, "USDT"),
            new MoneyPerQuantityUnit(0m, "USDT"));
        RiskEvaluationInput riskInput = new("fixture-risk", projection, math, Portfolio,
            cursor, new RiskPortfolioUsage(Portfolio, new Money(0m, "USDT"),
                new Money(0m, "USDT"), new Money(0m, "USDT"), 0, "usage", "fixture-v1",
                cursor, "fixture"), new Money(0m, "USDT"), "fixture-period",
            new Money(100m, "USDT"), 1,
            RiskHealthEvidence.Healthy("account", "fixture-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("portfolio", "fixture-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("market", "fixture-v1", cursor, "fixture"),
            RiskKillSwitchEvidence.Clear("kill", "fixture-v1", cursor, "fixture"),
            "persisted-multilane-fixture",
            protection: new RiskProtectionEvidence(Instrument, Direction.Long,
                decision.Decision.SetupId, "fixture-protection-v1", "fixture", "fixture-v1",
                RiskEvidenceFreshness.Fresh, cursor, new Price(95m), new Price(110m),
                new Price(120m), "synthetic research protection"),
            cooldown: RiskCooldownState.Clear(Instrument, "cooldown", "fixture-v1", cursor,
                "synthetic research cooldown"));
        RiskEvaluationConfiguration riskConfiguration = new(RiskEvaluationVersion.Current,
            new RiskMathVersion(RiskMathVersion), new RiskLimitConfiguration(
                new Money(100m, "USDT"), new Money(100m, "USDT"),
                new Money(100m, "USDT"), "fixture-period"),
            new PortfolioLimits(Portfolio, new Money(100m, "USDT"),
                new Money(10_000m, "USDT"), 2), "fixture-risk-config");
        RiskEvaluationResult risk = RiskEvaluationEngine.Evaluate(riskInput,
            riskConfiguration, new LinearPerpetualRiskMathCalculator());
        SimulatedRiskPlan plan = new("fixture-research-plan", Instrument, Direction.Long,
            cursor.AsOf, math, [], null, false, null,
            new SimulatedFillAssumptions(0m, 0m,
                new FillSimulationAssumptions("fixture fees", "fixture slippage",
                    "next available bar", "OHLCV only", "partial quantities preserved",
                    "stop before target", "synthetic research data"),
                new FillSimulationVersion("fixture-fill-assumptions-v1")), decision.Decision);
        ReplayEnvelope[] bars = Bars();
        SimulatedRiskSimulationResult simulation = new SimulatedRiskSimulator(
            new LinearPerpetualRiskMathCalculator()).Simulate(plan, bars);

        MultiLaneTrace trace = new(manifest.Identity, features.InputIdentity,
            features.State.ToString(), features.Reasons.ToArray(), laneA.State.ToString(),
            lifecycle.State.ToString(), transitions.Select(static transition =>
                transition.Status.ToString()).ToArray(), LaneBIdentity(laneB),
            laneB.Status.ToString(), laneC.ResultIdentity, laneC.Status.ToString(),
            decision.Decision.Outcome, decision.Reasons.ToArray(), risk.RiskDecision.Outcome,
            risk.Reasons.ToArray(), plan.ResearchPlanId,
            simulation.Fills.Select(static fill => $"{fill.Role}:{fill.Quantity.Value}:{fill.Price.Value}").ToArray(),
            replay.ReadinessReasons.ToArray(), RiskMathVersion, string.Empty);
        return trace with
        {
            CanonicalJson = CoreJsonSerializer.Serialize(trace with
            { CanonicalJson = string.Empty })
        };

        SetupLifecycleSnapshot Apply(SetupLifecycleFactKind kind, long sequence,
            SetupLifecycleSnapshot current, SetupEvidenceSnapshot? evidence,
            SetupLifecycleConfirmation? lifecycleConfirmation)
        {
            SetupLifecycleInput input = new(SetupLifecycleInput.CurrentFormatVersion,
                "fixture-lifecycle", $"{kind}-{sequence}", kind, lifecycleIdentity,
                new UtcTimestamp(BaseTime), new UtcTimestamp(BaseTime),
                new UtcTimestamp(BaseTime), sequence, lifecycleConfiguration.RulesVersion,
                lifecycleConfiguration.ConfigurationVersion, "persisted-fixture", evidence,
                lifecycleConfirmation);
            SetupLifecycleTransition transition = lifecycleEngine.Apply(current, input);
            transitions.Add(transition);
            return transition.NextSnapshot;
        }
    }

    private static CaptureEnvelope[] Captures()
    {
        CaptureDepthMetadata snapshotMetadata = new(100, 100, null, 1, true);
        CaptureDepthMetadata updateMetadata = new(101, 101, 100, 1, false);
        return [
            Capture(CaptureRecordKind.Depth, new BookDelta("book-snapshot", Instrument,
                Utc(-3), [new BookLevel(new Price(99m), new Quantity(8m))],
                [new BookLevel(new Price(101m), new Quantity(3m))]), 0,
                snapshotMetadata),
            Capture(CaptureRecordKind.Depth, new BookDelta("book-update", Instrument,
                Utc(-2), [new BookLevel(new Price(99m), new Quantity(9m))],
                [new BookLevel(new Price(101m), new Quantity(3m))]), 1, updateMetadata),
            Capture(CaptureRecordKind.Trade, new Trade("buy-trade", Instrument, Utc(-1),
                new Price(99m), new Quantity(3m), TradeSide.Buy), 2, null),
            Capture(CaptureRecordKind.Trade, new Trade("sell-trade", Instrument, Utc(-1),
                new Price(101m), new Quantity(1m), TradeSide.Sell), 3, null)
        ];

        CaptureEnvelope Capture(CaptureRecordKind kind, MarketEvent payload,
            long sequence, CaptureDepthMetadata? depth) =>
            CaptureEnvelope.FromMarketEvent("capture", sequence, payload, payload.EventTime,
                payload.EventTime, sequence,
                new CaptureProvenance("fixture", "capture", "session-a", payload.EventId,
                    null, new ImmutableValueList<string>(Array.Empty<string>()), depth));
    }

    private static string LaneBIdentity(LaneBShadowResult result) =>
        CoreJsonSerializer.Serialize(result);

    private static MultiSourceDatasetManifest CreateManifest(CaptureEnvelope[] captures,
        LaneBFundingObservation funding, bool degraded)
    {
        MultiSourceReplayOrderEntry[] order = captures.Select((capture, index) =>
                new MultiSourceReplayOrderEntry("capture", capture.Payload!.EventId,
                    capture.StableSequence, 6 + index, capture.EventTime,
                    capture.ExchangeTime, capture.ReceiveTime, capture.AvailabilityTime))
            .Append(new MultiSourceReplayOrderEntry("funding", funding.EventId, 0, 10,
                funding.EventTime, funding.ExchangeTime, funding.ReceiveTime,
                funding.AvailabilityTime)).ToArray();
        MultiSourceDatasetSource captureSource = new("capture", MultiSourceLaneKind.L2,
            "capture-source-manifest-v1", "capture-quality-evidence-v1", [],
            degraded ? MultiSourceCoverage.Partial : MultiSourceCoverage.Complete,
            degraded ? MultiSourceQuality.Degraded : MultiSourceQuality.Ready,
            degraded ? ["capture-quality-degraded"] : [], captures.Length,
            new UtcTimestamp(captures.Min(static row => row.EventTime.Value)),
            new UtcTimestamp(captures.Max(static row => row.EventTime.Value)),
            new UtcTimestamp(captures.Min(static row => row.AvailabilityTime.Value)),
            new UtcTimestamp(captures.Max(static row => row.AvailabilityTime.Value)),
            captures.Min(static row => row.StableSequence), captures.Max(static row => row.StableSequence));
        MultiSourceDatasetSource fundingSource = new("funding", MultiSourceLaneKind.Funding,
            "funding-source-manifest-v1", "funding-quality-evidence-v1", [],
            MultiSourceCoverage.Complete, MultiSourceQuality.Ready, [], 1,
            funding.EventTime, funding.EventTime, funding.AvailabilityTime,
            funding.AvailabilityTime, funding.StableSequence, funding.StableSequence);
        return MultiSourceDatasetManifest.Create("fixture-dataset-v1",
            [captureSource, fundingSource], order);
    }

    private static ReplayEnvelope[] Bars() => [
        Bar("signal", 0, 100m, 101m, 99m, 100m, 20_001),
        Bar("entry", 1, 100m, 101m, 99m, 100m, 20_002),
        Bar("exit", 2, 101m, 102m, 100m, 101m, 20_003)
    ];

    private static ReplayEnvelope Bar(string id, int minute, decimal open, decimal high,
        decimal low, decimal close, long sequence) => new("simulation", new CausalMarketEvent(
            new Bar(id, Instrument, Utc(minute), Utc(minute), Utc(minute + 1),
                new Price(open), new Price(high), new Price(low), new Price(close),
                new Quantity(1m)), Utc(minute + 1), Utc(minute + 1), sequence));

    private static UtcTimestamp Utc(int seconds) => new(BaseTime.AddSeconds(seconds));

    private sealed record MultiLaneTrace(string ManifestIdentity, string FeatureIdentity,
        string FeatureState, string[] FeatureReasons, string LaneAState,
        string LifecycleState, string[] LifecycleTransitions, string LaneBIdentity,
        string LaneBState, string LaneCIdentity, string LaneCState,
        DecisionOutcome DecisionOutcome, string[] DecisionReasons,
        RiskDecisionOutcome RiskOutcome, string[] RiskReasons, string ResearchPlanId,
        string[] ResearchFills, string[] Reasons, string RiskMathVersion,
        string CanonicalJson);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "tradingbot-multilane-regression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
