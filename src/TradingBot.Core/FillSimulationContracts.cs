using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Identifies the immutable version of a fill-simulation contract.</summary>
public sealed record FillSimulationVersion
{
    /// <summary>Gets the non-empty, ordinally compared version value.</summary>
    public string Value { get; }

    /// <summary>Creates a fill-simulation version identifier.</summary>
    [JsonConstructor]
    public FillSimulationVersion(string value)
    {
        ContractGuard.RequireText(value, nameof(value));
        Value = value;
    }
}

/// <summary>
/// Describes every assumption a research fill simulator applies. Each value is
/// prose so unknown parameters are recorded explicitly instead of receiving a
/// numeric default.
/// </summary>
public sealed record FillSimulationAssumptions
{
    /// <summary>Describes the fee schedule or records why it is unknown.</summary>
    public string Fees { get; }

    /// <summary>Describes price slippage or records why it is unknown.</summary>
    public string Slippage { get; }

    /// <summary>Describes request and observation latency or records why it is unknown.</summary>
    public string Latency { get; }

    /// <summary>Describes displayed liquidity and queue treatment or records why it is unknown.</summary>
    public string LiquidityAndQueue { get; }

    /// <summary>Describes partial-fill treatment or records why it is unknown.</summary>
    public string PartialFills { get; }

    /// <summary>Describes the assumed intrabar event order or records why it is unknown.</summary>
    public string IntrabarOrdering { get; }

    /// <summary>Describes data exclusions and limitations.</summary>
    public string DataLimitations { get; }

    /// <summary>Creates a complete, immutable set of research assumptions.</summary>
    [JsonConstructor]
    public FillSimulationAssumptions(string fees, string slippage, string latency,
        string liquidityAndQueue, string partialFills, string intrabarOrdering,
        string dataLimitations)
    {
        Fees = ContractGuard.RequireDescription(fees, nameof(fees));
        Slippage = ContractGuard.RequireDescription(slippage, nameof(slippage));
        Latency = ContractGuard.RequireDescription(latency, nameof(latency));
        LiquidityAndQueue = ContractGuard.RequireDescription(liquidityAndQueue,
            nameof(liquidityAndQueue));
        PartialFills = ContractGuard.RequireDescription(partialFills, nameof(partialFills));
        IntrabarOrdering = ContractGuard.RequireDescription(intrabarOrdering,
            nameof(intrabarOrdering));
        DataLimitations = ContractGuard.RequireDescription(dataLimitations,
            nameof(dataLimitations));
    }
}

/// <summary>
/// Describes one hypothetical research request and the causal market view
/// available to the simulator at its request time.
/// </summary>
public sealed record FillSimulationInput
{
    /// <summary>Gets the research-only identifier for this hypothetical request.</summary>
    public string HypotheticalRequestId { get; }

    /// <summary>Gets the instrument represented by the request and market snapshot.</summary>
    public InstrumentId InstrumentId { get; }

    /// <summary>Gets the hypothetical position direction.</summary>
    public Direction Direction { get; }

    /// <summary>Gets the positive quantity considered by the research simulation.</summary>
    public Quantity RequestedQuantity { get; }

    /// <summary>Gets an optional research reference price.</summary>
    public Price? ReferencePrice { get; }

    /// <summary>Gets the as-of time at which this hypothetical view was formed.</summary>
    public UtcTimestamp RequestedAt { get; }

    /// <summary>Gets the immutable causal market events visible at the request time.</summary>
    public ImmutableValueList<CausalMarketEvent> MarketSnapshot { get; }

    /// <summary>Gets the complete assumptions used by a research simulator.</summary>
    public FillSimulationAssumptions Assumptions { get; }

    /// <summary>
    /// Creates a fill-simulation input and defensively snapshots its causal market view.
    /// </summary>
    public FillSimulationInput(string hypotheticalRequestId, InstrumentId instrumentId,
        Direction direction, Quantity requestedQuantity, UtcTimestamp requestedAt,
        IEnumerable<CausalMarketEvent> marketSnapshot, FillSimulationAssumptions assumptions,
        Price? referencePrice = null)
        : this(hypotheticalRequestId, instrumentId, direction, requestedQuantity, requestedAt,
            SnapshotMarket(marketSnapshot, nameof(marketSnapshot)), assumptions, referencePrice)
    {
    }

    /// <summary>Deserializes an already immutable causal market snapshot.</summary>
    [JsonConstructor]
    public FillSimulationInput(string hypotheticalRequestId, InstrumentId instrumentId,
        Direction direction, Quantity requestedQuantity, UtcTimestamp requestedAt,
        ImmutableValueList<CausalMarketEvent> marketSnapshot,
        FillSimulationAssumptions assumptions, Price? referencePrice = null)
    {
        ContractGuard.RequireText(hypotheticalRequestId, nameof(hypotheticalRequestId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireDefined(direction, nameof(direction));
        ArgumentNullException.ThrowIfNull(requestedQuantity);
        if (requestedQuantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedQuantity),
                requestedQuantity.Value, "A fill-simulation quantity must be greater than zero.");
        }

        ArgumentNullException.ThrowIfNull(requestedAt);
        ArgumentNullException.ThrowIfNull(marketSnapshot);
        ArgumentNullException.ThrowIfNull(assumptions);
        ValidateMarketSnapshot(instrumentId, requestedAt, marketSnapshot);

        HypotheticalRequestId = hypotheticalRequestId;
        InstrumentId = instrumentId;
        Direction = direction;
        RequestedQuantity = requestedQuantity;
        ReferencePrice = referencePrice;
        RequestedAt = requestedAt;
        MarketSnapshot = new ImmutableValueList<CausalMarketEvent>(marketSnapshot);
        Assumptions = assumptions;
    }

    private static ImmutableValueList<CausalMarketEvent> SnapshotMarket(
        IEnumerable<CausalMarketEvent> marketSnapshot, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(marketSnapshot, parameterName);
        ImmutableValueList<CausalMarketEvent> snapshot = new(marketSnapshot);
        if (snapshot.Count == 0 || snapshot.Any(static item => item is null))
        {
            throw new ArgumentException(
                "A causal market snapshot must contain at least one non-null event.",
                parameterName);
        }

        return snapshot;
    }

    private static void ValidateMarketSnapshot(InstrumentId instrumentId,
        UtcTimestamp requestedAt, ImmutableValueList<CausalMarketEvent> marketSnapshot)
    {
        if (marketSnapshot.Count == 0)
        {
            throw new ArgumentException(
                "A causal market snapshot must contain at least one event.",
                nameof(marketSnapshot));
        }

        foreach (CausalMarketEvent candidate in marketSnapshot)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (candidate.Payload.InstrumentId != instrumentId)
            {
                throw new ArgumentException(
                    "Every market snapshot event must use the input instrument.",
                    nameof(marketSnapshot));
            }

            if (candidate.Payload.EventTime.Value > requestedAt.Value
                || candidate.AvailabilityTime.Value > requestedAt.Value)
            {
                throw new ArgumentException(
                    "A causal market snapshot cannot contain an event observed after the request time.",
                    nameof(marketSnapshot));
            }
        }
    }
}

/// <summary>One immutable fill observation produced for research only.</summary>
public sealed record ResearchFill
{
    /// <summary>Gets the non-empty research fill identifier.</summary>
    public string FillId { get; }

    /// <summary>Gets the positive quantity represented by this fill.</summary>
    public Quantity FilledQuantity { get; }

    /// <summary>Gets the positive simulated fill price.</summary>
    public Price FillPrice { get; }

    /// <summary>Gets the time assigned to this research observation.</summary>
    public UtcTimestamp FilledAt { get; }

    /// <summary>Gets the causal sequence associated with this observation.</summary>
    public long StableSequence { get; }

    /// <summary>Creates a research fill observation without execution metadata.</summary>
    [JsonConstructor]
    public ResearchFill(string fillId, Quantity filledQuantity, Price fillPrice,
        UtcTimestamp filledAt, long stableSequence)
    {
        ContractGuard.RequireText(fillId, nameof(fillId));
        ArgumentNullException.ThrowIfNull(filledQuantity);
        if (filledQuantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(filledQuantity),
                filledQuantity.Value, "A research fill quantity must be greater than zero.");
        }

        ArgumentNullException.ThrowIfNull(fillPrice);
        ArgumentNullException.ThrowIfNull(filledAt);
        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "A research fill sequence cannot be negative.");
        }

        FillId = fillId;
        FilledQuantity = filledQuantity;
        FillPrice = fillPrice;
        FilledAt = filledAt;
        StableSequence = stableSequence;
    }
}

/// <summary>
/// Carries only research fill observations and the complete immutable input snapshot.
/// It contains no decision, risk, order, or execution authorization state.
/// </summary>
public sealed record FillSimulationResult
{
    /// <summary>Gets the version of the simulator contract that produced this result.</summary>
    public FillSimulationVersion Version { get; }

    /// <summary>Gets the complete immutable input snapshot used for the result.</summary>
    public FillSimulationInput Input { get; }

    /// <summary>Gets the immutable research fill observations, which may be empty.</summary>
    public ImmutableValueList<ResearchFill> Fills { get; }

    /// <summary>Creates a result and defensively snapshots its research fills and input.</summary>
    public FillSimulationResult(FillSimulationVersion version, FillSimulationInput input,
        IEnumerable<ResearchFill> fills)
        : this(version, input, SnapshotFills(fills, nameof(fills)))
    {
    }

    /// <summary>Deserializes an already immutable fill collection.</summary>
    [JsonConstructor]
    public FillSimulationResult(FillSimulationVersion version, FillSimulationInput input,
        ImmutableValueList<ResearchFill> fills)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(fills);
        ValidateFills(input, fills);

        Version = version;
        Input = new FillSimulationInput(input.HypotheticalRequestId, input.InstrumentId,
            input.Direction, input.RequestedQuantity, input.RequestedAt, input.MarketSnapshot,
            input.Assumptions, input.ReferencePrice);
        Fills = new ImmutableValueList<ResearchFill>(fills);
    }

    private static ImmutableValueList<ResearchFill> SnapshotFills(
        IEnumerable<ResearchFill> fills, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(fills, parameterName);
        ImmutableValueList<ResearchFill> snapshot = new(fills);
        if (snapshot.Any(static item => item is null))
        {
            throw new ArgumentException("Research fills cannot contain null values.", parameterName);
        }

        return snapshot;
    }

    private static void ValidateFills(FillSimulationInput input,
        ImmutableValueList<ResearchFill> fills)
    {
        HashSet<string> fillIds = new(StringComparer.Ordinal);
        decimal filledQuantity = 0m;
        foreach (ResearchFill fill in fills)
        {
            ArgumentNullException.ThrowIfNull(fill);
            if (!fillIds.Add(fill.FillId))
            {
                throw new ArgumentException("Research fill identifiers must be unique.", nameof(fills));
            }

            if (fill.FilledQuantity.Value > input.RequestedQuantity.Value - filledQuantity)
            {
                throw new ArgumentException(
                    "Research fills cannot exceed the hypothetical requested quantity.",
                    nameof(fills));
            }

            filledQuantity += fill.FilledQuantity.Value;
        }
    }
}

/// <summary>
/// Defines a deterministic, side-effect-free research fill simulation boundary.
/// Implementations cannot authorize, submit, or execute an order through this contract.
/// </summary>
public interface IFillSimulator
{
    /// <summary>Gets the immutable version identifier for this simulator.</summary>
    FillSimulationVersion Version { get; }

    /// <summary>Simulates research fills for one hypothetical input snapshot.</summary>
    FillSimulationResult Simulate(FillSimulationInput input);
}
