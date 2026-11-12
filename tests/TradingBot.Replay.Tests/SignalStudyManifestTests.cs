using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class SignalStudyManifestTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void ManifestSnapshotsCanonicalizesAndPersistsWriteOnce()
    {
        using TemporaryDirectory directory = new();
        List<SignalStudyHashDeclaration> inputs = CreateInputs();
        List<SignalStudyCriterionDeclaration> criteria = CreateCriteria();
        SignalStudyTrialRegistry trials = CreateTrialRegistry();
        SignalStudyTemporalSplitPlan splits = CreateSplitPlan();

        SignalStudyManifest manifest = SignalStudyManifest.Create("setup-baseline-v1",
            inputs, criteria, splits, trials);
        inputs.Reverse();
        criteria.Reverse();
        SignalStudyManifest permuted = SignalStudyManifest.Create("setup-baseline-v1",
            CreateInputs().AsEnumerable().Reverse(),
            CreateCriteria().AsEnumerable().Reverse(), CreateSplitPlan(),
            CreateTrialRegistry());

        Assert.Equal(SignalStudyDefinitionState.Ready, manifest.State);
        Assert.Equal(manifest.Identity, permuted.Identity);
        Assert.Equal(manifest.CanonicalJson, permuted.CanonicalJson);
        Assert.Equal(manifest.CanonicalJson,
            SignalStudyManifest.Deserialize(manifest.CanonicalJson).CanonicalJson);

        string path = Path.Combine(directory.Path, "study-manifest.json");
        SignalStudyManifestJournal.WriteOnce(manifest, path);
        SignalStudyManifestJournal.WriteOnce(manifest, path);
        Assert.Equal(manifest.Identity,
            SignalStudyManifestJournal.Read(path).Identity);

        SignalStudyManifest changedInputs = CreateManifest(
            configurationHash: Hash("changed-configuration"));
        Assert.NotEqual(manifest.Identity, changedInputs.Identity);
        Assert.Throws<SignalStudyManifestMismatchException>(() =>
            manifest.RequireEvidenceIdentity(changedInputs.Identity));
        Assert.Throws<InvalidOperationException>(() =>
            SignalStudyManifestJournal.WriteOnce(changedInputs, path));
    }

    [Fact]
    public void UnknownInputsAndCriteriaRemainUnknownAndBlockOutOfSampleAccess()
    {
        using TemporaryDirectory directory = new();
        List<SignalStudyHashDeclaration> inputs = CreateInputs();
        inputs[0] = SignalStudyHashDeclaration.Unknown(
            SignalStudyInputKind.Dataset, "dataset coverage is not frozen");
        List<SignalStudyCriterionDeclaration> criteria = CreateCriteria();
        criteria[2] = SignalStudyCriterionDeclaration.Unknown(
            SignalStudyCriterionKind.PrecisionPolicy,
            "required precision was not declared");
        SignalStudyManifest manifest = SignalStudyManifest.Create(
            "setup-baseline-v1", inputs, criteria,
            SignalStudyTemporalSplitPlan.Unknown("split assignments are not frozen"),
            SignalStudyTrialRegistry.Unknown("trial registry is not declared"));

        Assert.Equal(SignalStudyDefinitionState.Unknown, manifest.State);
        Assert.Contains(manifest.UnknownReasons, reason =>
            reason == "dataset coverage is not frozen");
        Assert.Contains(manifest.UnknownReasons, reason =>
            reason == "required precision was not declared");
        Assert.Throws<InvalidOperationException>(() =>
            new SignalStudyOutOfSampleViewJournal(directory.Path)
                .MarkViewed(manifest, At(60)));
    }

    [Fact]
    public void TemporalPlanUsesHalfOpenBoundariesAndPurgesForwardDependencyOverlap()
    {
        SignalStudyTemporalSplitPlan plan = CreateSplitPlan();

        Assert.Equal(2, plan.ForwardEmbargoIntervals.Count);
        Assert.Equal(At(20), plan.ForwardEmbargoIntervals[0].Interval.Start);
        Assert.Equal(At(22), plan.ForwardEmbargoIntervals[0].Interval.End);
        Assert.Equal(At(30), plan.ForwardEmbargoIntervals[1].Interval.Start);
        Assert.Equal(At(32), plan.ForwardEmbargoIntervals[1].Interval.End);
        Assert.Contains(plan.LabelDependencies, dependency =>
            dependency.OpportunityId == "train-next-fold"
                && dependency.IsPurged
                && dependency.ExclusionReason == "prior-validation-embargo");

        List<SignalStudyLabelDependency> unpurged = CreateLabelDependencies()
            .Where(dependency => dependency.OpportunityId != "train-next-fold")
            .Append(new SignalStudyLabelDependency("train-next-fold", "fold-2",
                SignalStudySplitRole.Training, At(21),
                new SignalStudyTemporalInterval(At(21), At(22)), At(22)))
            .ToList();
        Assert.Throws<ArgumentException>(() => SignalStudyTemporalSplitPlan.Create(
            CreateFolds(), new SignalStudyTemporalInterval(At(35), At(45)),
            unpurged));

        List<SignalStudyLabelDependency> crossingHeldOut = CreateLabelDependencies()
            .Append(new SignalStudyLabelDependency("crossing-validation", "fold-1",
                SignalStudySplitRole.Training, At(8),
                new SignalStudyTemporalInterval(At(19), At(21)), At(21)))
            .ToList();
        List<SignalStudyLabelDependency> crossingDevelopment =
            CreateLabelDependencies().Append(new SignalStudyLabelDependency(
                "crossing-development", "fold-1", SignalStudySplitRole.Training,
                At(9), new SignalStudyTemporalInterval(At(11), At(12)), At(12)))
            .ToList();
        Assert.Throws<ArgumentException>(() => SignalStudyTemporalSplitPlan.Create(
            CreateFolds(), new SignalStudyTemporalInterval(At(35), At(45)),
            crossingDevelopment));
        Assert.Throws<ArgumentException>(() => SignalStudyTemporalSplitPlan.Create(
            CreateFolds(), new SignalStudyTemporalInterval(At(35), At(45)),
            crossingHeldOut));
    }

    [Fact]
    public void ViewedOutOfSampleHashCannotBeReclassifiedByChangedManifest()
    {
        using TemporaryDirectory directory = new();
        SignalStudyOutOfSampleViewJournal journal =
            new(directory.Path);
        SignalStudyManifest original = CreateManifest();
        SignalStudyManifest changed = CreateManifest(
            configurationHash: Hash("changed-configuration"));
        SignalStudyManifest changedOutOfSample = CreateManifest(
            outOfSampleHash: Hash("new-out-of-sample-data"));

        Assert.Equal(SignalStudyOutOfSampleViewState.Unseen,
            journal.GetState(original));
        SignalStudyOutOfSampleViewRecord firstView =
            journal.MarkViewed(original, At(60));
        Assert.Equal(SignalStudyOutOfSampleViewState.ViewedByThisStudy,
            journal.GetState(original));
        Assert.Equal(firstView.CanonicalJson,
            journal.MarkViewed(original, At(70)).CanonicalJson);
        Assert.Equal(SignalStudyOutOfSampleViewState.ViewedByDifferentStudy,
            journal.GetState(changed));
        Assert.Throws<InvalidOperationException>(() =>
            journal.MarkViewed(changed, At(80)));
        Assert.Equal(SignalStudyOutOfSampleViewState.Unseen,
            journal.GetState(changedOutOfSample));
    }

    private static SignalStudyManifest CreateManifest(
        string? configurationHash = null, string? outOfSampleHash = null) =>
        SignalStudyManifest.Create("setup-baseline-v1",
            CreateInputs(configurationHash, outOfSampleHash), CreateCriteria(),
            CreateSplitPlan(), CreateTrialRegistry());

    private static List<SignalStudyHashDeclaration> CreateInputs(
        string? configurationHash = null, string? outOfSampleHash = null) =>
    [
        SignalStudyHashDeclaration.Declared(SignalStudyInputKind.Dataset,
            Hash("dataset")),
        SignalStudyHashDeclaration.Declared(
            SignalStudyInputKind.PointInTimeUniverse, Hash("pit-universe")),
        SignalStudyHashDeclaration.Declared(SignalStudyInputKind.Configuration,
            configurationHash ?? Hash("configuration")),
        SignalStudyHashDeclaration.Declared(SignalStudyInputKind.Formula,
            Hash("formula")),
        SignalStudyHashDeclaration.Declared(SignalStudyInputKind.SeedHistory,
            Hash("seed-history")),
        SignalStudyHashDeclaration.Declared(SignalStudyInputKind.Labels,
            Hash("labels")),
        SignalStudyHashDeclaration.Declared(SignalStudyInputKind.Horizons,
            Hash("horizons")),
        SignalStudyHashDeclaration.Declared(SignalStudyInputKind.CostModel,
            Hash("cost-model")),
        SignalStudyHashDeclaration.Declared(
            SignalStudyInputKind.LatencyAssumptions, Hash("latency")),
        SignalStudyHashDeclaration.Declared(SignalStudyInputKind.Exclusions,
            Hash("exclusions")),
        SignalStudyHashDeclaration.Declared(
            SignalStudyInputKind.OutOfSampleDataset,
            outOfSampleHash ?? Hash("oos-dataset"))
    ];

    private static List<SignalStudyCriterionDeclaration> CreateCriteria() =>
    [
        SignalStudyCriterionDeclaration.Declared(
            SignalStudyCriterionKind.PrimaryEstimand,
            "incremental post-decision signed return versus the frozen control"),
        SignalStudyCriterionDeclaration.Declared(
            SignalStudyCriterionKind.NullHypothesis,
            "H0: delta is not positive for the declared estimand"),
        SignalStudyCriterionDeclaration.Declared(
            SignalStudyCriterionKind.PrecisionPolicy,
            "fixture precision declaration"),
        SignalStudyCriterionDeclaration.Declared(
            SignalStudyCriterionKind.RejectPolicy,
            "fixture reject policy"),
        SignalStudyCriterionDeclaration.Declared(
            SignalStudyCriterionKind.MultiplicityPolicy,
            "fixture multiplicity policy")
    ];

    private static SignalStudyTrialRegistry CreateTrialRegistry() =>
        SignalStudyTrialRegistry.Declared(
        [
            new SignalStudyTrial("candidate", "fixture-hypothesis",
                Hash("candidate-input"), SignalStudyTrialRole.Candidate),
            new SignalStudyTrial("control", "fixture-control",
                Hash("control-input"), SignalStudyTrialRole.Control)
        ]);

    private static SignalStudyTemporalSplitPlan CreateSplitPlan() =>
        SignalStudyTemporalSplitPlan.Create(CreateFolds(),
            new SignalStudyTemporalInterval(At(35), At(45)),
            CreateLabelDependencies());

    private static SignalStudyWalkForwardFold[] CreateFolds() =>
    [
        new("fold-1", new SignalStudyTemporalInterval(At(0), At(10)),
            new SignalStudyTemporalInterval(At(10), At(15)),
            new SignalStudyTemporalInterval(At(15), At(20))),
        new("fold-2", new SignalStudyTemporalInterval(At(20), At(25)),
            new SignalStudyTemporalInterval(At(25), At(27)),
            new SignalStudyTemporalInterval(At(27), At(30)))
    ];

    private static List<SignalStudyLabelDependency> CreateLabelDependencies() =>
    [
        new("train-first-fold", "fold-1", SignalStudySplitRole.Training,
            At(8), new SignalStudyTemporalInterval(At(8), At(10)), At(10)),
        new("development-first-fold", "fold-1", SignalStudySplitRole.Development,
            At(14), new SignalStudyTemporalInterval(At(14), At(15)), At(15)),
        new("validation-first-fold", "fold-1", SignalStudySplitRole.Validation,
            At(18), new SignalStudyTemporalInterval(At(19), At(20)), At(22)),
        new("train-next-fold", "fold-2", SignalStudySplitRole.Training,
            At(21), new SignalStudyTemporalInterval(At(21), At(22)), At(22),
            isPurged: true, exclusionReason: "prior-validation-embargo"),
        new("validation-next-fold", "fold-2", SignalStudySplitRole.Validation,
            At(29), new SignalStudyTemporalInterval(At(30), At(32)), At(32)),
        new("oos-observation", null, SignalStudySplitRole.OutOfSample,
            At(36), new SignalStudyTemporalInterval(At(37), At(40)), At(42))
    ];

    private static UtcTimestamp At(int minute) => new(BaseTime.AddMinutes(minute));

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"signal-study-{Guid.NewGuid():N}");

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
