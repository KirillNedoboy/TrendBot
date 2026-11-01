using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>One bounded causal observation presented to the local-paper lane.</summary>
public sealed record LocalPaperObservation
{
    public ReplayEnvelope Envelope { get; }

    public LocalPaperApplication? Application { get; }

    public LocalPaperObservation(ReplayEnvelope envelope,
        LocalPaperApplication? application = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Envelope = envelope;
        Application = application;
    }

    /// <summary>Convenience constructor for an observation carrying a risk/plan pair.</summary>
    public LocalPaperObservation(ReplayEnvelope envelope, RiskEvaluationInput riskInput,
        SimulatedRiskPlan plan)
        : this(envelope, new LocalPaperApplication(riskInput, plan))
    {
    }
}

/// <summary>Immutable risk input and separate fill-simulation plan.</summary>
public sealed record LocalPaperApplication
{
    public RiskEvaluationInput RiskEvaluationInput { get; }

    public RiskEvaluationInput RiskInput => RiskEvaluationInput;

    public SimulatedRiskPlan SimulatedRiskPlan { get; }

    public SimulatedRiskPlan Plan => SimulatedRiskPlan;

    public LocalPaperApplication(RiskEvaluationInput riskEvaluationInput,
        SimulatedRiskPlan simulatedRiskPlan)
    {
        ArgumentNullException.ThrowIfNull(riskEvaluationInput);
        ArgumentNullException.ThrowIfNull(simulatedRiskPlan);
        RiskEvaluationInput = riskEvaluationInput;
        SimulatedRiskPlan = simulatedRiskPlan;
    }
}

/// <summary>Outcome status for one local-paper observation.</summary>
public enum LocalPaperExecutionStatus
{
    Recorded,
    Applied,
    AlreadyProcessed,
    Blocked
}

/// <summary>
/// Result of local-paper processing. Risk evidence and fill simulation remain
/// separate properties; neither property grants execution authority.
/// </summary>
public sealed record LocalPaperExecutionResult
{
    public LocalPaperExecutionStatus Status { get; }

    public string StreamId { get; }

    public long Sequence { get; }

    public string EventId { get; }

    public string ObservationIdentity { get; }

    public string ConfigurationIdentity { get; }

    public string? PlanIdentity { get; }

    public string? RiskInputIdentity { get; }

    public string? ModelIdentity { get; }

    public RiskEvaluationResult? RiskEvaluation { get; }

    public RiskEvaluationResult? Risk => RiskEvaluation;

    public SimulatedRiskSimulationResult? FillSimulation { get; }

    public SimulatedRiskSimulationResult? Simulation => FillSimulation;

    public LocalPaperLedgerSnapshot? Ledger { get; }

    public long Revision => Ledger?.Revision ?? 0;

    public bool IsBlocked => Status == LocalPaperExecutionStatus.Blocked;

    public bool IsIdempotent => Status == LocalPaperExecutionStatus.AlreadyProcessed;

    public bool IsApplied => Status is LocalPaperExecutionStatus.Applied
        or LocalPaperExecutionStatus.Recorded;

    public IReadOnlyList<string> BlockReasons { get; }

    public string? BlockReason => BlockReasons.Count == 0 ? null : BlockReasons[0];

    private LocalPaperExecutionResult(LocalPaperExecutionStatus status, string streamId,
        long sequence, string eventId, string observationIdentity, string configurationIdentity,
        string? planIdentity, string? riskInputIdentity, string? modelIdentity,
        RiskEvaluationResult? riskEvaluation, SimulatedRiskSimulationResult? fillSimulation,
        LocalPaperLedgerSnapshot? ledger, IEnumerable<string> blockReasons)
    {
        Status = status;
        StreamId = streamId;
        Sequence = sequence;
        EventId = eventId;
        ObservationIdentity = observationIdentity;
        ConfigurationIdentity = configurationIdentity;
        PlanIdentity = planIdentity;
        RiskInputIdentity = riskInputIdentity;
        ModelIdentity = modelIdentity;
        RiskEvaluation = riskEvaluation;
        FillSimulation = fillSimulation;
        Ledger = ledger;
        BlockReasons = new ReadOnlyCollection<string>(blockReasons
            .Where(static reason => !String.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }

    internal static LocalPaperExecutionResult Create(LocalPaperExecutionStatus status,
        LocalPaperObservation observation, string observationIdentity, string configurationIdentity,
        string? planIdentity, string? riskInputIdentity, string? modelIdentity,
        RiskEvaluationResult? riskEvaluation, SimulatedRiskSimulationResult? fillSimulation,
        LocalPaperLedgerSnapshot? ledger, IEnumerable<string> reasons) => new(status,
        observation.Envelope.StreamId, observation.Envelope.CausalEvent.StableSequence,
        observation.Envelope.CausalEvent.Payload.EventId, observationIdentity,
        configurationIdentity, planIdentity, riskInputIdentity, modelIdentity, riskEvaluation,
        fillSimulation, ledger, reasons);
}

/// <summary>
/// Runs one deterministic local-paper observation over the durable causal
/// prefix. It has no private transport, credential, activation, or order API.
/// </summary>
public enum LocalPaperFaultPoint
{
    BeforeIntent,
    AfterIntent,
    BeforeCommit,
    AfterCommitBeforeResponse
}

/// <summary>Test-only crash/interruption signal for restart proof.</summary>
public sealed class LocalPaperInterruptionException : Exception
{
    public LocalPaperInterruptionException(LocalPaperFaultPoint point)
        : base($"Injected local-paper interruption at {point}.") => Point = point;

    public LocalPaperFaultPoint Point { get; }
}

public sealed class LocalPaperExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly LocalPaperConfiguration _configuration;
    private readonly LocalPaperStore _store;
    private readonly IRiskMathCalculator _riskMathCalculator;
    private readonly IFillSimulator _fillSimulator;
    private readonly SimulatedRiskSimulator _simulator;
    private readonly Action<LocalPaperFaultPoint>? _faultInjector;

    public LocalPaperExecutor(LocalPaperConfiguration configuration, LocalPaperStore store,
        IRiskMathCalculator riskMathCalculator, IFillSimulator fillSimulator)
        : this(configuration, store, riskMathCalculator, fillSimulator, null)
    {
    }

    /// <summary>
    /// Optional interruption seam used by deterministic restart tests. It is
    /// deliberately limited to local persistence boundaries and grants no
    /// execution authority.
    /// </summary>
    public LocalPaperExecutor(LocalPaperConfiguration configuration, LocalPaperStore store,
        IRiskMathCalculator riskMathCalculator, IFillSimulator fillSimulator,
        Action<LocalPaperFaultPoint>? faultInjector)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(riskMathCalculator);
        ArgumentNullException.ThrowIfNull(fillSimulator);
        _configuration = configuration;
        _store = store;
        _riskMathCalculator = riskMathCalculator;
        _fillSimulator = fillSimulator;
        _simulator = new SimulatedRiskSimulator(riskMathCalculator, fillSimulator);
        _faultInjector = faultInjector;
    }

    public LocalPaperExecutor(LocalPaperConfiguration configuration, LocalPaperStore store,
        IRiskMathCalculator riskMathCalculator, Action<LocalPaperFaultPoint>? faultInjector)
        : this(configuration, store, riskMathCalculator, new OhlcvFillSimulator(),
            faultInjector)
    {
    }

    public LocalPaperExecutor(LocalPaperConfiguration configuration, LocalPaperStore store,
        IRiskMathCalculator riskMathCalculator)
        : this(configuration, store, riskMathCalculator, new OhlcvFillSimulator())
    {
    }

    public LocalPaperExecutor(LocalPaperStore store, LocalPaperConfiguration configuration,
        IRiskMathCalculator riskMathCalculator, IFillSimulator fillSimulator)
        : this(configuration, store, riskMathCalculator, fillSimulator)
    {
    }

    /// <summary>Processes one bounded local causal observation.</summary>
    public LocalPaperExecutionResult Process(LocalPaperObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ReplayEnvelope envelope = observation.Envelope;
        string configurationJson;
        string configurationIdentity;
        string? planIdentity = null;
        string? riskInputIdentity = null;
        string? modelIdentity = null;
        string? applicationJson = null;
        RiskEvaluationResult? risk = null;
        SimulatedRiskSimulationResult? simulation = null;
        LocalPaperPreparation? preparation = null;
        LocalPaperApplication? simulationApplication = observation.Application;
        long? reservationSequence = null;
        string observationIdentity = "observation-unavailable";

        try
        {
            configurationJson = CanonicalizeJson(_configuration.ToJson());
            configurationIdentity = Hash(configurationJson);

            if (observation.Application is { } application)
            {
                bool existingObservation = _store.ReadObservations(envelope.StreamId)
                    .Any(row => row.Sequence == envelope.CausalEvent.StableSequence);
                decimal? expectedLedgerEquity = existingObservation
                    ? null
                    : ReadCurrentLedgerEquity(envelope.StreamId);
                List<string> validationReasons = ValidateApplication(envelope, application,
                    expectedLedgerEquity);
                if (validationReasons.Count != 0)
                {
                    return Blocked(observation, configurationIdentity, null, null, null,
                        validationReasons);
                }

                LocalPaperCausalIdentity causal = LocalPaperCausalIdentity.From(envelope);
                string riskInputJson = CanonicalCoreJson(application.RiskEvaluationInput);
                string planJson = CanonicalCoreJson(application.SimulatedRiskPlan);
                string assumptionsJson = CanonicalCoreJson(
                    application.SimulatedRiskPlan.FillAssumptions);
                riskInputIdentity = Hash(CanonicalJson(new IdentityDocument(riskInputJson,
                    causal)));
                planIdentity = Hash(CanonicalJson(new IdentityDocument(planJson, causal)));
                modelIdentity = Hash(CanonicalJson(new IdentityDocument(
                    $"{_fillSimulator.Version.Value}\n{assumptionsJson}", causal)));
                applicationJson = CanonicalJson(new ApplicationDocument(riskInputJson, planJson,
                    assumptionsJson));
                observationIdentity = Hash(CanonicalJson(new ObservationIdentityDocument(
                    CanonicalCoreJson(envelope), applicationJson, configurationIdentity,
                    riskInputIdentity, planIdentity, modelIdentity, causal)));

                risk = RiskEvaluationEngine.Evaluate(application.RiskEvaluationInput,
                    _configuration.RiskConfiguration, _riskMathCalculator);
                if (!risk.IsApproved)
                {
                    return Blocked(observation, configurationIdentity, planIdentity,
                        riskInputIdentity, modelIdentity, risk.Reasons, risk,
                        durableObservationIdentity: observationIdentity);
                }

                if (risk.RiskMathResult is null)
                {
                    return Blocked(observation, configurationIdentity, planIdentity,
                        riskInputIdentity, modelIdentity,
                        ["Risk math evidence is unavailable."], risk,
                        durableObservationIdentity: observationIdentity);
                }
            }
            else
            {
                IReadOnlyList<LocalPaperObservationRecord> persisted = _store
                    .ReadObservations(envelope.StreamId);
                LocalPaperObservationRecord? existing = persisted.FirstOrDefault(row =>
                    row.Sequence == envelope.CausalEvent.StableSequence);
                if (existing is not null)
                {
                    planIdentity = existing.PlanIdentity;
                    riskInputIdentity = existing.RiskInputIdentity;
                    modelIdentity = existing.ModelIdentity;
                }
                else
                {
                    IReadOnlyList<LocalPaperObservationRecord> reserved = _store
                        .ReadReservedObservations(envelope.StreamId);
                    if (reserved.Count > 1)
                    {
                        return Blocked(observation, configurationIdentity, null, null, null,
                            ["Multiple open paper positions require an explicit continuation application."]);
                    }

                    if (reserved.Count == 1)
                    {
                        LocalPaperObservationRecord active = reserved[0];
                        if (!StringComparer.Ordinal.Equals(active.Status, "complete")
                            || active.ApplicationJson is null)
                        {
                            return Blocked(observation, configurationIdentity,
                                active.PlanIdentity, active.RiskInputIdentity, active.ModelIdentity,
                                ["An incomplete paper continuation is not recoverable automatically."]);
                        }

                        simulationApplication = DeserializeApplication(active.ApplicationJson);
                        List<string> continuationReasons = ValidateApplication(envelope,
                            simulationApplication, expectedLedgerEquity: null);
                        if (continuationReasons.Count != 0)
                        {
                            return Blocked(observation, configurationIdentity,
                                active.PlanIdentity, active.RiskInputIdentity, active.ModelIdentity,
                                continuationReasons);
                        }

                        risk = RiskEvaluationEngine.Evaluate(
                            simulationApplication.RiskEvaluationInput,
                            _configuration.RiskConfiguration, _riskMathCalculator);
                        if (!risk.IsApproved)
                        {
                            return Blocked(observation, configurationIdentity,
                                active.PlanIdentity, active.RiskInputIdentity, active.ModelIdentity,
                                risk.Reasons, risk);
                        }

                        if (risk.RiskMathResult is null)
                        {
                            return Blocked(observation, configurationIdentity,
                                active.PlanIdentity, active.RiskInputIdentity, active.ModelIdentity,
                                ["Risk math evidence is unavailable."], risk);
                        }

                        planIdentity = active.PlanIdentity
                            ?? throw new InvalidDataException(
                                "An active paper reservation has no plan identity.");
                        riskInputIdentity = active.RiskInputIdentity
                            ?? throw new InvalidDataException(
                                "An active paper reservation has no risk identity.");
                        modelIdentity = active.ModelIdentity
                            ?? throw new InvalidDataException(
                                "An active paper reservation has no model identity.");
                        reservationSequence = active.Sequence;
                    }
                }

                LocalPaperCausalIdentity causal = LocalPaperCausalIdentity.From(envelope);
                observationIdentity = Hash(CanonicalJson(new ObservationIdentityDocument(
                    CanonicalCoreJson(envelope), null, configurationIdentity,
                    riskInputIdentity, planIdentity, modelIdentity, causal)));
            }

            LocalPaperReservationRequest request = BuildPreparationRequest(observation,
                configurationIdentity, observationIdentity, applicationJson, riskInputIdentity,
                planIdentity, modelIdentity, risk);
            InjectFault(LocalPaperFaultPoint.BeforeIntent);
            preparation = _store.Prepare(request);
            if (preparation.Status == LocalPaperPreparationStatus.Blocked)
            {
                return Blocked(observation, configurationIdentity, planIdentity,
                    riskInputIdentity, modelIdentity, [preparation.BlockReason!], risk,
                    durableObservationIdentity: observationIdentity);
            }

            if (preparation.Status == LocalPaperPreparationStatus.AlreadyCompleted)
            {
                LocalPaperLedgerSnapshot? existing = _store.ReadSnapshot(envelope.StreamId);
                return LocalPaperExecutionResult.Create(
                    LocalPaperExecutionStatus.AlreadyProcessed, observation, observationIdentity,
                    configurationIdentity, planIdentity, riskInputIdentity, modelIdentity, null,
                    null, existing, []);
            }

            InjectFault(LocalPaperFaultPoint.AfterIntent);

            if (simulationApplication is { } currentApplication)
            {
                simulation = _simulator.Simulate(currentApplication.SimulatedRiskPlan,
                    _store.ReadEnvelopes(envelope.StreamId));
                List<string> simulationReasons = ValidateSimulation(currentApplication,
                    risk!, simulation);
                if (simulationReasons.Count != 0)
                {
                    TryBlock(observation, observationIdentity, simulationReasons[0]);
                    return Blocked(observation, configurationIdentity, planIdentity,
                        riskInputIdentity, modelIdentity, simulationReasons, risk, simulation,
                        durableObservationIdentity: observationIdentity);
                }
            }

            string resultJson = BuildResultJson(observation, configurationIdentity,
                planIdentity, riskInputIdentity, modelIdentity, risk, simulation);
            string resultIdentity = Hash(CanonicalizeJson(resultJson));
            LocalPaperCommitRequest commit = new(envelope.StreamId,
                envelope.CausalEvent.StableSequence, envelope.CausalEvent.Payload.EventId,
                observationIdentity, resultIdentity, resultJson,
                simulation is null ? [] : ToFillRecords(simulation), preparation.ExpectedRevision,
                releaseReservation: simulation is null || !simulation.IsOpen,
                reservationSequence);
            InjectFault(LocalPaperFaultPoint.BeforeCommit);
            LocalPaperCommitResult committed = _store.Commit(commit);
            InjectFault(LocalPaperFaultPoint.AfterCommitBeforeResponse);
            if (committed.IsBlocked)
            {
                return Blocked(observation, configurationIdentity, planIdentity,
                    riskInputIdentity, modelIdentity, [committed.BlockReason!], risk, simulation,
                    durableObservationIdentity: observationIdentity);
            }

            LocalPaperExecutionStatus status = committed.WasAlreadyCompleted
                ? LocalPaperExecutionStatus.AlreadyProcessed
                : simulationApplication is null
                    ? LocalPaperExecutionStatus.Recorded
                    : LocalPaperExecutionStatus.Applied;
            return LocalPaperExecutionResult.Create(status, observation, observationIdentity,
                configurationIdentity, planIdentity, riskInputIdentity, modelIdentity,
                status == LocalPaperExecutionStatus.AlreadyProcessed ? null : risk,
                status == LocalPaperExecutionStatus.AlreadyProcessed ? null : simulation,
                committed.Snapshot, []);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or InvalidOperationException or JsonException or OverflowException
            or ArgumentException or NotSupportedException or SqliteException)
        {
            string safeConfigurationIdentity = TryHashConfiguration();
            if (preparation is
                {
                    Status: LocalPaperPreparationStatus.Prepared
                or LocalPaperPreparationStatus.AlreadyPrepared
                }
                && simulationApplication is not null)
            {
                // Once preparation succeeded, the exact canonical identity is
                // already assigned. Preserve it while fail-closing a simulation
                // exception; rebuilding an envelope-only identity would leave
                // the durable intent pending.
                TryBlock(observation, observationIdentity, exception.Message);
            }

            return Blocked(observation, safeConfigurationIdentity, planIdentity,
                riskInputIdentity, modelIdentity, [exception.Message], risk, simulation,
                durableObservationIdentity: observationIdentity);
        }
    }

    /// <summary>Alias retaining the explicit observation terminology.</summary>
    public LocalPaperExecutionResult ProcessObservation(LocalPaperObservation observation) =>
        Process(observation);

    private LocalPaperReservationRequest BuildPreparationRequest(LocalPaperObservation observation,
        string configurationIdentity, string observationIdentity, string? applicationJson,
        string? riskInputIdentity, string? planIdentity, string? modelIdentity,
        RiskEvaluationResult? risk)
    {
        PortfolioLimits? portfolioLimits = _configuration.RiskConfiguration.PortfolioLimits;
        decimal? maxRisk = portfolioLimits?.MaxRisk.Amount;
        decimal? maxNotional = portfolioLimits?.MaxNotionalExposure.Amount;
        int? maxSlots = portfolioLimits?.MaxPositionSlots;
        decimal riskAmount = risk?.RiskMathResult?.RiskBudget.Amount ?? 0m;
        decimal notional = risk?.RiskMathResult?.NotionalExposure.Amount ?? 0m;
        int slots = observation.Application is null
            ? 0
            : risk?.Input.CandidatePositionSlots
                ?? throw new InvalidOperationException("Risk capacity evidence is unavailable.");
        LocalPaperLedgerSnapshot? persistedLedger = _store.ReadSnapshot(
            observation.Envelope.StreamId);
        decimal streamInitialEquity = persistedLedger?.InitialEquity
            ?? _configuration.InitialEquity.Amount;
        string streamCurrency = persistedLedger?.Currency
            ?? _configuration.InitialEquity.Currency;
        string envelopeJson = CanonicalCoreJson(observation.Envelope);
        string? application = applicationJson;
        return new LocalPaperReservationRequest(observation.Envelope.StreamId,
            observation.Envelope.CausalEvent.StableSequence,
            observation.Envelope.CausalEvent.Payload.EventId, envelopeJson, application,
            observationIdentity, configurationIdentity, planIdentity, riskInputIdentity,
            modelIdentity, streamInitialEquity, streamCurrency, riskAmount, notional, slots, maxRisk,
            maxNotional, maxSlots);
    }

    private decimal ReadCurrentLedgerEquity(string streamId) =>
        _store.ReadSnapshot(streamId)?.Equity ?? _configuration.InitialEquity.Amount;

    private List<string> ValidateApplication(ReplayEnvelope envelope,
        LocalPaperApplication application, decimal? expectedLedgerEquity)
    {
        List<string> reasons = [];
        RiskEvaluationInput input = application.RiskEvaluationInput;
        SimulatedRiskPlan plan = application.SimulatedRiskPlan;
        if (input.RiskMathInput != plan.RiskInput)
        {
            reasons.Add("The risk input does not match the simulation plan.");
        }

        if (input.DecisionEvidence is null || plan.Decision is null
            || input.DecisionEvidence != plan.Decision)
        {
            reasons.Add("The decision identity does not match the risk and simulation plan.");
        }

        if (plan.Decision?.Outcome != DecisionOutcome.Allow)
        {
            reasons.Add("Only an ALLOW decision can enter local paper simulation.");
        }

        if (plan.FillAssumptions.Version is null)
        {
            reasons.Add("The fill-simulation model version is unavailable.");
        }
        else if (plan.FillAssumptions != _configuration.FillAssumptions)
        {
            reasons.Add("The simulation plan fill assumptions differ from configuration.");
        }

        if (plan.FillAssumptions.Version is not null
            && plan.FillAssumptions.Version.Value != _fillSimulator.Version.Value)
        {
            reasons.Add("The injected fill-simulation model version differs from the plan.");
        }

        if (!StringComparer.Ordinal.Equals(input.RiskMathInput.Equity.Currency,
                _configuration.InitialEquity.Currency))
        {
            reasons.Add("Risk equity currency differs from local-paper configuration.");
        }

        if (expectedLedgerEquity is decimal ledgerEquity
            && input.RiskMathInput.Equity.Amount != ledgerEquity)
        {
            reasons.Add("Risk equity does not match current virtual ledger equity.");
        }

        if (input.RiskMathInput.RiskFraction != _configuration.RiskFraction)
        {
            reasons.Add("Risk fraction differs from local-paper configuration.");
        }

        if (input.Cursor.AsOf.Value > envelope.CausalEvent.AvailabilityTime.Value
            || input.Cursor.MaxStableSequence > envelope.CausalEvent.StableSequence)
        {
            reasons.Add("Risk evidence is from after the observation causal boundary.");
        }

        if (plan.DecisionTime.Value > envelope.CausalEvent.Payload.EventTime.Value)
        {
            reasons.Add("The simulation plan uses a future decision time.");
        }

        return reasons;
    }

    private static List<string> ValidateSimulation(LocalPaperApplication application,
        RiskEvaluationResult risk, SimulatedRiskSimulationResult simulation)
    {
        List<string> reasons = [];
        if (simulation.RiskResult is null || risk.RiskMathResult is null
            || simulation.RiskResult != risk.RiskMathResult)
        {
            reasons.Add("The simulation risk result does not match shared risk evidence.");
        }

        if (simulation.FillSimulationVersion != application.SimulatedRiskPlan.FillAssumptions.Version)
        {
            reasons.Add("The fill-simulation model changed during processing.");
        }

        if (simulation.Diagnostics.Any(static diagnostic =>
                diagnostic.Contains("partial protective fill", StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add("Unsupported partial protective fill remains open.");
        }

        return reasons;
    }

    private static LocalPaperApplication DeserializeApplication(string applicationJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(applicationJson);
            JsonElement root = document.RootElement;
            string riskInputJson = root.GetProperty("riskInputJson").GetString()
                ?? throw new JsonException("The paper risk input is missing.");
            string planJson = root.GetProperty("planJson").GetString()
                ?? throw new JsonException("The paper simulation plan is missing.");
            RiskEvaluationInput riskInput;
            try
            {
                riskInput = CoreJsonSerializer.Deserialize<RiskEvaluationInput>(riskInputJson);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException($"The persisted risk input is corrupt: {exception.Message}",
                    exception);
            }

            SimulatedRiskPlan plan;
            try
            {
                plan = DeserializeSimulationPlan(planJson);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException($"The persisted simulation plan is corrupt: {exception.Message}",
                    exception);
            }

            return new LocalPaperApplication(riskInput, plan);
        }
        catch (Exception exception) when (exception is KeyNotFoundException
            or InvalidOperationException or JsonException or NotSupportedException
            or ArgumentException or FormatException)
        {
            throw new InvalidDataException($"The persisted paper application is corrupt: {exception.Message}",
                exception);
        }
    }

    private static SimulatedRiskPlan DeserializeSimulationPlan(string planJson)
    {
        using JsonDocument document = JsonDocument.Parse(planJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The persisted simulation plan must be a JSON object.");
        }

        string researchPlanId = RequiredString(root, "researchPlanId");
        JsonElement instrument = RequiredProperty(root, "instrumentId");
        InstrumentId instrumentId = new(RequiredString(instrument, "value"));
        Direction direction = RequiredEnum<Direction>(root, "direction");
        JsonElement decisionTime = RequiredProperty(root, "decisionTime");
        UtcTimestamp timestamp = new(RequiredDateTimeOffset(decisionTime, "value"));
        RiskMathInput riskInput = CoreJsonSerializer.Deserialize<RiskMathInput>(
            RequiredProperty(root, "riskInput").GetRawText());

        List<SimulatedTakeProfit> takeProfitSteps = [];
        foreach (JsonElement step in RequiredProperty(root, "takeProfitSteps").EnumerateArray())
        {
            takeProfitSteps.Add(new SimulatedTakeProfit(
                RequiredString(step, "stepId"),
                new Price(RequiredValue(step, "price")),
                RequiredDecimal(step, "fraction")));
        }

        JsonElement holdingPeriod = RequiredProperty(root, "holdingPeriod");
        TimeSpan? duration = holdingPeriod.ValueKind == JsonValueKind.Null
            ? null
            : TimeSpan.Parse(RequiredString(holdingPeriod), CultureInfo.InvariantCulture);
        bool moveStopToBreakeven = RequiredProperty(root, "moveStopToBreakeven").GetBoolean();
        JsonElement breakeven = RequiredProperty(root, "breakevenStopPrice");
        Price? breakevenStopPrice = breakeven.ValueKind == JsonValueKind.Null
            ? null
            : new Price(RequiredValue(breakeven, "value"));
        SimulatedFillAssumptions fillAssumptions = DeserializeFillAssumptions(
            RequiredProperty(root, "fillAssumptions"));
        Decision? decision = DeserializeDecision(RequiredProperty(root, "decision"));

        return new SimulatedRiskPlan(researchPlanId, instrumentId, direction, timestamp,
            riskInput, takeProfitSteps, duration, moveStopToBreakeven, breakevenStopPrice,
            fillAssumptions, decision);
    }

    private static SimulatedFillAssumptions DeserializeFillAssumptions(JsonElement element)
    {
        JsonElement descriptions = RequiredProperty(element, "descriptions");
        FillSimulationAssumptions description = new(
            RequiredString(descriptions, "fees"),
            RequiredString(descriptions, "slippage"),
            RequiredString(descriptions, "latency"),
            RequiredString(descriptions, "liquidityAndQueue"),
            RequiredString(descriptions, "partialFills"),
            RequiredString(descriptions, "intrabarOrdering"),
            RequiredString(descriptions, "dataLimitations"));
        JsonElement version = RequiredProperty(element, "version");
        FillSimulationVersion? fillVersion = version.ValueKind == JsonValueKind.Null
            ? null
            : new FillSimulationVersion(RequiredString(version, "value"));
        return new SimulatedFillAssumptions(RequiredDecimal(element, "feePerQuantityUnit"),
            RequiredDecimal(element, "slippagePerQuantityUnit"), description, fillVersion);
    }

    private static Decision? DeserializeDecision(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        JsonElement reasons = RequiredProperty(element, "reasons");
        return new Decision(RequiredString(element, "decisionId"),
            RequiredString(element, "setupId"),
            RequiredEnum<DecisionOutcome>(element, "outcome"),
            reasons.EnumerateArray().Select(static reason => reason.GetString()
                ?? throw new JsonException("A persisted decision reason cannot be null.")));
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new JsonException($"The persisted contract is missing '{name}'.");

    private static string RequiredString(JsonElement parent, string? name = null)
    {
        JsonElement value = name is null ? parent : RequiredProperty(parent, name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            && !String.IsNullOrWhiteSpace(text)
            ? text
            : throw new JsonException($"The persisted contract value '{name ?? "value"}' is invalid.");
    }

    private static decimal RequiredDecimal(JsonElement parent, string name) =>
        RequiredProperty(parent, name).ValueKind == JsonValueKind.Number
            ? RequiredProperty(parent, name).GetDecimal()
            : throw new JsonException($"The persisted contract value '{name}' is invalid.");

    private static decimal RequiredValue(JsonElement parent, string name) =>
        RequiredDecimal(RequiredProperty(parent, name), "value");

    private static DateTimeOffset RequiredDateTimeOffset(JsonElement parent, string name) =>
        RequiredProperty(parent, name).ValueKind == JsonValueKind.String
            ? RequiredProperty(parent, name).GetDateTimeOffset()
            : throw new JsonException($"The persisted timestamp value '{name}' is invalid.");

    private static TEnum RequiredEnum<TEnum>(JsonElement parent, string name)
        where TEnum : struct, Enum
    {
        string text = RequiredString(parent, name);
        return Enum.TryParse<TEnum>(text, ignoreCase: true, out TEnum value)
            && Enum.IsDefined(value)
            ? value
            : throw new JsonException($"The persisted enum value '{name}' is invalid.");
    }

    private static ReadOnlyCollection<LocalPaperFillRecord> ToFillRecords(
        SimulatedRiskSimulationResult simulation)
    {
        string currency = simulation.Plan.RiskInput.Equity.Currency;
        return new ReadOnlyCollection<LocalPaperFillRecord>(simulation.Fills.Select(fill =>
            new LocalPaperFillRecord(fill.FillId, simulation.Plan.InstrumentId.Value,
                simulation.Plan.Direction, fill.Role == SimulatedFillRole.Entry,
                fill.Role.ToString(), fill.Quantity.Value, fill.Price.Value, fill.Fee.Amount,
                fill.Slippage.Amount, currency, fill.FilledAt.Value.UtcDateTime.Ticks,
                fill.StableSequence, fill.EventId)).ToArray());
    }

    private static string BuildResultJson(LocalPaperObservation observation,
        string configurationIdentity, string? planIdentity, string? riskInputIdentity,
        string? modelIdentity, RiskEvaluationResult? risk,
        SimulatedRiskSimulationResult? simulation)
    {
        string? riskJson = risk is null ? null : CanonicalCoreJson(risk);
        string? planJson = simulation is null ? null : CanonicalCoreJson(simulation.Plan);
        string? assumptionsJson = simulation is null
            ? null
            : CanonicalCoreJson(simulation.Plan.FillAssumptions);
        IReadOnlyList<LocalPaperFillRecord> fills = simulation is null
            ? []
            : ToFillRecords(simulation);
        ResultDocument document = new(observation.Envelope.StreamId,
            observation.Envelope.CausalEvent.StableSequence,
            observation.Envelope.CausalEvent.Payload.EventId, configurationIdentity,
            planIdentity, riskInputIdentity, modelIdentity, riskJson, planJson, assumptionsJson,
            fills, simulation?.RemainingQuantity,
            simulation?.ExitReason.ToString(), simulation?.Diagnostics,
            simulation?.TotalFees, simulation?.TotalSlippage,
            LocalPaperCausalIdentity.From(observation.Envelope));
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static LocalPaperExecutionResult Blocked(LocalPaperObservation observation,
        string configurationIdentity, string? planIdentity, string? riskInputIdentity,
        string? modelIdentity, IEnumerable<string> reasons, RiskEvaluationResult? risk = null,
        SimulatedRiskSimulationResult? simulation = null,
        LocalPaperLedgerSnapshot? ledger = null,
        string? durableObservationIdentity = null) => LocalPaperExecutionResult.Create(
        LocalPaperExecutionStatus.Blocked, observation,
        durableObservationIdentity ?? TryObservationIdentity(observation, configurationIdentity),
        configurationIdentity,
        planIdentity, riskInputIdentity, modelIdentity, risk, simulation, ledger, reasons);

    private void TryBlock(LocalPaperObservation observation, string observationIdentity,
        string reason)
    {
        try
        {
            _store.Block(new LocalPaperBlockRequest(observation.Envelope.StreamId,
                observation.Envelope.CausalEvent.StableSequence, observationIdentity, reason));
        }
        catch (InvalidDataException)
        {
            // The primary result remains fail-closed; a corrupt store is not
            // repaired or overwritten by an executor retry.
        }
        catch (InvalidOperationException)
        {
            // A competing recovery writer may have completed the exact intent.
        }
        catch (SqliteException)
        {
            // A busy or unavailable database cannot justify a retry or release.
        }
    }

    private string TryHashConfiguration()
    {
        try
        {
            return Hash(CanonicalizeJson(_configuration.ToJson()));
        }
        catch (Exception)
        {
            return "configuration-unavailable";
        }
    }

    private static string TryObservationIdentity(LocalPaperObservation observation,
        string configurationIdentity)
    {
        try
        {
            return Hash(CanonicalJson(new ObservationIdentityDocument(
                CanonicalCoreJson(observation.Envelope), null, configurationIdentity, null,
                null, null, LocalPaperCausalIdentity.From(observation.Envelope))));
        }
        catch (Exception)
        {
            return "observation-unavailable";
        }
    }

    private static string CanonicalCoreJson<T>(T value) =>
        CanonicalizeJson(CoreJsonSerializer.Serialize(value));

    private static string CanonicalJson<T>(T value) =>
        CanonicalizeJson(JsonSerializer.Serialize(value, JsonOptions));

    private static string CanonicalizeJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, JsonOptions);
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    private void InjectFault(LocalPaperFaultPoint point) => _faultInjector?.Invoke(point);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private sealed record ApplicationDocument(string RiskInputJson, string PlanJson,
        string FillAssumptionsJson);

    private sealed record IdentityDocument(string ContentJson, LocalPaperCausalIdentity Causal);

    private sealed record ObservationIdentityDocument(string EnvelopeJson,
        string? ApplicationJson, string ConfigurationIdentity, string? RiskInputIdentity,
        string? PlanIdentity, string? ModelIdentity, LocalPaperCausalIdentity Causal);

    private sealed record ResultDocument(string StreamId, long Sequence, string EventId,
        string ConfigurationIdentity, string? PlanIdentity, string? RiskInputIdentity,
        string? ModelIdentity, string? RiskJson, string? PlanJson,
        string? FillAssumptionsJson, IReadOnlyList<LocalPaperFillRecord> Fills,
        decimal? RemainingQuantity, string? ExitReason, IReadOnlyList<string>? Diagnostics,
        decimal? TotalFees, decimal? TotalSlippage, LocalPaperCausalIdentity Causal);
}

/// <summary>All causal fields used in local-paper identities and reports.</summary>
public sealed record LocalPaperCausalIdentity
{
    public long EventTimeTicks { get; }
    public long? ExchangeTimeTicks { get; }
    public long ReceiveTimeTicks { get; }
    public long AvailabilityTimeTicks { get; }
    public long StableSequence { get; }

    private LocalPaperCausalIdentity(long eventTimeTicks, long? exchangeTimeTicks,
        long receiveTimeTicks, long availabilityTimeTicks, long stableSequence)
    {
        EventTimeTicks = eventTimeTicks;
        ExchangeTimeTicks = exchangeTimeTicks;
        ReceiveTimeTicks = receiveTimeTicks;
        AvailabilityTimeTicks = availabilityTimeTicks;
        StableSequence = stableSequence;
    }

    public static LocalPaperCausalIdentity From(ReplayEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        CausalMarketEvent causal = envelope.CausalEvent;
        return new LocalPaperCausalIdentity(causal.Payload.EventTime.Value.UtcDateTime.Ticks,
            causal.ExchangeTime?.Value.UtcDateTime.Ticks,
            causal.ReceiveTime.Value.UtcDateTime.Ticks,
            causal.AvailabilityTime.Value.UtcDateTime.Ticks, causal.StableSequence);
    }
}
