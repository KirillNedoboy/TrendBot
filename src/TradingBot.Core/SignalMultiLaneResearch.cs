using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Explicit operating bounds for one unvalidated public signal experiment.</summary>
public sealed record SignalMultiLanePolicy
{
    public const string CurrentAlgorithmVersion = "signal-multilane-research-v1";

    public string AlgorithmVersion { get; }
    public TimeSpan OpenInterestPollInterval { get; }
    public TimeSpan MaximumOpenInterestAge { get; }
    public TimeSpan MaximumMarkPriceAge { get; }
    public LaneCFeatureConfiguration Microstructure { get; }

    [JsonConstructor]
    public SignalMultiLanePolicy(string algorithmVersion, TimeSpan openInterestPollInterval,
        TimeSpan maximumOpenInterestAge, TimeSpan maximumMarkPriceAge,
        LaneCFeatureConfiguration microstructure)
    {
        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentException("Unsupported signal-only multi-lane algorithm.",
                nameof(algorithmVersion));
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(openInterestPollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumOpenInterestAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumMarkPriceAge, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(microstructure);
        AlgorithmVersion = algorithmVersion;
        OpenInterestPollInterval = openInterestPollInterval;
        MaximumOpenInterestAge = maximumOpenInterestAge;
        MaximumMarkPriceAge = maximumMarkPriceAge;
        Microstructure = microstructure;
    }
}

/// <summary>One public mark-price update and its funding forecast, not a funding payment.</summary>
public sealed record SignalMarkPriceObservation
{
    public string SourceId { get; }
    public string SessionId { get; }
    public string EventId { get; }
    public InstrumentId InstrumentId { get; }
    public Price MarkPrice { get; }
    public decimal FundingForecast { get; }
    public UtcTimestamp EventTime { get; }
    public UtcTimestamp ReceiveTime { get; }
    public UtcTimestamp AvailabilityTime { get; }
    public long StableSequence { get; }

    [JsonConstructor]
    public SignalMarkPriceObservation(string sourceId, string sessionId, string eventId,
        InstrumentId instrumentId, Price markPrice, decimal fundingForecast,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(sessionId, nameof(sessionId));
        ContractGuard.RequireText(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(markPrice);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        ArgumentOutOfRangeException.ThrowIfNegative(stableSequence);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException("Availability cannot precede receipt.",
                nameof(availabilityTime));
        }
        SourceId = sourceId;
        SessionId = sessionId;
        EventId = eventId;
        InstrumentId = instrumentId;
        MarkPrice = markPrice;
        FundingForecast = fundingForecast;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
    }
}

/// <summary>Two distinct public observations paired without looking past the OI receipt.</summary>
public sealed record SignalOpenInterestMarkSample
{
    public LaneBOpenInterestObservation OpenInterest { get; }
    public SignalMarkPriceObservation MarkPrice { get; }

    [JsonConstructor]
    public SignalOpenInterestMarkSample(LaneBOpenInterestObservation openInterest,
        SignalMarkPriceObservation markPrice)
    {
        ArgumentNullException.ThrowIfNull(openInterest);
        ArgumentNullException.ThrowIfNull(markPrice);
        if (openInterest.InstrumentId != markPrice.InstrumentId
            || markPrice.EventTime.Value > openInterest.AvailabilityTime.Value
            || markPrice.AvailabilityTime.Value > openInterest.AvailabilityTime.Value)
        {
            throw new ArgumentException("The mark observation must be visible at the OI receipt.",
                nameof(markPrice));
        }
        OpenInterest = openInterest;
        MarkPrice = markPrice;
    }
}

/// <summary>Exact public B/C inputs retained for an individual signal decision.</summary>
public sealed record SignalMultiLaneInput
{
    public InstrumentId InstrumentId { get; }
    public Direction Direction { get; }
    public UtcTimestamp DecisionAt { get; }
    public SignalMultiLanePolicy Policy { get; }
    public SignalOpenInterestMarkSample? PreviousOpenInterest { get; }
    public SignalOpenInterestMarkSample? CurrentOpenInterest { get; }
    public SignalMarkPriceObservation? CurrentMarkPrice { get; }
    public UtcTimestamp? TradeCoverageStart { get; }
    public LaneCFeatureInput? MicrostructureInput { get; }

    [JsonConstructor]
    public SignalMultiLaneInput(InstrumentId instrumentId, Direction direction,
        UtcTimestamp decisionAt, SignalMultiLanePolicy policy,
        SignalOpenInterestMarkSample? previousOpenInterest,
        SignalOpenInterestMarkSample? currentOpenInterest,
        SignalMarkPriceObservation? currentMarkPrice, UtcTimestamp? tradeCoverageStart,
        LaneCFeatureInput? microstructureInput)
    {
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireDefined(direction, nameof(direction));
        ArgumentNullException.ThrowIfNull(decisionAt);
        ArgumentNullException.ThrowIfNull(policy);
        if (previousOpenInterest is not null
                && previousOpenInterest.OpenInterest.InstrumentId != instrumentId
            || currentOpenInterest is not null
                && currentOpenInterest.OpenInterest.InstrumentId != instrumentId
            || currentMarkPrice is not null && currentMarkPrice.InstrumentId != instrumentId
            || microstructureInput is not null && microstructureInput.InstrumentId != instrumentId)
        {
            throw new ArgumentException("Every lane input must match the signal instrument.");
        }
        InstrumentId = instrumentId;
        Direction = direction;
        DecisionAt = decisionAt;
        Policy = policy;
        PreviousOpenInterest = previousOpenInterest;
        CurrentOpenInterest = currentOpenInterest;
        CurrentMarkPrice = currentMarkPrice;
        TradeCoverageStart = tradeCoverageStart;
        MicrostructureInput = microstructureInput;
    }
}

/// <summary>Pure signal-only research assessment; it never authorizes an order.</summary>
public sealed record SignalMultiLaneAssessment(DecisionEvidenceStatus LaneB,
    DecisionEvidenceStatus LaneC, ImmutableValueList<string> Reasons)
{
    [JsonIgnore]
    public bool IsAllowed => LaneB is DecisionEvidenceStatus.Confirm or DecisionEvidenceStatus.Neutral
        && LaneC is DecisionEvidenceStatus.Confirm or DecisionEvidenceStatus.Neutral;
}

public static class SignalMultiLaneResearchEvaluator
{
    public static SignalMultiLaneAssessment Evaluate(SignalMultiLaneInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        List<string> reasons = [];
        DecisionEvidenceStatus laneB = EvaluateLaneB(input, reasons);
        DecisionEvidenceStatus laneC = EvaluateLaneC(input, reasons);
        return new SignalMultiLaneAssessment(laneB, laneC,
            new ImmutableValueList<string>(reasons));
    }

    private static DecisionEvidenceStatus EvaluateLaneB(SignalMultiLaneInput input,
        List<string> reasons)
    {
        SignalOpenInterestMarkSample? previous = input.PreviousOpenInterest;
        SignalOpenInterestMarkSample? current = input.CurrentOpenInterest;
        SignalMarkPriceObservation? latestMark = input.CurrentMarkPrice;
        DateTimeOffset cut = input.DecisionAt.Value;
        if (previous is null || current is null || latestMark is null)
        {
            reasons.Add("lane-b-input-missing");
            return DecisionEvidenceStatus.Unknown;
        }
        if (!StringComparer.Ordinal.Equals(previous.OpenInterest.SourceId,
                current.OpenInterest.SourceId)
            || !StringComparer.Ordinal.Equals(previous.OpenInterest.Unit, current.OpenInterest.Unit)
            || StringComparer.Ordinal.Equals(previous.OpenInterest.EventId,
                current.OpenInterest.EventId)
            || previous.OpenInterest.EventTime.Value >= current.OpenInterest.EventTime.Value
            || previous.OpenInterest.AvailabilityTime.Value > current.OpenInterest.AvailabilityTime.Value)
        {
            reasons.Add("lane-b-oi-sequence-incompatible");
            return DecisionEvidenceStatus.Unknown;
        }
        if (!Fresh(previous.OpenInterest.EventTime, previous.OpenInterest.AvailabilityTime,
                cut, input.Policy.MaximumOpenInterestAge)
            || !Fresh(current.OpenInterest.EventTime, current.OpenInterest.AvailabilityTime,
                cut, input.Policy.MaximumOpenInterestAge)
            || !Fresh(latestMark.EventTime, latestMark.AvailabilityTime,
                cut, input.Policy.MaximumMarkPriceAge)
            || !Fresh(previous.MarkPrice.EventTime, previous.MarkPrice.AvailabilityTime,
                previous.OpenInterest.AvailabilityTime.Value, input.Policy.MaximumMarkPriceAge)
            || !Fresh(current.MarkPrice.EventTime, current.MarkPrice.AvailabilityTime,
                current.OpenInterest.AvailabilityTime.Value, input.Policy.MaximumMarkPriceAge))
        {
            reasons.Add("lane-b-input-stale-or-future");
            return DecisionEvidenceStatus.Unknown;
        }
        if (current.OpenInterest.Value <= previous.OpenInterest.Value)
        {
            reasons.Add("lane-b-oi-not-rising");
            return DecisionEvidenceStatus.Neutral;
        }
        int priceDirection = current.MarkPrice.MarkPrice.Value.CompareTo(
            previous.MarkPrice.MarkPrice.Value);
        int wanted = input.Direction == Direction.Long ? 1 : -1;
        if (priceDirection == 0)
        {
            reasons.Add("lane-b-mark-price-unchanged");
            return DecisionEvidenceStatus.Neutral;
        }
        bool aligned = priceDirection == wanted;
        reasons.Add(aligned ? "lane-b-oi-price-aligned" : "lane-b-oi-price-opposed");
        return aligned ? DecisionEvidenceStatus.Confirm : DecisionEvidenceStatus.Veto;
    }

    private static DecisionEvidenceStatus EvaluateLaneC(SignalMultiLaneInput input,
        List<string> reasons)
    {
        LaneCFeatureInput? history = input.MicrostructureInput;
        if (history is null || input.TradeCoverageStart is null
            || history.Cursor.AsOf != input.DecisionAt
            || input.TradeCoverageStart.Value > input.DecisionAt.Value.Subtract(
                input.Policy.Microstructure.Window))
        {
            reasons.Add("lane-c-coverage-incomplete");
            return DecisionEvidenceStatus.Unknown;
        }
        if (!MicrostructureFresh(history, input.Policy.Microstructure))
        {
            reasons.Add("lane-c-input-invalid-or-stale");
            return DecisionEvidenceStatus.Unknown;
        }
        LaneCFeatureResult features;
        try
        {
            features = LaneCFeatureEvaluator.Evaluate(history, input.Policy.Microstructure);
        }
        catch (OverflowException)
        {
            reasons.Add("lane-c-arithmetic-overflow");
            return DecisionEvidenceStatus.Unknown;
        }
        if (features.State != LaneCFeatureState.Ready
            || features.BookImbalance is null || features.AggressorImbalance is null)
        {
            reasons.Add($"lane-c-feature-{features.State.ToString().ToLowerInvariant()}");
            return DecisionEvidenceStatus.Unknown;
        }
        int wanted = input.Direction == Direction.Long ? 1 : -1;
        int book = Math.Sign(features.BookImbalance.Value);
        int flow = Math.Sign(features.AggressorImbalance.Value);
        if (book == wanted && flow == wanted)
        {
            reasons.Add("lane-c-book-flow-aligned");
            return DecisionEvidenceStatus.Confirm;
        }
        if (book == -wanted && flow == -wanted)
        {
            reasons.Add("lane-c-book-flow-opposed");
            return DecisionEvidenceStatus.Veto;
        }
        reasons.Add("lane-c-book-flow-mixed");
        return DecisionEvidenceStatus.Neutral;
    }

    private static bool MicrostructureFresh(LaneCFeatureInput input,
        LaneCFeatureConfiguration configuration)
    {
        CaptureEnvelope? lastBook = null;
        CaptureEnvelope? lastTrade = null;
        DateTimeOffset cut = input.Cursor.AsOf.Value;
        foreach (CaptureEnvelope record in input.Records)
        {
            if (record.StableSequence > input.Cursor.MaxStableSequence
                || record.EventTime.Value > cut || record.AvailabilityTime.Value > cut)
            {
                continue;
            }
            if (record.Payload is BookDelta
                && (lastBook is null || record.StableSequence > lastBook.StableSequence))
            {
                lastBook = record;
            }
            else if (record.Payload is Trade
                && (lastTrade is null || record.EventTime.Value > lastTrade.EventTime.Value
                    || record.EventTime.Value == lastTrade.EventTime.Value
                        && record.StableSequence > lastTrade.StableSequence))
            {
                lastTrade = record;
            }
        }
        return lastBook is not null && lastTrade is not null
            && lastBook.Provenance.DepthMetadata?.IsSnapshot == true
            && lastBook.Payload is BookDelta book && HasUsableDepth(book, configuration.TopN)
            && Fresh(lastBook.EventTime, lastBook.AvailabilityTime, cut,
                configuration.MaximumBookAge)
            && Fresh(lastTrade.EventTime, lastTrade.AvailabilityTime, cut,
                configuration.MaximumTradeAge);
    }

    private static bool HasUsableDepth(BookDelta book, int topN)
    {
        int bids = 0;
        int asks = 0;
        decimal bestBid = 0m;
        decimal bestAsk = decimal.MaxValue;
        foreach (BookLevel level in book.Bids)
        {
            if (level.Quantity.Value > 0m)
            {
                bids++;
                bestBid = Math.Max(bestBid, level.Price.Value);
            }
        }
        foreach (BookLevel level in book.Asks)
        {
            if (level.Quantity.Value > 0m)
            {
                asks++;
                bestAsk = Math.Min(bestAsk, level.Price.Value);
            }
        }
        return bids >= topN && asks >= topN && bestBid < bestAsk;
    }

    private static bool Fresh(UtcTimestamp eventTime, UtcTimestamp availabilityTime,
        DateTimeOffset cut, TimeSpan maximumAge) =>
        eventTime.Value <= cut && availabilityTime.Value <= cut
        && cut - eventTime.Value <= maximumAge && cut - availabilityTime.Value <= maximumAge;
}
