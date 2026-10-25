using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class FrozenOosEvaluationTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void RepeatedEvaluationPreservesFrozenIdsAndProducesTheSameReport()
    {
        EvaluationFixture fixture = CreateFixture();

        FrozenOosEvaluationReport first = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            fixture.Records, "config-v1", fixture.Configuration,
            fixture.Models, fixture.Assessments, fixture.Reconciliation,
            static _ => "stable trace");
        FrozenOosEvaluationReport repeated = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            fixture.Records, "config-v1", fixture.Configuration,
            fixture.Models, fixture.Assessments, fixture.Reconciliation,
            static _ => "stable trace");

        Assert.Equal(fixture.Research.Identity, first.ResearchManifestIdentity);
        Assert.Equal(fixture.Criteria.Identity, first.CriteriaIdentity);
        Assert.Equal(fixture.Dataset.Identity, first.MultiSourceManifestIdentity);
        Assert.Equal(FrozenOosEvaluationStatus.Evaluated, first.Status);
        Assert.Equal("quality-proof", Assert.Single(first.QualityEvidence)
            .QualityEvidenceIdentity);
        Assert.Equal(["none"], first.Exclusions);
        Assert.Equal(first.CanonicalJson, repeated.CanonicalJson);
        Assert.Equal(first.Identity, repeated.Identity);
        Assert.Equal(first.CanonicalJson,
            FrozenOosEvaluationReport.Deserialize(first.CanonicalJson).CanonicalJson);
        Assert.Equal("trade-1", Assert.Single(first.Trace).EventId);
        Assert.Equal(1, Assert.Single(first.Trace).StableSequence);
        Assert.Equal(Utc(0), Assert.Single(first.Trace).EventTime);
    }

    [Fact]
    public void ChangedPayloadInvalidatesTheEvaluationBeforeTraceProjection()
    {
        EvaluationFixture fixture = CreateFixture();
        ReplayEnvelope original = (ReplayEnvelope)fixture.Records[0].Payload;
        ReplayEnvelope changed = Envelope("trade-1", 0, 1, price: 102m);
        MultiSourceReplayRecord changedRecord = new(fixture.Records[0].Ordering, changed);
        bool projectionCalled = false;

        FrozenOosEvaluationReport report = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            [changedRecord], "config-v1", fixture.Configuration,
            fixture.Models, fixture.Assessments, fixture.Reconciliation, _ =>
            {
                projectionCalled = true;
                return "changed";
            });

        Assert.Equal(FrozenOosEvaluationStatus.Invalidated, report.Status);
        Assert.Contains(report.Blockers, blocker => blocker.Contains("input",
            StringComparison.OrdinalIgnoreCase));
        Assert.False(projectionCalled);
        Assert.Equal(original.CausalEvent.Payload.EventId,
            fixture.Research.Events[0].CausalEvent.Payload.EventId);
    }

    [Fact]
    public void ChangedModelVersionInvalidatesTheEvaluationAndRetainsFrozenIdentity()
    {
        EvaluationFixture fixture = CreateFixture();
        FrozenOosModelVersions changedModels = new([new("strategy", "strategy-v2")]);
        bool projectionCalled = false;

        FrozenOosEvaluationReport report = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            fixture.Records, "config-v1", fixture.Configuration,
            changedModels, fixture.Assessments, fixture.Reconciliation, _ =>
            {
                projectionCalled = true;
                return "changed";
            });

        Assert.Equal(FrozenOosEvaluationStatus.Invalidated, report.Status);
        Assert.Equal(fixture.Research.Identity, report.ResearchManifestIdentity);
        Assert.Contains(report.Blockers, blocker => blocker.Contains("strategy",
            StringComparison.Ordinal));
        Assert.False(projectionCalled);
    }

    [Fact]
    public void UnresolvedCriteriaBlockReplayAndRemainExplicit()
    {
        EvaluationFixture fixture = CreateFixture(unresolvedCriteria: true);
        bool projectionCalled = false;

        FrozenOosEvaluationReport report = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            fixture.Records, "config-v1", fixture.Configuration,
            fixture.Models, [], fixture.Reconciliation, _ =>
            {
                projectionCalled = true;
                return "should not run";
            });

        Assert.Equal(FrozenOosEvaluationStatus.Blocked, report.Status);
        Assert.Contains("criteria-not-ready", report.Blockers);
        Assert.False(projectionCalled);
    }

    [Fact]
    public void IncompleteQualityAndMissingReconciliationRemainBlockers()
    {
        EvaluationFixture fixture = CreateFixture(partialQuality: true);
        FrozenOosEvaluationReport report = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            fixture.Records, "config-v1", fixture.Configuration,
            fixture.Models, fixture.Assessments, null, static _ => "trace");

        Assert.Equal(FrozenOosEvaluationStatus.Blocked, report.Status);
        Assert.NotEmpty(report.QualityReasons);
        Assert.Contains("reconciliation-unknown", report.Blockers);
        Assert.Null(report.Reconciliation);
    }

    [Fact]
    public void MissingQualityEvidenceIdentityBlocksAnOtherwiseReadySource()
    {
        EvaluationFixture fixture = CreateFixture(missingQualityEvidence: true);
        FrozenOosEvaluationReport report = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            fixture.Records, "config-v1", fixture.Configuration,
            fixture.Models, fixture.Assessments, fixture.Reconciliation, static _ => "trace");

        Assert.Equal(FrozenOosEvaluationStatus.Blocked, report.Status);
        Assert.Contains("data-quality:quality-evidence-missing:trades",
            report.Blockers);
        Assert.Null(Assert.Single(report.QualityEvidence).QualityEvidenceIdentity);
    }

    [Fact]
    public void UnknownModelVersionIsRetainedAndBlocksAResolvedEvaluation()
    {
        EvaluationFixture fixture = CreateFixture(unknownModelVersion: true);
        FrozenOosEvaluationReport report = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            fixture.Records, "config-v1", fixture.Configuration,
            fixture.Models, fixture.Assessments, fixture.Reconciliation, static _ => "trace");

        Assert.Equal(FrozenOosEvaluationStatus.Blocked, report.Status);
        Assert.Contains("model-version-unknown:execution", report.Blockers);
        Assert.Null(report.CurrentModelVersions.Versions.Single(static value =>
            value.Component == "execution").Version);
        Assert.Null(FrozenOosEvaluationReport.Deserialize(report.CanonicalJson)
            .CurrentModelVersions.Versions.Single(static value =>
                value.Component == "execution").Version);
    }

    [Fact]
    public void ConfigurationChangeInvalidatesTheEvaluationBeforeReplay()
    {
        EvaluationFixture fixture = CreateFixture();
        bool projectionCalled = false;

        FrozenOosEvaluationReport report = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            fixture.Records, "config-v1",
            [new("profile", "changed")], fixture.Models, fixture.Assessments,
            fixture.Reconciliation, _ =>
            {
                projectionCalled = true;
                return "changed";
            });

        Assert.Equal(FrozenOosEvaluationStatus.Invalidated, report.Status);
        Assert.False(projectionCalled);
        Assert.Equal(fixture.Research.Identity, report.ResearchManifestIdentity);
    }

    [Fact]
    public void ChangedCriteriaIdentityInvalidatesThePriorEvaluation()
    {
        EvaluationFixture fixture = CreateFixture();
        PredeclaredPromotionCriteria changedCriteria = PredeclaredPromotionCriteria.Create(
            fixture.Research, [new("changed-question", "A different predeclared question?",
                ["trace"])], []);

        FrozenOosEvaluationReport report = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, changedCriteria, fixture.Dataset,
            fixture.Records, "config-v1", fixture.Configuration, fixture.Models,
            fixture.Assessments, fixture.Reconciliation, static _ => "trace");

        Assert.Equal(FrozenOosEvaluationStatus.Invalidated, report.Status);
        Assert.Equal(fixture.Criteria.Identity, report.CriteriaIdentity);
    }

    [Fact]
    public void ChangedMultiSourceManifestIdentityInvalidatesThePriorEvaluation()
    {
        EvaluationFixture fixture = CreateFixture();
        MultiSourceDatasetManifest changedDataset = MultiSourceDatasetManifest.Create(
            "changed-dataset", fixture.Dataset.Sources, fixture.Dataset.Ordering);

        FrozenOosEvaluationReport report = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, changedDataset,
            fixture.Records, "config-v1", fixture.Configuration, fixture.Models,
            fixture.Assessments, fixture.Reconciliation, static _ => "trace");

        Assert.Equal(FrozenOosEvaluationStatus.Invalidated, report.Status);
        Assert.Equal(fixture.Dataset.Identity, report.MultiSourceManifestIdentity);
    }

    [Fact]
    public void ExactDuplicateInputRemainsVisibleInTheEvaluationReport()
    {
        EvaluationFixture fixture = CreateFixture();
        MultiSourceReplayRecord[] duplicateRecords =
            [fixture.Records[0], fixture.Records[0]];
        FrozenOosEvaluationDefinition definition = FrozenOosEvaluationDefinition.Create(
            fixture.Research, fixture.Criteria, fixture.Dataset, duplicateRecords,
            fixture.Models);

        FrozenOosEvaluationReport report = FrozenOosEvaluator.Evaluate(
            definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            duplicateRecords, "config-v1", fixture.Configuration, fixture.Models,
            fixture.Assessments, fixture.Reconciliation, static _ => "trace");

        Assert.Equal(FrozenOosEvaluationStatus.Evaluated, report.Status);
        Assert.Equal(MultiSourceReplayDiagnosticKind.Duplicate,
            Assert.Single(report.ReplayDiagnostics).Kind);
        Assert.Single(report.Trace);
        Assert.Equal(report.CanonicalJson,
            FrozenOosEvaluationReport.Deserialize(report.CanonicalJson).CanonicalJson);
    }

    private static EvaluationFixture CreateFixture(bool unresolvedCriteria = false,
        bool partialQuality = false, bool missingQualityEvidence = false,
        bool unknownModelVersion = false)
    {
        ReplayEnvelope envelope = Envelope("trade-1", 0, 1);
        MultiSourceReplayRecord record = Record(envelope);
        BacktestConfigurationEntry[] configuration = [new("profile", "fixed")];
        FrozenResearchManifest research = FrozenResearchManifest.Create(
            "oos-fixture", "dataset-v1", "synthetic fixture", ["trades"], ["none"],
            [envelope], "config-v1", configuration, [new("selection", "fixed")],
            "risk-v1", "fill-v1", ["synthetic only"]);
        PredeclaredPromotionCriteria criteria = PredeclaredPromotionCriteria.Create(
            research, [new("causal-replay", "Is the replay causal?", ["trace"])],
            unresolvedCriteria ? ["criteria are not predeclared"] : []);
        MultiSourceCoverage coverage = partialQuality
            ? MultiSourceCoverage.Partial : MultiSourceCoverage.Complete;
        MultiSourceQuality quality = partialQuality
            ? MultiSourceQuality.Degraded : MultiSourceQuality.Ready;
        MultiSourceDatasetManifest dataset = MultiSourceDatasetManifest.Create(
            "dataset-fixture",
            [new("trades", MultiSourceLaneKind.Trades, "source-manifest",
                missingQualityEvidence ? null : "quality-proof",
                [], coverage, quality, partialQuality ? ["gap:fixture"] : [], 1,
                Utc(0), Utc(0), Utc(0), Utc(0), 1, 1)],
            [new("trades", "trade-1", 1, 1, Utc(0), null, Utc(0), Utc(0))]);
        FrozenOosModelVersions models = unknownModelVersion
            ? new([new("strategy", "strategy-v1"), new("execution", null)])
            : new([new("strategy", "strategy-v1")]);
        FrozenOosEvaluationDefinition definition = FrozenOosEvaluationDefinition.Create(
            research, criteria, dataset, [record], models);
        FrozenOosCriterionAssessment[] assessments =
        [
            new("causal-replay", FrozenOosCriterionStatus.Supported,
                [new("trace", "trace-evidence-sha256")], [])
        ];
        ReconciliationCheckpoint reconciliation = new(new PortfolioId("fixture"), 1,
            [], [], [], [], ReconciliationStatus.Acknowledged);
        return new EvaluationFixture(research, criteria, dataset, definition, [record],
            configuration, models, assessments, reconciliation);
    }

    private sealed record EvaluationFixture(FrozenResearchManifest Research,
        PredeclaredPromotionCriteria Criteria, MultiSourceDatasetManifest Dataset,
        FrozenOosEvaluationDefinition Definition,
        IReadOnlyList<MultiSourceReplayRecord> Records,
        IReadOnlyList<BacktestConfigurationEntry> Configuration,
        FrozenOosModelVersions Models,
        IReadOnlyList<FrozenOosCriterionAssessment> Assessments,
        ReconciliationCheckpoint Reconciliation);

    private static MultiSourceReplayRecord Record(ReplayEnvelope envelope) =>
        new(new MultiSourceReplayOrderEntry("trades", envelope.CausalEvent.Payload.EventId,
            envelope.CausalEvent.StableSequence, 1, envelope.CausalEvent.Payload.EventTime,
            envelope.CausalEvent.ExchangeTime, envelope.CausalEvent.ReceiveTime,
            envelope.CausalEvent.AvailabilityTime), envelope);

    private static ReplayEnvelope Envelope(string eventId, int minutes, long sequence,
        decimal? price = null) =>
        new("trades", new CausalMarketEvent(new Trade(eventId, Instrument, Utc(minutes),
            new Price(price ?? 100m + sequence), new Quantity(1m), TradeSide.Buy), Utc(minutes),
            Utc(minutes), sequence));

    private static UtcTimestamp Utc(int minutes) => new(BaseTime.AddMinutes(minutes));
}
