using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Backtesting;

/// <summary>One caller supplied research configuration value in a report.</summary>
public sealed record BacktestConfigurationEntry
{
    public string Name { get; }

    public string Value { get; }

    public BacktestConfigurationEntry(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Name = name;
        Value = value;
    }
}

/// <summary>
/// Causal reference for an event delivered by the historical replay runner.
/// The payload is retained as canonical Core JSON so the report can be audited
/// without reinterpreting a live object graph.
/// </summary>
public sealed record BacktestEventReference
{
    public string EventId { get; }

    public string EventType { get; }

    public string InstrumentId { get; }

    public DateTimeOffset EventTime { get; }

    public DateTimeOffset ReceiveTime { get; }

    public DateTimeOffset AvailabilityTime { get; }

    public DateTimeOffset? ExchangeTime { get; }

    public DateTimeOffset VirtualTime { get; }

    public long StableSequence { get; }

    public string PayloadJson { get; }

    internal static BacktestEventReference From(ReplayStepResult step)
    {
        ArgumentNullException.ThrowIfNull(step);
        CausalMarketEvent causal = step.Envelope.CausalEvent;
        MarketEvent payload = causal.Payload;
        return new BacktestEventReference(payload.EventId,
            payload.GetType().Name, payload.InstrumentId.Value,
            payload.EventTime.Value, causal.ReceiveTime.Value,
            causal.AvailabilityTime.Value, causal.ExchangeTime?.Value,
            step.VirtualTime.Value, causal.StableSequence,
            CoreJsonSerializer.Serialize(payload));
    }

    public BacktestEventReference(string eventId, string eventType,
        string instrumentId, DateTimeOffset eventTime, DateTimeOffset receiveTime,
        DateTimeOffset availabilityTime, DateTimeOffset? exchangeTime,
        DateTimeOffset virtualTime, long stableSequence, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentId);
        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "A causal reference sequence cannot be negative.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        EventId = eventId;
        EventType = eventType;
        InstrumentId = instrumentId;
        EventTime = eventTime.ToUniversalTime();
        ReceiveTime = receiveTime.ToUniversalTime();
        AvailabilityTime = availabilityTime.ToUniversalTime();
        ExchangeTime = exchangeTime?.ToUniversalTime();
        VirtualTime = virtualTime.ToUniversalTime();
        StableSequence = stableSequence;
        PayloadJson = payloadJson;
    }
}

/// <summary>Serializable decision and reason references retained by a report.</summary>
public sealed record BacktestDecisionReference
{
    public string DecisionId { get; }

    public string SetupId { get; }

    public DecisionOutcome Outcome { get; }

    public IReadOnlyList<string> Reasons { get; }

    internal static BacktestDecisionReference From(Decision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new BacktestDecisionReference(decision.DecisionId, decision.SetupId,
            decision.Outcome, decision.Reasons);
    }

    public BacktestDecisionReference(string decisionId, string setupId,
        DecisionOutcome outcome, IEnumerable<string> reasons)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(setupId);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
                "A decision outcome must be defined.");
        }

        ArgumentNullException.ThrowIfNull(reasons);
        string[] snapshot = reasons.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A decision must retain at least one reason.",
                nameof(reasons));
        }

        DecisionId = decisionId;
        SetupId = setupId;
        Outcome = outcome;
        Reasons = new ReadOnlyCollection<string>(snapshot);
    }
}

/// <summary>Serialized lifecycle transition reference for regression comparison.</summary>
public sealed record BacktestTransitionReference
{
    public SetupLifecycleTransitionStatus Status { get; }

    public SetupState PreviousState { get; }

    public SetupState CurrentState { get; }

    public string PreviousSetupIdentity { get; }

    public string CurrentSetupIdentity { get; }

    public string InputEventId { get; }

    public long InputStableSequence { get; }

    public string DecisionInputSnapshotJson { get; }

    public IReadOnlyList<string> Reasons { get; }

    internal static BacktestTransitionReference From(SetupLifecycleTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return new BacktestTransitionReference(transition.Status,
            transition.PreviousState, transition.CurrentState,
            transition.PreviousIdentity.Fingerprint, transition.CurrentIdentity.Fingerprint,
            transition.Input.EventId, transition.Input.StableSequence,
            transition.DecisionInputSnapshotJson, transition.Reasons);
    }

    public BacktestTransitionReference(SetupLifecycleTransitionStatus status,
        SetupState previousState, SetupState currentState,
        string previousSetupIdentity, string currentSetupIdentity,
        string inputEventId, long inputStableSequence,
        string decisionInputSnapshotJson, IEnumerable<string> reasons)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status,
                "A lifecycle transition status must be defined.");
        }

        if (!Enum.IsDefined(previousState))
        {
            throw new ArgumentOutOfRangeException(nameof(previousState), previousState,
                "A lifecycle state must be defined.");
        }

        if (!Enum.IsDefined(currentState))
        {
            throw new ArgumentOutOfRangeException(nameof(currentState), currentState,
                "A lifecycle state must be defined.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(previousSetupIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSetupIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputEventId);
        if (inputStableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputStableSequence), inputStableSequence,
                "A lifecycle input sequence cannot be negative.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(decisionInputSnapshotJson);
        ArgumentNullException.ThrowIfNull(reasons);
        string[] snapshot = reasons.ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Transition reasons cannot contain blank values.",
                nameof(reasons));
        }

        Status = status;
        PreviousState = previousState;
        CurrentState = currentState;
        PreviousSetupIdentity = previousSetupIdentity;
        CurrentSetupIdentity = currentSetupIdentity;
        InputEventId = inputEventId;
        InputStableSequence = inputStableSequence;
        DecisionInputSnapshotJson = decisionInputSnapshotJson;
        Reasons = new ReadOnlyCollection<string>(snapshot);
    }
}

/// <summary>Explicit limitations carried into every OHLCV report.</summary>
public static class BacktestReportLimitations
{
    public static IReadOnlyList<string> Default { get; } = new ReadOnlyCollection<string>(
    [
        "OHLCV cannot reconstruct intrabar sequence, actual fills, spread, queue position, L2 state, or true point of control.",
        "Take-profit portions are management assumptions; liquidity and exchange partial fills are not proven."
    ]);
}

/// <summary>Outcome of comparing two deterministic backtest reports.</summary>
public sealed record BacktestRegressionComparison
{
    public bool Equivalent { get; }

    public string LeftFingerprint { get; }

    public string RightFingerprint { get; }

    public IReadOnlyList<string> Differences { get; }

    internal BacktestRegressionComparison(bool equivalent, string leftFingerprint,
        string rightFingerprint, IEnumerable<string> differences)
    {
        Equivalent = equivalent;
        LeftFingerprint = leftFingerprint;
        RightFingerprint = rightFingerprint;
        Differences = new ReadOnlyCollection<string>(differences.ToArray());
    }
}

/// <summary>Compares completed research reports without considering run identity.</summary>
public static class BacktestRegression
{
    public static BacktestRegressionComparison Compare(BacktestReport left,
        BacktestReport right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        bool equivalent = StringComparer.Ordinal.Equals(left.DeterministicFingerprint,
            right.DeterministicFingerprint);
        string[] differences = equivalent
            ? []
            : ["canonical backtest result fingerprints differ"];
        return new BacktestRegressionComparison(equivalent,
            left.DeterministicFingerprint, right.DeterministicFingerprint, differences);
    }
}

/// <summary>
/// Canonical, versioned result journal for one completed historical research run.
/// It contains no authorization, order intent, or execution state.
/// </summary>
public sealed record BacktestReport
{
    public const string CurrentFormatVersion = "backtest-report-v1";

    public string FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>Alias for callers that refer to the report schema version.</summary>
    public string ReportVersion => FormatVersion;

    public string RunId { get; init; } = string.Empty;

    public string StreamId { get; init; } = string.Empty;

    public bool Completed { get; init; } = true;

    public IReadOnlyList<BacktestConfigurationEntry> Configuration { get; init; } =
        Array.Empty<BacktestConfigurationEntry>();

    public IReadOnlyList<BacktestEventReference> Events { get; init; } =
        Array.Empty<BacktestEventReference>();

    public IReadOnlyList<BacktestTransitionReference> Transitions { get; init; } =
        Array.Empty<BacktestTransitionReference>();

    public IReadOnlyList<string> SetupIdentities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<BacktestDecisionReference> Decisions { get; init; } =
        Array.Empty<BacktestDecisionReference>();

    public RiskMathResult? RiskResult { get; init; }

    /// <summary>Gets the complete research plan when risk simulation was run.</summary>
    public SimulatedRiskPlan? RiskPlan { get; init; }

    public IReadOnlyList<SimulatedFill> Fills { get; init; } = Array.Empty<SimulatedFill>();

    public decimal TotalFees { get; init; }

    public decimal TotalSlippage { get; init; }

    public decimal RemainingQuantity { get; init; }

    public bool IsOpen => RemainingQuantity > 0m;

    public SimulatedRiskExitReason ExitReason { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Limitations { get; init; } = BacktestReportLimitations.Default;

    /// <summary>Gets compact canonical JSON for atomic journal export.</summary>
    [JsonIgnore]
    public string CanonicalJson => BacktestReportSerialization.Serialize(this, includeRunId: true);

    /// <summary>
    /// Gets a lower-case SHA-256 over the report result excluding the caller's
    /// run identifier, so repeated runs can be compared directly.
    /// </summary>
    [JsonIgnore]
    public string DeterministicFingerprint => BacktestReportSerialization.Fingerprint(this);

    /// <summary>Gets the identity of this exact serialized report.</summary>
    [JsonIgnore]
    public string Identity => ComputeHash(CanonicalJson);

    /// <summary>Alias used by journal consumers that call the report complete.</summary>
    [JsonIgnore]
    public bool IsCompleted => Completed;

    public static BacktestReport Create(string runId, ReplayRunResult replay,
        SimulatedRiskSimulationResult? simulation = null,
        IEnumerable<Decision>? decisions = null,
        IEnumerable<SetupLifecycleTransition>? transitions = null,
        IEnumerable<BacktestConfigurationEntry>? configuration = null,
        IEnumerable<string>? limitations = null,
        IEnumerable<SetupLifecycleIdentity>? setupIdentities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(replay);
        if (!replay.Succeeded)
        {
            throw new InvalidOperationException(
                "A backtest report can only be created from a successful replay.",
                replay.Failure);
        }

        BacktestTransitionReference[] transitionReferences = transitions is null
            ? []
            : transitions.Select(BacktestTransitionReference.From).ToArray();
        string[] transitionIdentities = transitionReferences
            .SelectMany(static transition => new[]
            {
                transition.PreviousSetupIdentity, transition.CurrentSetupIdentity
            })
            .ToArray();
        string[] explicitSetupIdentities = setupIdentities is null
            ? []
            : setupIdentities.Select(static identity => identity.Fingerprint).ToArray();
        string[] setupIdentitySnapshot = transitionIdentities
            .Concat(explicitSetupIdentities)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        List<string> diagnostics = replay.Diagnostics
            .Select(static diagnostic =>
                $"{diagnostic.Kind}:{diagnostic.EventId ?? "none"}:{diagnostic.Message}")
            .ToList();

        RiskMathResult? riskResult = simulation?.RiskResult;
        Decision[] decisionSnapshot = decisions?.ToArray()
            ?? (simulation?.Plan.Decision is Decision linkedDecision
                ? [linkedDecision]
                : []);
        SimulatedFill[] fills = simulation?.Fills.ToArray() ?? [];
        if (simulation is not null)
        {
            diagnostics.AddRange(simulation.Diagnostics);
        }

        if (diagnostics.Count == 0)
        {
            diagnostics.Add("none");
        }

        string[] limitationSnapshot = limitations?.ToArray()
            ?? BacktestReportLimitations.Default.ToArray();
        if (limitationSnapshot.Length == 0 || limitationSnapshot.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A report must state at least one limitation.",
                nameof(limitations));
        }

        return new BacktestReport
        {
            FormatVersion = CurrentFormatVersion,
            RunId = runId,
            StreamId = replay.StreamId,
            Completed = true,
            Configuration = SnapshotConfiguration(configuration),
            Events = new ReadOnlyCollection<BacktestEventReference>(
                replay.Steps.Select(BacktestEventReference.From).ToArray()),
            Transitions = new ReadOnlyCollection<BacktestTransitionReference>(
                transitionReferences),
            SetupIdentities = new ReadOnlyCollection<string>(setupIdentitySnapshot),
            Decisions = new ReadOnlyCollection<BacktestDecisionReference>(
                decisionSnapshot.Select(BacktestDecisionReference.From).ToArray()),
            RiskResult = riskResult,
            RiskPlan = simulation?.Plan,
            Fills = new ReadOnlyCollection<SimulatedFill>(fills),
            TotalFees = simulation?.TotalFees ?? 0m,
            TotalSlippage = simulation?.TotalSlippage ?? 0m,
            RemainingQuantity = simulation?.RemainingQuantity ?? 0m,
            ExitReason = simulation?.ExitReason ?? SimulatedRiskExitReason.None,
            Diagnostics = new ReadOnlyCollection<string>(diagnostics),
            Limitations = new ReadOnlyCollection<string>(limitationSnapshot)
        };
    }

    private static IReadOnlyList<BacktestConfigurationEntry> SnapshotConfiguration(
        IEnumerable<BacktestConfigurationEntry>? configuration)
    {
        if (configuration is null)
        {
            return Array.Empty<BacktestConfigurationEntry>();
        }

        BacktestConfigurationEntry[] values = configuration.ToArray();
        if (values.Any(static value => value is null))
        {
            throw new ArgumentException("Configuration cannot contain null entries.",
                nameof(configuration));
        }

        return new ReadOnlyCollection<BacktestConfigurationEntry>(values
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ThenBy(static value => value.Value, StringComparer.Ordinal)
            .ToArray());
    }

    private static string ComputeHash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Atomic writer for completed canonical backtest reports.</summary>
public static class BacktestReportJournal
{
    public static void WriteAtomic(BacktestReport report, string path)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!report.Completed)
        {
            throw new InvalidOperationException(
                "Only completed backtest reports can be exported.");
        }

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The report path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(report.CanonicalJson);
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

/// <summary>Compatibility name for callers that treat the report as a writer.</summary>
public static class BacktestReportWriter
{
    public static void WriteAtomic(BacktestReport report, string path) =>
        BacktestReportJournal.WriteAtomic(report, path);
}

internal static class BacktestReportSerialization
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(BacktestReport report, bool includeRunId)
    {
        ArgumentNullException.ThrowIfNull(report);
        BacktestReportPayload payload = ToPayload(report, includeRunId);
        return JsonSerializer.Serialize(payload, Options);
    }

    public static string Fingerprint(BacktestReport report) => ComputeHash(
        Serialize(report, includeRunId: false));

    private static BacktestReportPayload ToPayload(BacktestReport report, bool includeRunId)
    {
        return new BacktestReportPayload(
            report.FormatVersion,
            includeRunId ? report.RunId : null,
            report.StreamId,
            report.Completed,
            report.Configuration
                .OrderBy(static value => value.Name, StringComparer.Ordinal)
                .ThenBy(static value => value.Value, StringComparer.Ordinal)
                .ToArray(),
            report.Events.ToArray(),
            report.Transitions.ToArray(),
            report.SetupIdentities.Order(StringComparer.Ordinal).ToArray(),
            report.Decisions.OrderBy(static value => value.DecisionId,
                StringComparer.Ordinal).ToArray(),
            ToRiskPayload(report.RiskResult),
            ToPlanPayload(report.RiskPlan),
            report.Fills.Select(BacktestFillPayload.From).ToArray(),
            report.TotalFees,
            report.TotalSlippage,
            report.RemainingQuantity,
            report.IsOpen,
            report.ExitReason,
            report.Diagnostics.ToArray(),
            report.Limitations.ToArray());
    }

    private static BacktestRiskPayload? ToRiskPayload(RiskMathResult? result)
    {
        if (result is null)
        {
            return null;
        }

        return new BacktestRiskPayload(result.Version.Value,
            result.Input.InstrumentId.Value, result.Input.Direction,
            result.RiskBudget.Amount, result.RiskBudget.Currency,
            result.LossPerUnit.Amount, result.RawQuantity.Value,
            result.NotionalExposure.Amount, result.NotionalExposure.Currency);
    }

    private static BacktestPlanPayload? ToPlanPayload(SimulatedRiskPlan? plan)
    {
        if (plan is null)
        {
            return null;
        }

        RiskMathInput input = plan.RiskInput;
        SimulatedFillAssumptions assumptions = plan.FillAssumptions;
        FillSimulationAssumptions descriptions = assumptions.Descriptions;
        return new BacktestPlanPayload(plan.ResearchPlanId, plan.DecisionId,
            plan.InstrumentId.Value, plan.Direction, plan.DecisionTime.Value,
            new BacktestRiskInputPayload(input.InstrumentId.Value, input.Direction,
                input.Equity.Amount, input.Equity.Currency, input.RiskFraction,
                input.EntryPrice.Value, input.ProtectiveStopPrice.Value,
                input.FeePerUnit.Amount, input.EstimatedSlippagePerUnit.Amount),
            plan.TakeProfitSteps.Select(static target => new BacktestTakeProfitPayload(
                target.StepId, target.Price.Value, target.Fraction)).ToArray(),
            plan.HoldingPeriod?.ToString("c", CultureInfo.InvariantCulture),
            plan.MoveStopToBreakeven, plan.BreakevenStopPrice?.Value,
            new BacktestFillAssumptionsPayload(assumptions.Version?.Value,
                assumptions.FeePerQuantityUnit, assumptions.SlippagePerQuantityUnit,
                descriptions.Fees, descriptions.Slippage, descriptions.Latency,
                descriptions.LiquidityAndQueue, descriptions.PartialFills,
                descriptions.IntrabarOrdering, descriptions.DataLimitations));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private static string ComputeHash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record BacktestReportPayload(
        string FormatVersion,
        string? RunId,
        string StreamId,
        bool Completed,
        IReadOnlyList<BacktestConfigurationEntry> Configuration,
        IReadOnlyList<BacktestEventReference> Events,
        IReadOnlyList<BacktestTransitionReference> Transitions,
        IReadOnlyList<string> SetupIdentities,
        IReadOnlyList<BacktestDecisionReference> Decisions,
        BacktestRiskPayload? RiskResult,
        BacktestPlanPayload? RiskPlan,
        IReadOnlyList<BacktestFillPayload> Fills,
        decimal TotalFees,
        decimal TotalSlippage,
        decimal RemainingQuantity,
        bool IsOpen,
        SimulatedRiskExitReason ExitReason,
        IReadOnlyList<string> Diagnostics,
        IReadOnlyList<string> Limitations);

    private sealed record BacktestRiskPayload(
        string Version,
        string InstrumentId,
        Direction Direction,
        decimal RiskBudget,
        string RiskBudgetCurrency,
        decimal LossPerUnit,
        decimal RawQuantity,
        decimal NotionalExposure,
        string NotionalExposureCurrency);

    private sealed record BacktestPlanPayload(
        string ResearchPlanId,
        string? DecisionId,
        string InstrumentId,
        Direction Direction,
        DateTimeOffset DecisionTime,
        BacktestRiskInputPayload RiskInput,
        IReadOnlyList<BacktestTakeProfitPayload> TakeProfitSteps,
        string? HoldingPeriod,
        bool MoveStopToBreakeven,
        decimal? BreakevenStopPrice,
        BacktestFillAssumptionsPayload FillAssumptions);

    private sealed record BacktestRiskInputPayload(
        string InstrumentId,
        Direction Direction,
        decimal Equity,
        string EquityCurrency,
        decimal RiskFraction,
        decimal EntryPrice,
        decimal ProtectiveStopPrice,
        decimal FeePerUnit,
        decimal EstimatedSlippagePerUnit);

    private sealed record BacktestTakeProfitPayload(
        string StepId,
        decimal Price,
        decimal Fraction);

    private sealed record BacktestFillAssumptionsPayload(
        string? Version,
        decimal FeePerQuantityUnit,
        decimal SlippagePerQuantityUnit,
        string Fees,
        string Slippage,
        string Latency,
        string LiquidityAndQueue,
        string PartialFills,
        string IntrabarOrdering,
        string DataLimitations);

    private sealed record BacktestFillPayload(
        string FillId,
        SimulatedFillRole Role,
        decimal Quantity,
        decimal Price,
        decimal Fee,
        string FeeCurrency,
        decimal Slippage,
        string SlippageCurrency,
        DateTimeOffset FilledAt,
        long StableSequence,
        string EventId)
    {
        public static BacktestFillPayload From(SimulatedFill fill) => new(fill.FillId,
            fill.Role, fill.Quantity.Value, fill.Price.Value, fill.Fee.Amount,
            fill.Fee.Currency, fill.Slippage.Amount, fill.Slippage.Currency,
            fill.FilledAt.Value, fill.StableSequence, fill.EventId);
    }
}
