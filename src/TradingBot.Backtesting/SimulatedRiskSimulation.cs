using System.Collections.ObjectModel;
using TradingBot.Core;

namespace TradingBot.Backtesting;

/// <summary>Identifies a research fill produced by the simulated risk harness.</summary>
public enum SimulatedFillRole
{
    Entry,
    StopLoss,
    BreakevenStop,
    TakeProfit1,
    TakeProfit2,
    TimeExit
}

/// <summary>Identifies why the simulated position stopped or remained open.</summary>
public enum SimulatedRiskExitReason
{
    None,
    StopLoss,
    BreakevenStop,
    TakeProfit1,
    TakeProfit2,
    TimeExit,
    EndOfData
}

/// <summary>One explicitly configured take-profit level and fraction.</summary>
public sealed record SimulatedTakeProfit
{
    public string StepId { get; }

    public Price Price { get; }

    public decimal Fraction { get; }

    public SimulatedTakeProfit(string stepId, Price price, decimal fraction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentNullException.ThrowIfNull(price);
        if (fraction <= 0m || fraction > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction,
                "A take-profit fraction must be greater than zero and no greater than one.");
        }

        StepId = stepId;
        Price = price;
        Fraction = fraction;
    }
}

/// <summary>Explicit numeric and prose assumptions for research fills.</summary>
public sealed record SimulatedFillAssumptions
{
    public decimal FeePerQuantityUnit { get; }

    public decimal SlippagePerQuantityUnit { get; }

    public FillSimulationAssumptions Descriptions { get; }

    /// <summary>Gets the caller supplied assumption version, when declared.</summary>
    public FillSimulationVersion? Version { get; }

    public SimulatedFillAssumptions(decimal feePerQuantityUnit,
        decimal slippagePerQuantityUnit, FillSimulationAssumptions descriptions)
        : this(feePerQuantityUnit, slippagePerQuantityUnit, descriptions, null)
    {
    }

    /// <summary>Creates numeric assumptions with an explicit version identifier.</summary>
    public SimulatedFillAssumptions(decimal feePerQuantityUnit,
        decimal slippagePerQuantityUnit, FillSimulationAssumptions descriptions,
        FillSimulationVersion? version)
    {
        if (feePerQuantityUnit < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(feePerQuantityUnit),
                feePerQuantityUnit, "A research fee cannot be negative.");
        }

        if (slippagePerQuantityUnit < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(slippagePerQuantityUnit),
                slippagePerQuantityUnit, "Research slippage cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(descriptions);
        FeePerQuantityUnit = feePerQuantityUnit;
        SlippagePerQuantityUnit = slippagePerQuantityUnit;
        Descriptions = descriptions;
        Version = version;
    }

    /// <summary>Alias used by callers that describe fees as a per-unit cost.</summary>
    public decimal FeesPerQuantityUnit => FeePerQuantityUnit;

    /// <summary>Alias used by callers that describe slippage as a per-unit cost.</summary>
    public decimal SlippagePerUnit => SlippagePerQuantityUnit;
}

/// <summary>
/// Default research fill model. The risk harness selects the event, role, and
/// reference execution price; this model turns that one causal observation into
/// a fill observation and applies no risk or execution policy of its own.
/// </summary>
public sealed class OhlcvFillSimulator : IFillSimulator
{
    /// <summary>Stable identifier for the default research fill model.</summary>
    public static FillSimulationVersion CurrentVersion { get; } =
        new("ohlcv-fill-v1");

    /// <inheritdoc />
    public FillSimulationVersion Version => CurrentVersion;

    /// <inheritdoc />
    public FillSimulationResult Simulate(FillSimulationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Price referencePrice = input.ReferencePrice
            ?? throw new InvalidOperationException(
                "The default fill model requires a caller-supplied reference price.");
        CausalMarketEvent latest = input.MarketSnapshot
            .OrderBy(static item => item.StableSequence)
            .Last();
        ResearchFill fill = new(
            input.HypotheticalRequestId + ":fill",
            input.RequestedQuantity,
            referencePrice,
            input.RequestedAt,
            latest.StableSequence);
        return new FillSimulationResult(Version, input, [fill]);
    }
}

/// <summary>
/// Immutable, research-only risk plan. The injected calculator supplies the
/// quantity and risk result; this plan supplies only caller-declared exit and fill
/// assumptions.
/// </summary>
public sealed record SimulatedRiskPlan
{
    public string ResearchPlanId { get; }

    /// <summary>Gets the Core decision that authorized this research hypothesis.</summary>
    public Decision? Decision { get; }

    /// <summary>Gets the linked Core decision identifier, when supplied.</summary>
    public string? DecisionId => Decision?.DecisionId;

    public InstrumentId InstrumentId { get; }

    public Direction Direction { get; }

    public UtcTimestamp DecisionTime { get; }

    public RiskMathInput RiskInput { get; }

    public ImmutableValueList<SimulatedTakeProfit> TakeProfitSteps { get; }

    public TimeSpan? HoldingPeriod { get; }

    public bool MoveStopToBreakeven { get; }

    public Price? BreakevenStopPrice { get; }

    public SimulatedFillAssumptions FillAssumptions { get; }

    public SimulatedRiskPlan(string researchPlanId, InstrumentId instrumentId,
        Direction direction, UtcTimestamp decisionTime, RiskMathInput riskInput,
        IEnumerable<SimulatedTakeProfit> takeProfitSteps, TimeSpan? holdingPeriod,
        bool moveStopToBreakeven, Price? breakevenStopPrice,
        SimulatedFillAssumptions fillAssumptions, Decision? decision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(researchPlanId);
        ArgumentNullException.ThrowIfNull(instrumentId);
        BacktestingContractGuard.RequireDefined(direction, nameof(direction));
        ArgumentNullException.ThrowIfNull(decisionTime);
        ArgumentNullException.ThrowIfNull(riskInput);
        ArgumentNullException.ThrowIfNull(takeProfitSteps);
        ArgumentNullException.ThrowIfNull(fillAssumptions);
        if (holdingPeriod is TimeSpan duration && duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(holdingPeriod), duration,
                "A configured holding period must be positive.");
        }

        SimulatedTakeProfit[] targets = takeProfitSteps.ToArray();
        if (targets.Any(static target => target is null))
        {
            throw new ArgumentException("Take-profit steps cannot contain null values.",
                nameof(takeProfitSteps));
        }

        if (targets.Length > 2)
        {
            throw new ArgumentException("At most TP1 and TP2 may be configured.",
                nameof(takeProfitSteps));
        }

        decimal fractionTotal = 0m;
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (SimulatedTakeProfit target in targets)
        {
            if (!ids.Add(target.StepId))
            {
                throw new ArgumentException("Take-profit step identifiers must be unique.",
                    nameof(takeProfitSteps));
            }

            fractionTotal = checked(fractionTotal + target.Fraction);
        }

        if (fractionTotal > 1m)
        {
            throw new ArgumentException(
                "Take-profit fractions cannot exceed the injected risk quantity.",
                nameof(takeProfitSteps));
        }

        if (riskInput.InstrumentId != instrumentId || riskInput.Direction != direction)
        {
            throw new ArgumentException(
                "The risk input instrument and direction must match the research plan.",
                nameof(riskInput));
        }

        if (moveStopToBreakeven && breakevenStopPrice is not null
            && ((direction == Direction.Long && breakevenStopPrice.Value <= riskInput.ProtectiveStopPrice.Value)
                || (direction == Direction.Short && breakevenStopPrice.Value >= riskInput.ProtectiveStopPrice.Value)))
        {
            throw new ArgumentException(
                "A breakeven stop must be on the profitable side of the protective stop.",
                nameof(breakevenStopPrice));
        }

        if (moveStopToBreakeven && breakevenStopPrice is null)
        {
            throw new ArgumentException(
                "A breakeven stop price is required when breakeven movement is enabled.",
                nameof(breakevenStopPrice));
        }

        ResearchPlanId = researchPlanId;
        Decision = decision;
        InstrumentId = instrumentId;
        Direction = direction;
        DecisionTime = decisionTime;
        RiskInput = riskInput;
        TakeProfitSteps = new ImmutableValueList<SimulatedTakeProfit>(targets);
        HoldingPeriod = holdingPeriod;
        MoveStopToBreakeven = moveStopToBreakeven;
        BreakevenStopPrice = breakevenStopPrice;
        FillAssumptions = fillAssumptions;
    }
}

/// <summary>One immutable simulated execution observation.</summary>
public sealed record SimulatedFill
{
    public string FillId { get; }

    public SimulatedFillRole Role { get; }

    public Quantity Quantity { get; }

    public Price Price { get; }

    public Money Fee { get; }

    public Money Slippage { get; }

    public UtcTimestamp FilledAt { get; }

    public long StableSequence { get; }

    public string EventId { get; }

    public SimulatedFill(string fillId, SimulatedFillRole role, Quantity quantity,
        Price price, Money fee, Money slippage, UtcTimestamp filledAt,
        long stableSequence, string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fillId);
        BacktestingContractGuard.RequireDefined(role, nameof(role));
        ArgumentNullException.ThrowIfNull(quantity);
        if (quantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity.Value,
                "A simulated fill quantity must be positive.");
        }

        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(fee);
        ArgumentNullException.ThrowIfNull(slippage);
        ArgumentNullException.ThrowIfNull(filledAt);
        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "A simulated fill sequence cannot be negative.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        FillId = fillId;
        Role = role;
        Quantity = quantity;
        Price = price;
        Fee = fee;
        Slippage = slippage;
        FilledAt = filledAt;
        StableSequence = stableSequence;
        EventId = eventId;
    }
}

/// <summary>Immutable result of one simulated risk and fill run.</summary>
public sealed class SimulatedRiskSimulationResult
{
    internal SimulatedRiskSimulationResult(SimulatedRiskPlan plan,
        RiskMathResult riskResult, IEnumerable<SimulatedFill> fills,
        decimal remainingQuantity, SimulatedRiskExitReason exitReason,
        IEnumerable<string> diagnostics, decimal totalFees, decimal totalSlippage,
        FillSimulationVersion fillSimulationVersion)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(riskResult);
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(fillSimulationVersion);
        BacktestingContractGuard.RequireDefined(exitReason, nameof(exitReason));
        Plan = plan;
        RiskResult = riskResult;
        Fills = new ReadOnlyCollection<SimulatedFill>(fills.ToList());
        if (remainingQuantity < 0m)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(remainingQuantity,
                nameof(remainingQuantity));
        }

        RemainingQuantity = remainingQuantity;
        ExitReason = exitReason;
        Diagnostics = new ReadOnlyCollection<string>(diagnostics.ToList());
        TotalFees = totalFees;
        TotalSlippage = totalSlippage;
        FillSimulationVersion = fillSimulationVersion;
    }

    public SimulatedRiskPlan Plan { get; }

    public RiskMathResult RiskResult { get; }

    public IReadOnlyList<SimulatedFill> Fills { get; }

    public IReadOnlyList<SimulatedFill> ResearchFills => Fills;

    public decimal RemainingQuantity { get; }

    public SimulatedRiskExitReason ExitReason { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    public decimal TotalFees { get; }

    public decimal TotalSlippage { get; }

    /// <summary>Gets the version of the injected research fill model.</summary>
    public FillSimulationVersion FillSimulationVersion { get; }

    public bool IsOpen => RemainingQuantity > 0m;

    public bool IsResearchOnly => RiskResult is not null;
}

/// <summary>
/// Runs deterministic, research-only risk and OHLCV fill assumptions using an
/// injected shared risk-math calculator. It never emits authorization or order
/// state.
/// </summary>
public sealed class SimulatedRiskSimulator
{
    private readonly IRiskMathCalculator _riskCalculator;
    private readonly IFillSimulator _fillSimulator;

    public SimulatedRiskSimulator(IRiskMathCalculator riskCalculator)
        : this(riskCalculator, new OhlcvFillSimulator())
    {
    }

    /// <summary>Creates a simulator with a replaceable research fill model.</summary>
    public SimulatedRiskSimulator(IRiskMathCalculator riskCalculator,
        IFillSimulator fillSimulator)
    {
        ArgumentNullException.ThrowIfNull(riskCalculator);
        ArgumentNullException.ThrowIfNull(fillSimulator);
        _riskCalculator = riskCalculator;
        _fillSimulator = fillSimulator;
    }

    /// <summary>Simulates one plan over one persisted causal event stream.</summary>
    public SimulatedRiskSimulationResult Simulate(SimulatedRiskPlan plan,
        IEnumerable<ReplayEnvelope> events)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(events);

        if (plan.Decision is null)
        {
            throw new InvalidOperationException(
                "A research simulation requires an explicit Core decision link.");
        }

        if (plan.FillAssumptions.Version is null)
        {
            throw new InvalidOperationException(
                "A research simulation requires an explicit fill-assumption version.");
        }

        List<ReplayEnvelope> source = events.ToList();
        if (source.Count == 0)
        {
            throw new ArgumentException("A simulation requires at least one replay event.",
                nameof(events));
        }

        string streamId = source[0]?.StreamId
            ?? throw new ArgumentException("Replay events cannot contain null envelopes.",
                nameof(events));
        ReplayRunResult replay = HistoricalReplayRunner.Run(streamId, source,
            static context => context.Event);
        if (!replay.Succeeded)
        {
            throw new InvalidOperationException(
                "The causal event stream could not be replayed for simulation.", replay.Failure);
        }

        RiskMathResult riskResult = _riskCalculator.Calculate(plan.RiskInput);
        if (riskResult.Input != plan.RiskInput
            || riskResult.Input.InstrumentId != plan.InstrumentId
            || riskResult.Input.Direction != plan.Direction
            || riskResult.Version != _riskCalculator.Version)
        {
            throw new InvalidOperationException(
                "The injected risk result does not match the research plan input or calculator version.");
        }

        List<ReplayStepResult> barSteps = replay.Steps
            .Where(static step => step.Event is Bar)
            .ToList();
        if (replay.Steps.Any(step => step.Event.InstrumentId != plan.InstrumentId))
        {
            throw new ArgumentException("Every simulation bar must use the plan instrument.",
                nameof(events));
        }

        int entryIndex = barSteps.FindIndex(step =>
            ((Bar)step.Event).OpenTime.Value > plan.DecisionTime.Value);
        if (entryIndex < 0)
        {
            throw new InvalidOperationException(
                "No bar opened after the decision time; an entry cannot be simulated.");
        }

        Bar entryBar = (Bar)barSteps[entryIndex].Event;
        decimal quantity = riskResult.RawQuantity.Value;
        List<SimulatedFill> fills = [];
        List<string> diagnostics = [];
        decimal totalFees = 0m;
        decimal totalSlippage = 0m;

        decimal entryBase = entryBar.Open.Value;
        decimal entryPrice = ApplySlippage(entryBase, plan.Direction,
            plan.FillAssumptions.SlippagePerQuantityUnit, SimulatedFillRole.Entry);
        decimal entryFilled = AddFill(fills, diagnostics, plan, SimulatedFillRole.Entry,
            quantity, entryPrice, entryBase, entryBar.CloseTime, barSteps, entryIndex,
            ref totalFees, ref totalSlippage);
        if (entryFilled != quantity)
        {
            throw new InvalidOperationException(
                "The research fill model must fill the requested entry quantity.");
        }

        decimal remaining = quantity;
        decimal stop = riskResult.Input.ProtectiveStopPrice.Value;
        bool breakevenActivated = false;
        int targetIndex = 0;
        SimulatedRiskExitReason exitReason = SimulatedRiskExitReason.None;

        for (int index = entryIndex + 1; index < barSteps.Count && remaining > 0m; index++)
        {
            Bar bar = (Bar)barSteps[index].Event;
            ReplayStepResult step = barSteps[index];
            bool stopHit = HitsStop(bar, plan.Direction, stop);
            bool targetHit = targetIndex < plan.TakeProfitSteps.Count
                && HitsTarget(bar, plan.Direction, plan.TakeProfitSteps[targetIndex].Price.Value);

            if (stopHit && targetHit)
            {
                diagnostics.Add($"ambiguous OHLCV bar '{bar.EventId}': stop precedes target");
            }

            if (stopHit)
            {
                decimal basePrice = StopBasePrice(bar, plan.Direction, stop);
                SimulatedFillRole role = breakevenActivated
                    ? SimulatedFillRole.BreakevenStop
                    : SimulatedFillRole.StopLoss;
                decimal executionPrice = ApplySlippage(basePrice, plan.Direction,
                    plan.FillAssumptions.SlippagePerQuantityUnit, role);
                decimal stopFilled = AddFill(fills, diagnostics, plan, role, remaining,
                    executionPrice, basePrice, bar.CloseTime, barSteps, index,
                    ref totalFees, ref totalSlippage);
                remaining = checked(remaining - stopFilled);
                if (stopFilled != 0m && remaining > 0m)
                {
                    diagnostics.Add($"partial protective fill left {remaining} units open");
                }
                exitReason = breakevenActivated
                    ? SimulatedRiskExitReason.BreakevenStop
                    : SimulatedRiskExitReason.StopLoss;
                break;
            }

            while (targetIndex < plan.TakeProfitSteps.Count && remaining > 0m
                && HitsTarget(bar, plan.Direction, plan.TakeProfitSteps[targetIndex].Price.Value))
            {
                SimulatedTakeProfit target = plan.TakeProfitSteps[targetIndex];
                decimal targetQuantity = Math.Min(remaining, checked(quantity * target.Fraction));
                decimal basePrice = TargetBasePrice(bar, plan.Direction, target.Price.Value);
                SimulatedFillRole role = targetIndex == 0
                    ? SimulatedFillRole.TakeProfit1
                    : SimulatedFillRole.TakeProfit2;
                decimal executionPrice = ApplySlippage(basePrice, plan.Direction,
                    plan.FillAssumptions.SlippagePerQuantityUnit, role);
                decimal targetFilled = AddFill(fills, diagnostics, plan, role, targetQuantity,
                    executionPrice, basePrice, bar.CloseTime, barSteps, index,
                    ref totalFees, ref totalSlippage);
                remaining = checked(remaining - targetFilled);
                targetIndex++;

                if (targetIndex == 1 && plan.MoveStopToBreakeven && remaining > 0m)
                {
                    stop = plan.BreakevenStopPrice!.Value;
                    breakevenActivated = true;
                }

                exitReason = role == SimulatedFillRole.TakeProfit1
                    ? SimulatedRiskExitReason.TakeProfit1
                    : SimulatedRiskExitReason.TakeProfit2;
            }

            if (remaining <= 0m)
            {
                break;
            }

            if (plan.HoldingPeriod is TimeSpan holding
                && bar.CloseTime.Value >= entryBar.OpenTime.Value + holding)
            {
                decimal basePrice = bar.Close.Value;
                decimal executionPrice = ApplySlippage(basePrice, plan.Direction,
                    plan.FillAssumptions.SlippagePerQuantityUnit,
                    SimulatedFillRole.TimeExit);
                decimal timeFilled = AddFill(fills, diagnostics, plan,
                    SimulatedFillRole.TimeExit, remaining, executionPrice, basePrice,
                    bar.CloseTime, barSteps, index,
                    ref totalFees, ref totalSlippage);
                remaining = checked(remaining - timeFilled);
                exitReason = SimulatedRiskExitReason.TimeExit;
                break;
            }
        }

        if (remaining > 0m)
        {
            exitReason = SimulatedRiskExitReason.EndOfData;
        }

        return new SimulatedRiskSimulationResult(plan, riskResult, fills, remaining,
            exitReason, diagnostics, totalFees, totalSlippage, _fillSimulator.Version);
    }

    private decimal AddFill(List<SimulatedFill> fills, List<string> diagnostics,
        SimulatedRiskPlan plan, SimulatedFillRole role, decimal quantity, decimal executionPrice,
        decimal basePrice, UtcTimestamp requestedAt, IReadOnlyList<ReplayStepResult> barSteps,
        int barIndex,
        ref decimal totalFees, ref decimal totalSlippage)
    {
        if (quantity <= 0m)
        {
            return 0m;
        }

        FillSimulationInput input = new(
            $"{plan.ResearchPlanId}:{role}:{fills.Count + 1}",
            plan.InstrumentId,
            plan.Direction,
            new Quantity(quantity),
            requestedAt,
            barSteps.Take(barIndex + 1).Select(static step => step.Envelope.CausalEvent),
            plan.FillAssumptions.Descriptions,
            new Price(executionPrice));
        FillSimulationResult simulation = _fillSimulator.Simulate(input)
            ?? throw new InvalidOperationException("The fill model returned no result.");
        if (simulation.Version != _fillSimulator.Version || simulation.Input != input)
        {
            throw new InvalidOperationException(
                "The fill model returned a result with mismatched input or version.");
        }

        if (simulation.Fills.Count != 1)
        {
            throw new InvalidOperationException(
                "The fill model must return exactly one observation per requested fill.");
        }

        ResearchFill observation = simulation.Fills[0];
        if (observation.FilledQuantity.Value > quantity
            || observation.FilledAt.Value > requestedAt.Value)
        {
            throw new InvalidOperationException(
                "The fill model returned an overfill or a future observation.");
        }

        CausalMarketEvent? source = input.MarketSnapshot.FirstOrDefault(candidate =>
            candidate.StableSequence == observation.StableSequence);
        if (source is null)
        {
            throw new InvalidOperationException(
                "The fill model returned an observation outside the causal snapshot.");
        }

        decimal filledQuantity = observation.FilledQuantity.Value;
        decimal fee = checked(filledQuantity * plan.FillAssumptions.FeePerQuantityUnit);
        decimal slippage = checked(Math.Abs(observation.FillPrice.Value - basePrice)
            * filledQuantity);
        totalFees = checked(totalFees + fee);
        totalSlippage = checked(totalSlippage + slippage);
        string fillId = $"{plan.ResearchPlanId}:{fills.Count + 1}";
        string currency = plan.RiskInput.Equity.Currency;
        fills.Add(new SimulatedFill(fillId, role, new Quantity(filledQuantity),
            observation.FillPrice, new Money(fee, currency),
            new Money(slippage, currency), observation.FilledAt,
            observation.StableSequence, source.Payload.EventId));
        return filledQuantity;
    }

    private static decimal ApplySlippage(decimal basePrice, Direction direction,
        decimal slippage, SimulatedFillRole role)
    {
        bool sell = role == SimulatedFillRole.Entry
            ? direction == Direction.Short
            : direction == Direction.Long;
        decimal value = checked(basePrice + (sell ? -slippage : slippage));
        return value > 0m
            ? value
            : throw new OverflowException("Slippage moved a simulated price out of range.");
    }

    private static bool HitsStop(Bar bar, Direction direction, decimal stop) =>
        direction == Direction.Long ? bar.Low.Value <= stop : bar.High.Value >= stop;

    private static decimal StopBasePrice(Bar bar, Direction direction, decimal stop) =>
        direction == Direction.Long
            ? (bar.Open.Value <= stop ? bar.Open.Value : stop)
            : (bar.Open.Value >= stop ? bar.Open.Value : stop);

    private static bool HitsTarget(Bar bar, Direction direction, decimal target) =>
        direction == Direction.Long ? bar.High.Value >= target : bar.Low.Value <= target;

    private static decimal TargetBasePrice(Bar bar, Direction direction, decimal target) =>
        direction == Direction.Long
            ? (bar.Open.Value >= target ? bar.Open.Value : target)
            : (bar.Open.Value <= target ? bar.Open.Value : target);

}

internal static class BacktestingContractGuard
{
    public static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "The enum value is not defined.");
        }
    }
}

/// <summary>Compatibility alias for callers that name the component a backtest simulator.</summary>
public sealed class BacktestRiskSimulator
{
    private readonly SimulatedRiskSimulator _inner;

    public BacktestRiskSimulator(IRiskMathCalculator riskCalculator)
    {
        _inner = new SimulatedRiskSimulator(riskCalculator);
    }

    public BacktestRiskSimulator(IRiskMathCalculator riskCalculator,
        IFillSimulator fillSimulator)
    {
        _inner = new SimulatedRiskSimulator(riskCalculator, fillSimulator);
    }

    public SimulatedRiskSimulationResult Simulate(SimulatedRiskPlan plan,
        IEnumerable<ReplayEnvelope> events) => _inner.Simulate(plan, events);
}
