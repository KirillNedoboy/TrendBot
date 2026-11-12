using System.Text.Json;
using System.Collections.ObjectModel;
using TradingBot.Core;

namespace TradingBot.Backtesting;

public enum SignalStudySplitRole
{
    Training,
    Development,
    Validation,
    OutOfSample
}

/// <summary>A UTC half-open interval used by frozen study splits and labels.</summary>
public sealed record SignalStudyTemporalInterval
{
    public UtcTimestamp Start { get; }

    public UtcTimestamp End { get; }

    public SignalStudyTemporalInterval(UtcTimestamp start, UtcTimestamp end)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        if (start.Value >= end.Value)
        {
            throw new ArgumentException(
                "A study interval must have an increasing start and end.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public bool Contains(UtcTimestamp instant)
    {
        ArgumentNullException.ThrowIfNull(instant);
        return Start.Value <= instant.Value && instant.Value < End.Value;
    }

    public bool Overlaps(SignalStudyTemporalInterval other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start.Value < other.End.Value && other.Start.Value < End.Value;
    }

    public bool Contains(SignalStudyTemporalInterval other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start.Value <= other.Start.Value && other.End.Value <= End.Value;
    }
}

/// <summary>One chronological walk-forward fold; training may expand over prior folds.</summary>
public sealed record SignalStudyWalkForwardFold
{
    public string FoldId { get; }

    public SignalStudyTemporalInterval Training { get; }

    public SignalStudyTemporalInterval? Development { get; }

    public SignalStudyTemporalInterval Validation { get; }

    public SignalStudyWalkForwardFold(string foldId,
        SignalStudyTemporalInterval training,
        SignalStudyTemporalInterval? development,
        SignalStudyTemporalInterval validation)
    {
        FoldId = RequireText(foldId, nameof(foldId));
        Training = training ?? throw new ArgumentNullException(nameof(training));
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        Development = development;

        UtcTimestamp latestTrainingEnd = development?.Start ?? validation.Start;
        if (training.End.Value > latestTrainingEnd.Value
            || (development is not null
                && development.End.Value > validation.Start.Value))
        {
            throw new ArgumentException(
                "Walk-forward windows must be ordered training, development, validation.");
        }
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Study identifiers cannot contain line breaks.",
                parameterName);
        }

        return value;
    }
}

/// <summary>
/// Declared information and label dependency for one candidate. Purged rows remain
/// visible in the freeze but are excluded from the declared sample.
/// </summary>
public sealed record SignalStudyLabelDependency
{
    public string OpportunityId { get; }

    public string? FoldId { get; }

    public SignalStudySplitRole Role { get; }

    public UtcTimestamp InformationCut { get; }

    public SignalStudyTemporalInterval LabelInterval { get; }

    public UtcTimestamp LabelAvailableAt { get; }

    public bool IsPurged { get; }

    public string? ExclusionReason { get; }

    public SignalStudyLabelDependency(string opportunityId, string? foldId,
        SignalStudySplitRole role, UtcTimestamp informationCut,
        SignalStudyTemporalInterval labelInterval, UtcTimestamp labelAvailableAt,
        bool isPurged = false, string? exclusionReason = null)
    {
        OpportunityId = RequireText(opportunityId, nameof(opportunityId));
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        if (role == SignalStudySplitRole.OutOfSample)
        {
            if (foldId is not null)
            {
                throw new ArgumentException(
                    "An out-of-sample label is not assigned to a walk-forward fold.",
                    nameof(foldId));
            }
        }
        else
        {
            foldId = RequireText(foldId ?? throw new ArgumentNullException(nameof(foldId)),
                nameof(foldId));
        }

        FoldId = foldId;
        Role = role;
        InformationCut = informationCut
            ?? throw new ArgumentNullException(nameof(informationCut));
        LabelInterval = labelInterval
            ?? throw new ArgumentNullException(nameof(labelInterval));
        LabelAvailableAt = labelAvailableAt
            ?? throw new ArgumentNullException(nameof(labelAvailableAt));
        if (labelInterval.Start.Value < informationCut.Value
            || labelAvailableAt.Value < labelInterval.End.Value)
        {
            throw new ArgumentException(
                "Label intervals must follow the information cut and be available by their declared availability time.");
        }

        if (isPurged)
        {
            ExclusionReason = RequireText(exclusionReason!, nameof(exclusionReason));
        }
        else if (exclusionReason is not null)
        {
            throw new ArgumentException(
                "An included label cannot carry a purge exclusion reason.",
                nameof(exclusionReason));
        }

        IsPurged = isPurged;
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Study identifiers cannot contain line breaks.",
                parameterName);
        }

        return value;
    }
}

/// <summary>A forward embargo derived from frozen validation-label availability.</summary>
public sealed record SignalStudyForwardEmbargo(string FoldId,
    SignalStudyTemporalInterval Interval);

/// <summary>
/// Frozen chronological split schedule and label dependencies. Intervals are
/// half-open; training data is purged when a label is not available before a
/// later held-out boundary, and forward embargo ends at declared availability.
/// </summary>
public sealed class SignalStudyTemporalSplitPlan
{
    private readonly ReadOnlyCollection<SignalStudyWalkForwardFold> _folds;
    private readonly ReadOnlyCollection<SignalStudyLabelDependency> _labelDependencies;
    private readonly ReadOnlyCollection<SignalStudyForwardEmbargo> _forwardEmbargoIntervals;

    private SignalStudyTemporalSplitPlan(string? unknownReason,
        IReadOnlyList<SignalStudyWalkForwardFold> folds,
        SignalStudyTemporalInterval? outOfSample,
        IReadOnlyList<SignalStudyLabelDependency> labelDependencies)
    {
        UnknownReason = unknownReason;
        _folds = new ReadOnlyCollection<SignalStudyWalkForwardFold>(folds.ToArray());
        OutOfSample = outOfSample;
        _labelDependencies = new ReadOnlyCollection<SignalStudyLabelDependency>(
            labelDependencies.ToArray());
        _forwardEmbargoIntervals = new ReadOnlyCollection<SignalStudyForwardEmbargo>(
            ComputeForwardEmbargoes(_folds, _labelDependencies));
    }

    public SignalStudyDefinitionState State => UnknownReason is null
        ? SignalStudyDefinitionState.Ready : SignalStudyDefinitionState.Unknown;

    public string? UnknownReason { get; }

    public IReadOnlyList<SignalStudyWalkForwardFold> Folds => _folds;

    public SignalStudyTemporalInterval? OutOfSample { get; }

    public IReadOnlyList<SignalStudyLabelDependency> LabelDependencies =>
        _labelDependencies;

    public IReadOnlyList<SignalStudyForwardEmbargo> ForwardEmbargoIntervals =>
        _forwardEmbargoIntervals;

    public static SignalStudyTemporalSplitPlan Unknown(string reason)
    {
        string checkedReason = RequireText(reason, nameof(reason));
        return new SignalStudyTemporalSplitPlan(checkedReason, [], null, []);
    }

    public static SignalStudyTemporalSplitPlan Create(
        IEnumerable<SignalStudyWalkForwardFold> folds,
        SignalStudyTemporalInterval outOfSample,
        IEnumerable<SignalStudyLabelDependency> labelDependencies)
    {
        ArgumentNullException.ThrowIfNull(folds);
        ArgumentNullException.ThrowIfNull(outOfSample);
        ArgumentNullException.ThrowIfNull(labelDependencies);

        SignalStudyWalkForwardFold[] foldSnapshot = folds.ToArray();
        if (foldSnapshot.Length == 0 || foldSnapshot.Any(static fold => fold is null))
        {
            throw new ArgumentException(
                "A declared walk-forward plan requires at least one non-null fold.",
                nameof(folds));
        }

        if (foldSnapshot.Select(static fold => fold.FoldId)
            .Distinct(StringComparer.Ordinal).Count() != foldSnapshot.Length)
        {
            throw new ArgumentException("Walk-forward fold IDs must be unique.",
                nameof(folds));
        }

        foldSnapshot = foldSnapshot.OrderBy(static fold => fold.Validation.Start.Value)
            .ThenBy(static fold => fold.FoldId, StringComparer.Ordinal).ToArray();
        for (int index = 1; index < foldSnapshot.Length; index++)
        {
            if (foldSnapshot[index - 1].Validation.End.Value
                > foldSnapshot[index].Validation.Start.Value)
            {
                throw new ArgumentException(
                    "Walk-forward validation windows must be chronological and non-overlapping.",
                    nameof(folds));
            }
        }

        if (foldSnapshot[^1].Validation.End.Value > outOfSample.Start.Value)
        {
            throw new ArgumentException(
                "The single out-of-sample interval must follow all walk-forward validation windows.",
                nameof(outOfSample));
        }

        SignalStudyLabelDependency[] labels = labelDependencies.ToArray();
        if (labels.Any(static label => label is null))
        {
            throw new ArgumentException("Label dependencies cannot contain null entries.",
                nameof(labelDependencies));
        }

        if (labels.Select(static label => AssignmentKey(label))
            .Distinct(StringComparer.Ordinal).Count() != labels.Length)
        {
            throw new ArgumentException(
                "A split cannot declare the same opportunity assignment more than once.",
                nameof(labelDependencies));
        }

        SignalStudyTemporalSplitPlan plan = new(null, foldSnapshot, outOfSample,
            labels.OrderBy(static label => label.FoldId, StringComparer.Ordinal)
                .ThenBy(static label => label.Role)
                .ThenBy(static label => label.InformationCut.Value)
                .ThenBy(static label => label.OpportunityId, StringComparer.Ordinal)
                .ToArray());
        plan.ValidateLabels();
        return plan;
    }

    internal SignalStudyTemporalSplitPlanWire ToWire() => new(
        State, UnknownReason,
        _folds.Select(static fold => new SignalStudyWalkForwardFoldWire(
            fold.FoldId, ToWire(fold.Training),
            fold.Development is null ? null : ToWire(fold.Development),
            ToWire(fold.Validation))).ToArray(),
        OutOfSample is null ? null : ToWire(OutOfSample),
        _labelDependencies.Select(static label => new SignalStudyLabelDependencyWire(
            label.OpportunityId, label.FoldId, label.Role,
            label.InformationCut.Value, ToWire(label.LabelInterval),
            label.LabelAvailableAt.Value, label.IsPurged, label.ExclusionReason))
            .ToArray());

    internal static SignalStudyTemporalSplitPlan FromWire(
        SignalStudyTemporalSplitPlanWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (!Enum.IsDefined(wire.State))
        {
            throw new JsonException("The signal study split-plan state is invalid.");
        }

        if (wire.State == SignalStudyDefinitionState.Unknown)
        {
            if (wire.UnknownReason is null || wire.Folds.Count != 0
                || wire.OutOfSample is not null || wire.LabelDependencies.Count != 0)
            {
                throw new JsonException("The unknown signal study split plan is malformed.");
            }

            return Unknown(wire.UnknownReason);
        }

        if (wire.UnknownReason is not null || wire.OutOfSample is null)
        {
            throw new JsonException("The declared signal study split plan is malformed.");
        }

        return Create(wire.Folds.Select(static fold => new SignalStudyWalkForwardFold(
                fold.FoldId, FromWire(fold.Training),
                fold.Development is null ? null : FromWire(fold.Development),
                FromWire(fold.Validation))), FromWire(wire.OutOfSample),
            wire.LabelDependencies.Select(static label => new SignalStudyLabelDependency(
                label.OpportunityId, label.FoldId, label.Role,
                new UtcTimestamp(label.InformationCut), FromWire(label.LabelInterval),
                new UtcTimestamp(label.LabelAvailableAt), label.IsPurged,
                label.ExclusionReason)));
    }

    private void ValidateLabels()
    {
        IReadOnlyList<SignalStudyForwardEmbargo> embargoes = _forwardEmbargoIntervals;
        foreach (SignalStudyLabelDependency dependency in _labelDependencies)
        {
            SignalStudyTemporalInterval assignedWindow = GetAssignedWindow(dependency);
            if (!assignedWindow.Contains(dependency.InformationCut))
            {
                throw new ArgumentException(
                    "A label information cut must fall inside its declared split.",
                    nameof(LabelDependencies));
            }

            bool insidePriorEmbargo = embargoes.Any(embargo =>
                StringComparer.Ordinal.Equals(embargo.FoldId, dependency.FoldId) is false
                && embargo.Interval.Contains(dependency.InformationCut));
            if (!dependency.IsPurged && insidePriorEmbargo)
            {
                throw new ArgumentException(
                    "An observation inside a forward embargo must be explicitly purged.",
                    nameof(LabelDependencies));
            }

            if (dependency.IsPurged)
            {
                continue;
            }

            foreach (SignalStudyTemporalInterval heldOut in LaterHeldOutWindows(dependency))
            {
                if (dependency.LabelInterval.Overlaps(heldOut)
                    || dependency.LabelAvailableAt.Value > heldOut.Start.Value)
                {
                    throw new ArgumentException(
                        "A label that overlaps or is unavailable at a later held-out boundary must be purged.",
                        nameof(LabelDependencies));
                }
            }
        }
    }

    private SignalStudyTemporalInterval GetAssignedWindow(
        SignalStudyLabelDependency dependency)
    {
        if (dependency.Role == SignalStudySplitRole.OutOfSample)
        {
            return OutOfSample!;
        }

        SignalStudyWalkForwardFold fold = _folds.SingleOrDefault(item =>
            StringComparer.Ordinal.Equals(item.FoldId, dependency.FoldId))
            ?? throw new ArgumentException(
                "A label dependency refers to an unknown walk-forward fold.");
        return dependency.Role switch
        {
            SignalStudySplitRole.Training => fold.Training,
            SignalStudySplitRole.Development => fold.Development
                ?? throw new ArgumentException(
                    "A development label requires a declared development window."),
            SignalStudySplitRole.Validation => fold.Validation,
            _ => throw new ArgumentOutOfRangeException(nameof(dependency))
        };
    }

    private IEnumerable<SignalStudyTemporalInterval> LaterHeldOutWindows(
        SignalStudyLabelDependency dependency)
    {
        IEnumerable<SignalStudyTemporalInterval> heldOut = _folds
            .Where(static fold => fold.Development is not null)
            .Select(static fold => fold.Development!)
            .Concat(_folds.Select(static fold => fold.Validation))
            .Append(OutOfSample!);
        return heldOut.Where(window =>
            window.Start.Value > dependency.InformationCut.Value);
    }

    private static SignalStudyForwardEmbargo[] ComputeForwardEmbargoes(
        IReadOnlyList<SignalStudyWalkForwardFold> folds,
        IReadOnlyList<SignalStudyLabelDependency> labels) => folds
        .Select(fold =>
        {
            UtcTimestamp? latestAvailability = labels
                .Where(label => label.Role == SignalStudySplitRole.Validation
                    && StringComparer.Ordinal.Equals(label.FoldId, fold.FoldId)
                    && !label.IsPurged)
                .Select(static label => label.LabelAvailableAt)
                .OrderByDescending(static timestamp => timestamp.Value)
                .FirstOrDefault();
            return latestAvailability is null
                || latestAvailability.Value <= fold.Validation.End.Value
                ? null
                : new SignalStudyForwardEmbargo(fold.FoldId,
                    new SignalStudyTemporalInterval(fold.Validation.End,
                        latestAvailability));
        })
        .Where(static value => value is not null)
        .Select(static value => value!)
        .ToArray();

    private static string AssignmentKey(SignalStudyLabelDependency dependency) =>
        $"{dependency.FoldId ?? "<oos>"}\0{dependency.Role}\0{dependency.OpportunityId}";

    private static SignalStudyTemporalIntervalWire ToWire(
        SignalStudyTemporalInterval interval) => new(interval.Start.Value,
        interval.End.Value);

    private static SignalStudyTemporalInterval FromWire(
        SignalStudyTemporalIntervalWire interval) => new(
        new UtcTimestamp(interval.Start), new UtcTimestamp(interval.End));

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Study text cannot contain line breaks.",
                parameterName);
        }

        return value;
    }
}

internal sealed record SignalStudyTemporalSplitPlanWire(
    SignalStudyDefinitionState State,
    string? UnknownReason,
    IReadOnlyList<SignalStudyWalkForwardFoldWire> Folds,
    SignalStudyTemporalIntervalWire? OutOfSample,
    IReadOnlyList<SignalStudyLabelDependencyWire> LabelDependencies);

internal sealed record SignalStudyWalkForwardFoldWire(
    string FoldId,
    SignalStudyTemporalIntervalWire Training,
    SignalStudyTemporalIntervalWire? Development,
    SignalStudyTemporalIntervalWire Validation);

internal sealed record SignalStudyLabelDependencyWire(
    string OpportunityId,
    string? FoldId,
    SignalStudySplitRole Role,
    DateTimeOffset InformationCut,
    SignalStudyTemporalIntervalWire LabelInterval,
    DateTimeOffset LabelAvailableAt,
    bool IsPurged,
    string? ExclusionReason);

internal sealed record SignalStudyTemporalIntervalWire(
    DateTimeOffset Start,
    DateTimeOffset End);
