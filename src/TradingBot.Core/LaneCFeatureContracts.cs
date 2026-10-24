using System.Text.Json.Serialization;

namespace TradingBot.Core;

public enum LaneCFeatureState
{
    Ready,
    WarmingUp,
    InsufficientData,
    Stale,
    SequenceGap,
    Conflict
}

public enum LaneCLevelSide
{
    Bid,
    Ask
}

/// <summary>Explicit time, size, and finite-input bounds for Lane C features.</summary>
public sealed record LaneCFeatureConfiguration
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string FormulaVersion { get; }

    public TimeSpan Window { get; }

    public TimeSpan MaximumBookAge { get; }

    public TimeSpan MaximumTradeAge { get; }

    public int TopN { get; }

    public int MaximumInputRecords { get; }

    public decimal? WallQuantityFloor { get; }

    public decimal? MinimumAbsorptionVolume { get; }

    public decimal? MaximumAbsorptionPriceMove { get; }

    [JsonConstructor]
    public LaneCFeatureConfiguration(int formatVersion, string formulaVersion,
        TimeSpan window, TimeSpan maximumBookAge, TimeSpan maximumTradeAge, int topN,
        int maximumInputRecords, decimal? wallQuantityFloor,
        decimal? minimumAbsorptionVolume, decimal? maximumAbsorptionPriceMove)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);

        ContractGuard.RequireText(formulaVersion, nameof(formulaVersion));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBookAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumTradeAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInputRecords);
        if (wallQuantityFloor is <= 0m || minimumAbsorptionVolume is < 0m
            || maximumAbsorptionPriceMove is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(wallQuantityFloor));
        }

        FormatVersion = formatVersion;
        FormulaVersion = formulaVersion;
        Window = window;
        MaximumBookAge = maximumBookAge;
        MaximumTradeAge = maximumTradeAge;
        TopN = topN;
        MaximumInputRecords = maximumInputRecords;
        WallQuantityFloor = wallQuantityFloor;
        MinimumAbsorptionVolume = minimumAbsorptionVolume;
        MaximumAbsorptionPriceMove = maximumAbsorptionPriceMove;
    }

    public LaneCFeatureConfiguration(string formulaVersion, TimeSpan window,
        TimeSpan maximumBookAge, TimeSpan maximumTradeAge, int topN,
        int maximumInputRecords, decimal? wallQuantityFloor = null,
        decimal? minimumAbsorptionVolume = null, decimal? maximumAbsorptionPriceMove = null)
        : this(CurrentFormatVersion, formulaVersion, window, maximumBookAge,
            maximumTradeAge, topN, maximumInputRecords, wallQuantityFloor,
            minimumAbsorptionVolume, maximumAbsorptionPriceMove)
    {
    }
}

/// <summary>Bounded capture history and causal cut used by the pure Lane C evaluator.</summary>
public sealed record LaneCFeatureInput
{
    public InstrumentId InstrumentId { get; }

    public ImmutableValueList<CaptureEnvelope> Records { get; }

    public CausalEventCursor Cursor { get; }

    public string Provenance { get; }

    [JsonConstructor]
    public LaneCFeatureInput(InstrumentId instrumentId,
        ImmutableValueList<CaptureEnvelope> records, CausalEventCursor cursor,
        string provenance)
        : this(instrumentId, (IEnumerable<CaptureEnvelope>)records, cursor, provenance)
    {
    }

    public LaneCFeatureInput(InstrumentId instrumentId,
        IEnumerable<CaptureEnvelope> records, CausalEventCursor cursor, string provenance)
    {
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireText(provenance, nameof(provenance));
        CaptureEnvelope[] snapshot = records.ToArray();
        if (snapshot.Any(record => record is null
            || record.Payload is not null
                && record.Payload.InstrumentId != instrumentId))
        {
            throw new ArgumentException(
                "Lane C capture rows must be non-null and match the input instrument.",
                nameof(records));
        }

        if (snapshot.Select(static record => record.StableSequence).Distinct().Count()
            != snapshot.Length)
        {
            throw new ArgumentException("Lane C stable sequences must be unique.",
                nameof(records));
        }

        InstrumentId = instrumentId;
        Records = new ImmutableValueList<CaptureEnvelope>(snapshot);
        Cursor = cursor;
        Provenance = provenance;
    }
}

/// <summary>One versioned per-price quantity change derived from displayed depth.</summary>
public sealed record LaneCLevelFeature
{
    public LaneCLevelSide Side { get; }

    public Price Price { get; }

    public decimal CurrentQuantity { get; }

    public decimal MaximumQuantity { get; }

    public int ObservedBookCount { get; }

    public decimal AddedQuantity { get; }

    public decimal RemovedQuantity { get; }

    public int ReplenishmentCandidateCount { get; }

    public int WithdrawalCandidateCount { get; }

    public bool? IsWallCandidate { get; }

    [JsonConstructor]
    public LaneCLevelFeature(LaneCLevelSide side, Price price, decimal currentQuantity,
        decimal maximumQuantity, int observedBookCount, decimal addedQuantity,
        decimal removedQuantity, int replenishmentCandidateCount,
        int withdrawalCandidateCount, bool? isWallCandidate)
    {
        ContractGuard.RequireDefined(side, nameof(side));
        ArgumentNullException.ThrowIfNull(price);
        if (currentQuantity < 0m || maximumQuantity < currentQuantity
            || addedQuantity < 0m || removedQuantity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(currentQuantity));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(observedBookCount);
        ArgumentOutOfRangeException.ThrowIfNegative(replenishmentCandidateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(withdrawalCandidateCount);
        Side = side;
        Price = price;
        CurrentQuantity = currentQuantity;
        MaximumQuantity = maximumQuantity;
        ObservedBookCount = observedBookCount;
        AddedQuantity = addedQuantity;
        RemovedQuantity = removedQuantity;
        ReplenishmentCandidateCount = replenishmentCandidateCount;
        WithdrawalCandidateCount = withdrawalCandidateCount;
        IsWallCandidate = isWallCandidate;
    }
}

/// <summary>Deterministic result for a causal Lane C feature cut.</summary>
public sealed record LaneCFeatureResult
{
    public const string CurrentAlgorithmVersion = "lane-c-features-v1";

    public string AlgorithmVersion { get; }

    public string InputIdentity { get; }

    public string ConfigurationVersion { get; }

    public LaneCFeatureState State { get; }

    public CausalEventCursor Cursor { get; }

    public decimal? BestBid { get; }

    public decimal? BestAsk { get; }

    public decimal? Spread { get; }

    public decimal? TopBidDepth { get; }

    public decimal? TopAskDepth { get; }

    public decimal? BookImbalance { get; }

    public decimal? AggressiveBuyVolume { get; }

    public decimal? AggressiveSellVolume { get; }

    public decimal? AggressorImbalance { get; }

    public decimal? AddedBidLiquidity { get; }

    public decimal? RemovedBidLiquidity { get; }

    public decimal? AddedAskLiquidity { get; }

    public decimal? RemovedAskLiquidity { get; }

    public decimal? MidPriceMove { get; }

    public decimal? PriceResponsePerAggressorVolume { get; }

    public bool? AbsorptionCandidate { get; }

    public int WallMigrationCandidateCount { get; }

    public ImmutableValueList<LaneCLevelFeature> Levels { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonConstructor]
    public LaneCFeatureResult(string algorithmVersion, string inputIdentity,
        string configurationVersion, LaneCFeatureState state, CausalEventCursor cursor,
        decimal? bestBid, decimal? bestAsk, decimal? spread, decimal? topBidDepth,
        decimal? topAskDepth, decimal? bookImbalance, decimal? aggressiveBuyVolume,
        decimal? aggressiveSellVolume, decimal? aggressorImbalance,
        decimal? addedBidLiquidity, decimal? removedBidLiquidity,
        decimal? addedAskLiquidity, decimal? removedAskLiquidity, decimal? midPriceMove,
        decimal? priceResponsePerAggressorVolume, bool? absorptionCandidate,
        int wallMigrationCandidateCount, ImmutableValueList<LaneCLevelFeature> levels,
        ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireText(algorithmVersion, nameof(algorithmVersion));
        ContractGuard.RequireText(inputIdentity, nameof(inputIdentity));
        ContractGuard.RequireText(configurationVersion, nameof(configurationVersion));
        ContractGuard.RequireDefined(state, nameof(state));
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentOutOfRangeException.ThrowIfNegative(wallMigrationCandidateCount);
        AlgorithmVersion = algorithmVersion;
        InputIdentity = inputIdentity;
        ConfigurationVersion = configurationVersion;
        State = state;
        Cursor = cursor;
        BestBid = bestBid;
        BestAsk = bestAsk;
        Spread = spread;
        TopBidDepth = topBidDepth;
        TopAskDepth = topAskDepth;
        BookImbalance = bookImbalance;
        AggressiveBuyVolume = aggressiveBuyVolume;
        AggressiveSellVolume = aggressiveSellVolume;
        AggressorImbalance = aggressorImbalance;
        AddedBidLiquidity = addedBidLiquidity;
        RemovedBidLiquidity = removedBidLiquidity;
        AddedAskLiquidity = addedAskLiquidity;
        RemovedAskLiquidity = removedAskLiquidity;
        MidPriceMove = midPriceMove;
        PriceResponsePerAggressorVolume = priceResponsePerAggressorVolume;
        AbsorptionCandidate = absorptionCandidate;
        WallMigrationCandidateCount = wallMigrationCandidateCount;
        Levels = new ImmutableValueList<LaneCLevelFeature>(levels);
        Reasons = new ImmutableValueList<string>(reasons);
    }
}
