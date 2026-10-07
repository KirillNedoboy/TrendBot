using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Core;

namespace TradingBot.TestFixtures;

/// <summary>One executable negative contract case with an explicit exception and parameter.</summary>
public sealed record CoreExceptionVector(
    string Name,
    Type ExceptionType,
    string ParameterName,
    Action Invoke);

/// <summary>One strict JSON round-trip case with a fresh factory and typed restore delegate.</summary>
public sealed record CoreJsonRoundTripVector(
    string Name,
    Func<object> Create,
    Func<object, string> Serialize,
    Func<string, object> Deserialize);

/// <summary>One malformed or incomplete JSON case expected to fail during Core deserialization.</summary>
public sealed record CoreJsonInvalidVector(
    string Name,
    Type ContractType,
    string FieldName,
    Type ExceptionType,
    string Json,
    Func<string, object> Deserialize);

/// <summary>One deterministic event comparison with a declared relation expectation.</summary>
public sealed record CoreEventComparisonVector(
    string Name,
    MarketEvent Previous,
    MarketEvent Candidate,
    MarketEventRelation ExpectedRelation);

/// <summary>A causal event set and its expected visible processing order.</summary>
public sealed record CausalOrderingVector(
    IReadOnlyList<CausalMarketEvent> Events,
    IReadOnlyList<string> ExpectedEventIds,
    CausalEventCursor EventCursor);

/// <summary>A risk input/result pair with explicit deterministic output examples.</summary>
public sealed record RiskMathVector(
    RiskMathInput Input,
    RiskMathResult Result,
    decimal ExpectedRiskBudget,
    decimal ExpectedLossPerUnit,
    decimal ExpectedRawQuantity,
    decimal ExpectedNotionalExposure);

/// <summary>Caller-owned level lists and the immutable snapshot created from them.</summary>
public sealed record ImmutableCollectionVector(
    BookDelta Value,
    List<BookLevel> SourceBids,
    List<BookLevel> SourceAsks);

/// <summary>
/// Reusable deterministic values and executable vectors for the passive Core contracts.
/// This file is linked only into test projects and has no runtime dependency.
/// </summary>
public static class CoreFixtureVectors
{
    public const string InstrumentValue = "BTCUSDT";
    public const string OtherInstrumentValue = "ETHUSDT";
    public const string Currency = "USDT";
    public const string RiskVersionValue = "risk-v1";
    public const string FillVersionValue = "fill-v1";

    public static DateTimeOffset BaseDateTime =>
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static InstrumentId NewInstrument(string? value = null) =>
        new(value ?? InstrumentValue);

    public static InstrumentId NewOtherInstrument() => new(OtherInstrumentValue);

    public static UtcTimestamp NewEventTime() => new(BaseDateTime);

    public static UtcTimestamp NewSnapshotTime() => new(BaseDateTime);

    public static UtcTimestamp NewEquivalentOffsetTime() =>
        new(new DateTimeOffset(2026, 1, 2, 6, 4, 5, TimeSpan.FromHours(3)));

    public static UtcTimestamp NewTimeBeforeSnapshot() =>
        new(BaseDateTime.AddMinutes(-1));

    public static UtcTimestamp NewTimeAfterSnapshot() =>
        new(BaseDateTime.AddMinutes(1));

    public static Trade NewTrade(
        string eventId = "trade-1",
        InstrumentId? instrumentId = null,
        UtcTimestamp? eventTime = null,
        decimal price = 100m,
        decimal quantity = 1m,
        TradeSide side = TradeSide.Buy) =>
        new(eventId, instrumentId ?? NewInstrument(), eventTime ?? NewEventTime(),
            new Price(price), new Quantity(quantity), side);

    public static Bar NewBar(
        string eventId = "bar-1",
        decimal open = 100m,
        decimal high = 105m,
        decimal low = 95m,
        decimal close = 101m,
        decimal volume = 12.3456789m,
        InstrumentId? instrumentId = null,
        UtcTimestamp? eventTime = null)
    {
        UtcTimestamp closeTime = eventTime ?? NewEventTime();
        return new Bar(eventId, instrumentId ?? NewInstrument(), closeTime,
            new UtcTimestamp(closeTime.Value.AddMinutes(-1)), closeTime,
            new Price(open), new Price(high), new Price(low), new Price(close),
            new Quantity(volume));
    }

    public static BookDelta NewBookDelta(
        IEnumerable<BookLevel>? bids = null,
        IEnumerable<BookLevel>? asks = null,
        InstrumentId? instrumentId = null,
        UtcTimestamp? eventTime = null)
    {
        IEnumerable<BookLevel> bidLevels = bids ??
        [
            new BookLevel(new Price(100m), new Quantity(2m)),
            new BookLevel(new Price(99m), new Quantity(3m))
        ];
        IEnumerable<BookLevel> askLevels = asks ??
        [new BookLevel(new Price(101m), new Quantity(4m))];
        return new BookDelta("book-1", instrumentId ?? NewInstrument(),
            eventTime ?? NewEventTime(), bidLevels, askLevels);
    }

    public static ImmutableCollectionVector NewImmutableCollectionVector()
    {
        List<BookLevel> bids =
        [new BookLevel(new Price(100m), new Quantity(2m))];
        List<BookLevel> asks =
        [new BookLevel(new Price(101m), new Quantity(3m))];
        return new ImmutableCollectionVector(NewBookDelta(bids, asks), bids, asks);
    }

    public static Decision NewDecision() =>
        new("decision-1", "setup-1", DecisionOutcome.Allow,
            ["price-confirmed", "volume-confirmed"]);

    public static RiskDecision NewRiskDecision() =>
        new("risk-1", "decision-1", RiskDecisionOutcome.Blocked, ["kill-switch"]);

    public static Setup NewSetup(
        decimal anchor = 100m,
        decimal high = 110m,
        decimal low = 90m,
        InstrumentId? instrumentId = null,
        UtcTimestamp? anchorTime = null) =>
        new("setup-1", instrumentId ?? NewInstrument(), SetupType.BoundaryRejection,
            Direction.Long, SetupState.Observing, "anchor-1", anchorTime ?? NewEventTime(),
            new Price(anchor), new Price(101m), new Price(high), new Price(low),
            "context-v1");

    public static Position NewPosition(
        InstrumentId? instrumentId = null,
        UtcTimestamp? openedAt = null,
        UtcTimestamp? updatedAt = null)
    {
        UtcTimestamp opened = openedAt ?? NewEventTime();
        return new Position("position-1", instrumentId ?? NewInstrument(), Direction.Long,
            new Quantity(1.25m), opened,
            updatedAt ?? new UtcTimestamp(opened.Value.AddMinutes(2)),
            new Price(100m), new Money(1.5m, Currency));
    }

    public static CausalMarketEvent NewCausalEvent(
        string eventId = "event-1",
        long stableSequence = 1,
        DateTimeOffset? eventTime = null,
        DateTimeOffset? receiveTime = null,
        DateTimeOffset? availabilityTime = null,
        InstrumentId? instrumentId = null,
        UtcTimestamp? exchangeTime = null)
    {
        DateTimeOffset eventAt = eventTime ?? BaseDateTime.AddMinutes(-1);
        DateTimeOffset receiveAt = receiveTime ?? eventAt;
        DateTimeOffset availableAt = availabilityTime ?? receiveAt;
        return new CausalMarketEvent(
            NewTrade(eventId, instrumentId, new UtcTimestamp(eventAt)),
            new UtcTimestamp(receiveAt), new UtcTimestamp(availableAt), stableSequence,
            exchangeTime);
    }

    public static IReadOnlyList<CausalMarketEvent> NewCausalSnapshot() =>
    [NewCausalEvent("trade-1", stableSequence: 1)];

    public static RiskMathInput NewRiskInput(
        Direction direction = Direction.Long,
        decimal? protectiveStopPrice = null,
        string currency = Currency,
        decimal equityAmount = 1000.0000000000000000000000001m,
        decimal riskFraction = 0.01234567890123456789012345678m,
        decimal feePerUnit = 1.230000000000000000000000001m,
        decimal slippagePerUnit = 0.45000000000000000000000000001m)
    {
        decimal stop = protectiveStopPrice ??
            (direction == Direction.Long ? 90m : 110m);
        return new RiskMathInput(NewInstrument(), direction,
            new Money(equityAmount, currency), riskFraction, new Price(100m),
            new Price(stop), new MoneyPerQuantityUnit(feePerUnit, currency),
            new MoneyPerQuantityUnit(slippagePerUnit, currency));
    }

    public static RiskMathVector NewRiskMathVector(Direction direction = Direction.Long)
    {
        RiskMathInput input = NewRiskInput(direction);
        RiskMathResult result = new(new RiskMathVersion(RiskVersionValue), input,
            new Money(17.000000000000000000000000001m, Currency),
            new MoneyPerQuantityUnit(12.000000000000000000000000001m, Currency),
            new Quantity(0.50000000000000000000000000001m),
            new Money(321.0000000000000000000000001m, Currency));
        return new RiskMathVector(input, result, result.RiskBudget.Amount,
            result.LossPerUnit.Amount, result.RawQuantity.Value,
            result.NotionalExposure.Amount);
    }

    public static FillSimulationAssumptions NewFillAssumptions() =>
        new(
            "unknown: fee schedule is unavailable",
            "unknown: venue slippage is unavailable",
            "unknown: request latency is unavailable",
            "unknown: queue position is unavailable",
            "unknown: partial fill behavior is unavailable",
            "unknown: intrabar order is unavailable",
            "OHLCV input cannot reconstruct spread or queue state");

    public static FillSimulationInput NewFillInput(
        Direction direction = Direction.Long,
        decimal requestedQuantity = 0.25m,
        IEnumerable<CausalMarketEvent>? marketSnapshot = null,
        FillSimulationAssumptions? assumptions = null,
        UtcTimestamp? requestedAt = null,
        Price? referencePrice = null,
        InstrumentId? instrumentId = null)
    {
        InstrumentId instrument = instrumentId ?? NewInstrument();
        IEnumerable<CausalMarketEvent> snapshot = marketSnapshot ??
            NewCausalSnapshot();
        return new FillSimulationInput("research-request-1", instrument, direction,
            new Quantity(requestedQuantity), requestedAt ?? NewSnapshotTime(), snapshot,
            assumptions ?? NewFillAssumptions(), referencePrice ?? new Price(100m));
    }

    public static ResearchFill NewResearchFill(
        string fillId = "research-fill-1",
        decimal filledQuantity = 0.1m,
        decimal fillPrice = 100.5m,
        long stableSequence = 1,
        UtcTimestamp? filledAt = null) =>
        new(fillId, new Quantity(filledQuantity), new Price(fillPrice),
            filledAt ?? NewSnapshotTime(), stableSequence);

    public static FillSimulationResult NewFillResult()
    {
        FillSimulationInput input = NewFillInput();
        return new FillSimulationResult(new FillSimulationVersion(FillVersionValue), input,
        [
            NewResearchFill("research-fill-1", 0.1m, 100.5m, 1),
            NewResearchFill("research-fill-2", 0.15m, 100.75m, 2)
        ]);
    }

    public static IReadOnlyList<CoreEventComparisonVector> EventComparisonVectors =>
    [
        new("duplicate", NewTrade("event-1"), NewTrade("event-1"),
            MarketEventRelation.Duplicate),
        new("identity-conflict", NewTrade("event-1"), NewTrade("event-1", price: 101m),
            MarketEventRelation.IdentityConflict),
        new("earlier-event-time", NewTrade("event-2"),
            NewTrade("event-3", eventTime: new UtcTimestamp(BaseDateTime.AddMinutes(-1))),
            MarketEventRelation.EarlierEventTime),
        new("later-event-time", NewTrade("event-4"),
            NewTrade("event-5", eventTime: new UtcTimestamp(BaseDateTime.AddMinutes(1))),
            MarketEventRelation.LaterEventTime),
        new("same-event-time", NewTrade("event-6"), NewTrade("event-7"),
            MarketEventRelation.SameEventTime),
        new("different-instrument", NewTrade("event-8"),
            NewTrade("event-8", instrumentId: NewOtherInstrument()),
            MarketEventRelation.DifferentInstrument)
    ];

    public static CausalOrderingVector CausalOrdering => new(
    [
        NewCausalEvent("future-event", stableSequence: 3,
            eventTime: BaseDateTime.AddMinutes(1),
            receiveTime: BaseDateTime.AddMinutes(1),
            availabilityTime: BaseDateTime.AddMinutes(1)),
        NewCausalEvent("event-second", stableSequence: 2),
        NewCausalEvent("event-first", stableSequence: 1)
    ], ["event-first", "event-second"], new CausalEventCursor(NewSnapshotTime(), 2));

    public static IReadOnlyList<CoreExceptionVector> ExceptionVectors =>
    [
        new("instrument-missing", typeof(ArgumentException), "value",
            Ignore(() => new InstrumentId(" "))),
        new("price-zero", typeof(ArgumentOutOfRangeException), "value",
            Ignore(() => new Price(0m))),
        new("quantity-negative", typeof(ArgumentOutOfRangeException), "value",
            Ignore(() => new Quantity(-0.01m))),
        new("money-currency-missing", typeof(ArgumentException), "currency",
            Ignore(() => new Money(1m, " "))),
        new("risk-version-missing", typeof(ArgumentException), "value",
            Ignore(() => new RiskMathVersion(" "))),
        new("cost-negative", typeof(ArgumentOutOfRangeException), "amount",
            Ignore(() => new MoneyPerQuantityUnit(-0.01m, Currency))),
        new("cost-currency-missing", typeof(ArgumentException), "currency",
            Ignore(() => new MoneyPerQuantityUnit(1m, " "))),
        new("bar-invalid-duration", typeof(ArgumentException), "closeTime",
            Ignore(() => NewBar(eventTime: new UtcTimestamp(BaseDateTime),
                open: 100m, high: 105m, low: 95m, close: 101m)
                .WithInvalidDuration())),
        new("bar-high-excludes-open", typeof(ArgumentOutOfRangeException), "high",
            Ignore(() => NewBar(high: 99m, close: 98m))),
        new("bar-low-excludes-close", typeof(ArgumentOutOfRangeException), "low",
            Ignore(() => NewBar(low: 102m))),
        new("setup-high-excludes-anchor", typeof(ArgumentOutOfRangeException),
            "highSinceAnchor", Ignore(() => NewSetup(high: 99m))),
        new("setup-low-excludes-anchor", typeof(ArgumentOutOfRangeException),
            "lowSinceAnchor", Ignore(() => NewSetup(low: 101m))),
        new("position-update-before-open", typeof(ArgumentException), "updatedAt",
            Ignore(() => NewPosition(updatedAt: new UtcTimestamp(BaseDateTime.AddMinutes(-1))))),
        new("book-null-bids", typeof(ArgumentNullException), "bids",
            Ignore(() => new BookDelta("book-invalid", NewInstrument(), NewEventTime(),
                (IEnumerable<BookLevel>)null!, [new BookLevel(new Price(101m), new Quantity(1m))]))),
        new("book-null-level", typeof(ArgumentException), "bids",
            Ignore(() => NewBookDelta([null!], [new BookLevel(new Price(101m), new Quantity(1m))]))),
        new("decision-empty-reasons", typeof(ArgumentException), "reasons",
            Ignore(() => new Decision("decision-invalid", "setup-1", DecisionOutcome.Block, []))),
        new("risk-decision-empty-reasons", typeof(ArgumentException), "reasons",
            Ignore(() => new RiskDecision("risk-invalid", "decision-1", RiskDecisionOutcome.Blocked, []))),
        new("causal-availability-before-receive", typeof(ArgumentException), "availabilityTime",
            Ignore(() => new CausalMarketEvent(NewTrade(),
                new UtcTimestamp(BaseDateTime.AddSeconds(2)),
                new UtcTimestamp(BaseDateTime.AddSeconds(1)), 1))),
        new("causal-negative-sequence", typeof(ArgumentOutOfRangeException), "stableSequence",
            Ignore(() => new CausalMarketEvent(NewTrade(), NewEventTime(), NewEventTime(), -1))),
        new("cursor-negative-sequence", typeof(ArgumentOutOfRangeException), "maxStableSequence",
            Ignore(() => new CausalEventCursor(NewEventTime(), -1))),
        new("risk-zero-equity", typeof(ArgumentOutOfRangeException), "equity",
            Ignore(() => NewRiskInput(equityAmount: 0m))),
        new("risk-zero-fraction", typeof(ArgumentOutOfRangeException), "riskFraction",
            Ignore(() => NewRiskInput(riskFraction: 0m))),
        new("risk-fraction-above-one", typeof(ArgumentOutOfRangeException), "riskFraction",
            Ignore(() => NewRiskInput(riskFraction: 1.0000000000000000000000000001m))),
        new("risk-fee-currency-conflict", typeof(ArgumentException), "feePerUnit",
            Ignore(() => NewRiskInput(currency: Currency, feePerUnit: 1m)
                .WithFeeCurrency("USD"))),
        new("risk-slippage-currency-conflict", typeof(ArgumentException),
            "estimatedSlippagePerUnit", Ignore(() => NewRiskInput()
                .WithSlippageCurrency("USD"))),
        new("risk-long-stop-wrong-side", typeof(ArgumentException), "protectiveStopPrice",
            Ignore(() => NewRiskInput(Direction.Long, protectiveStopPrice: 110m))),
        new("risk-short-stop-wrong-side", typeof(ArgumentException), "protectiveStopPrice",
            Ignore(() => NewRiskInput(Direction.Short, protectiveStopPrice: 90m))),
        new("risk-result-zero-budget", typeof(ArgumentOutOfRangeException), "riskBudget",
            Ignore(() => NewRiskResultWith(riskBudget: new Money(0m, Currency)))),
        new("risk-result-zero-loss", typeof(ArgumentOutOfRangeException), "lossPerUnit",
            Ignore(() => NewRiskResultWith(lossPerUnit: new MoneyPerQuantityUnit(0m, Currency)))),
        new("risk-result-zero-quantity", typeof(ArgumentOutOfRangeException), "rawQuantity",
            Ignore(() => NewRiskResultWith(rawQuantity: new Quantity(0m)))),
        new("risk-result-zero-notional", typeof(ArgumentOutOfRangeException), "notionalExposure",
            Ignore(() => NewRiskResultWith(notionalExposure: new Money(0m, Currency)))),
        new("risk-result-currency-conflict", typeof(ArgumentException), "riskBudget",
            Ignore(() => NewRiskResultWith(riskBudget: new Money(17m, "USD")))),
        new("fill-assumption-missing-fees", typeof(ArgumentException), "fees",
            Ignore(() => new FillSimulationAssumptions(" ", "slippage", "latency", "queue",
                "partial", "intrabar", "data"))),
        new("fill-zero-quantity", typeof(ArgumentOutOfRangeException), "requestedQuantity",
            Ignore(() => NewFillInput(requestedQuantity: 0m))),
        new("fill-empty-snapshot", typeof(ArgumentException), "marketSnapshot",
            Ignore(() => NewFillInput(marketSnapshot: []))),
        new("fill-snapshot-wrong-instrument", typeof(ArgumentException), "marketSnapshot",
            Ignore(() => NewFillInput(marketSnapshot:
                [NewCausalEvent(instrumentId: NewOtherInstrument())]))),
        new("fill-snapshot-event-after-request", typeof(ArgumentException), "marketSnapshot",
            Ignore(() => NewFillInput(marketSnapshot:
                [NewCausalEvent(eventTime: BaseDateTime.AddMinutes(1))]))),
        new("fill-snapshot-availability-after-request", typeof(ArgumentException), "marketSnapshot",
            Ignore(() => NewFillInput(marketSnapshot:
                [NewCausalEvent(eventTime: BaseDateTime.AddMinutes(-1),
                    receiveTime: BaseDateTime.AddMinutes(-1),
                    availabilityTime: BaseDateTime.AddMinutes(1))]))),
        new("research-fill-zero-quantity", typeof(ArgumentOutOfRangeException), "filledQuantity",
            Ignore(() => NewResearchFill(filledQuantity: 0m))),
        new("research-fill-negative-sequence", typeof(ArgumentOutOfRangeException), "stableSequence",
            Ignore(() => NewResearchFill(stableSequence: -1))),
        new("fill-duplicate-id", typeof(ArgumentException), "fills",
            Ignore(() => NewFillResultWith(NewResearchFill("research-fill-1", 0.1m),
                NewResearchFill("research-fill-1", 0.1m)))),
        new("fill-over-requested-quantity", typeof(ArgumentException), "fills",
            Ignore(() => NewFillResultWith(NewResearchFill("research-fill-1", 0.2m),
                NewResearchFill("research-fill-2", 0.1m))))
    ];

    public static IReadOnlyList<CoreJsonRoundTripVector> JsonRoundTripVectors =>
    [
        Json("instrument", static () => NewInstrument()),
        Json("price", static () => new Price(12.345678901234567890123456789m)),
        Json("quantity", static () => new Quantity(0.00123456789m)),
        Json("money", static () => new Money(-12.345678901234567890123456789m, Currency)),
        Json("utc-timestamp", NewEventTime),
        Json("risk-version", static () => new RiskMathVersion(RiskVersionValue)),
        Json("cost-per-unit", static () =>
            new MoneyPerQuantityUnit(1.230000000000000000000000001m, Currency)),
        Json("trade", static () => NewTrade()),
        Json("market-event", static () => (MarketEvent)NewTrade()),
        Json("bar", static () => NewBar()),
        Json("book-level", static () => new BookLevel(new Price(100m), new Quantity(2m))),
        Json("book-delta", static () => NewBookDelta()),
        Json("funding-update", static () =>
            new FundingUpdate("funding-1", NewInstrument(), NewEventTime(), 0.000123456789m,
                new UtcTimestamp(BaseDateTime.AddHours(8)))),
        Json("open-interest-update", static () =>
            new OIUpdate("oi-1", NewInstrument(), NewEventTime(), 123456.7890123456789m)),
        Json("setup", static () => NewSetup()),
        Json("decision", static () => NewDecision()),
        Json("risk-decision", static () => NewRiskDecision()),
        Json("order-intent", static () =>
            new OrderIntent("intent-1", "run-1", "setup-1", "client-1", NewInstrument(),
                Direction.Short, OrderRole.Entry, 1, new Quantity(0.01m), new Price(99.5m),
                NewEventTime(), true)),
        Json("position", static () => NewPosition()),
        Json("causal-market-event", static () => NewCausalEvent()),
        Json("causal-cursor", static () => new CausalEventCursor(NewSnapshotTime(), 2)),
        Json("risk-input", static () => NewRiskMathVector().Input),
        Json("risk-result", static () => NewRiskMathVector().Result),
        Json("fill-version", static () => new FillSimulationVersion(FillVersionValue)),
        Json("fill-assumptions", static () => NewFillAssumptions()),
        Json("fill-input", static () => NewFillInput()),
        Json("research-fill", static () => NewResearchFill()),
        Json("fill-result", static () => NewFillResult())
    ];

    public static IReadOnlyList<CoreJsonInvalidVector> JsonInvalidVectors
    {
        get
        {
            RiskMathResult riskResult = NewRiskMathVector().Result;
            FillSimulationAssumptions assumptions = NewFillAssumptions();
            Trade trade = NewTrade();
            FillSimulationInput fillInput = NewFillInput();

            return
            [
                Missing("missing-risk-budget", riskResult, "riskBudget",
                    static json => CoreJsonSerializer.Deserialize<RiskMathResult>(json)),
                Missing("missing-fill-data-limitations", assumptions, "dataLimitations",
                    static json => CoreJsonSerializer.Deserialize<FillSimulationAssumptions>(json)),
                Unknown("unknown-trade-field", trade,
                    static json => CoreJsonSerializer.Deserialize<Trade>(json)),
                Unknown("unknown-fill-input-field", fillInput,
                    static json => CoreJsonSerializer.Deserialize<FillSimulationInput>(json)),
                Invalid("invalid-trade-price", trade, "price",
                    static root => ((JsonObject)root["price"]!)["value"] = 0m,
                    static json => CoreJsonSerializer.Deserialize<Trade>(json)),
                Invalid("invalid-trade-side", trade, "side",
                    static root => root["side"] = "hold",
                    static json => CoreJsonSerializer.Deserialize<Trade>(json)),
                Invalid("invalid-risk-stop", NewRiskMathVector().Input, "protectiveStopPrice",
                    static root => ((JsonObject)root["protectiveStopPrice"]!)["value"] = 110m,
                    static json => CoreJsonSerializer.Deserialize<RiskMathInput>(json)),
                Invalid("invalid-fill-requested-quantity", fillInput, "requestedQuantity",
                    static root => ((JsonObject)root["requestedQuantity"]!)["value"] = 0m,
                    static json => CoreJsonSerializer.Deserialize<FillSimulationInput>(json))
            ];
        }
    }

    private static CoreJsonRoundTripVector Json<T>(string name, Func<T> factory)
        where T : notnull =>
        new(name,
            () => factory()!,
            value => CoreJsonSerializer.Serialize((T)value),
            json => CoreJsonSerializer.Deserialize<T>(json)!);

    private static Action Ignore<T>(Func<T> factory) => () => _ = factory();

    private static CoreJsonInvalidVector Missing<T>(
        string name,
        T value,
        string fieldName,
        Func<string, object> deserialize)
        where T : notnull =>
        new(name, typeof(T), fieldName, typeof(JsonException),
            WithoutField(CoreJsonSerializer.Serialize(value), fieldName), deserialize);

    private static CoreJsonInvalidVector Unknown<T>(
        string name,
        T value,
        Func<string, object> deserialize)
        where T : notnull =>
        new(name, typeof(T), "unexpected", typeof(JsonException),
            WithUnknownField(CoreJsonSerializer.Serialize(value)), deserialize);

    private static CoreJsonInvalidVector Invalid<T>(
        string name,
        T value,
        string fieldName,
        Action<JsonObject> mutate,
        Func<string, object> deserialize)
        where T : notnull
    {
        JsonObject root = JsonNode.Parse(CoreJsonSerializer.Serialize(value))!.AsObject();
        mutate(root);
        return new(name, typeof(T), fieldName, typeof(JsonException),
            root.ToJsonString(), deserialize);
    }

    private static string WithoutField(string json, string fieldName)
    {
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        root.Remove(fieldName);
        return root.ToJsonString();
    }

    private static string WithUnknownField(string json)
    {
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        root["unexpected"] = true;
        return root.ToJsonString();
    }

    private static FillSimulationResult NewFillResultWith(params ResearchFill[] fills) =>
        new(new FillSimulationVersion(FillVersionValue), NewFillInput(), fills);

    private static RiskMathResult NewRiskResultWith(
        Money? riskBudget = null,
        MoneyPerQuantityUnit? lossPerUnit = null,
        Quantity? rawQuantity = null,
        Money? notionalExposure = null)
    {
        RiskMathVector vector = NewRiskMathVector();
        return new RiskMathResult(new RiskMathVersion(RiskVersionValue), vector.Input,
            riskBudget ?? vector.Result.RiskBudget,
            lossPerUnit ?? vector.Result.LossPerUnit,
            rawQuantity ?? vector.Result.RawQuantity,
            notionalExposure ?? vector.Result.NotionalExposure);
    }
}

internal static class CoreFixtureInvalidConstruction
{
    public static Bar WithInvalidDuration(this Bar valid)
    {
        return new Bar(valid.EventId, valid.InstrumentId, valid.EventTime,
            valid.CloseTime, valid.CloseTime, valid.Open, valid.High, valid.Low, valid.Close,
            valid.Volume);
    }

    public static RiskMathInput WithFeeCurrency(this RiskMathInput input, string currency)
    {
        return new RiskMathInput(input.InstrumentId, input.Direction, input.Equity,
            input.RiskFraction, input.EntryPrice, input.ProtectiveStopPrice,
            new MoneyPerQuantityUnit(input.FeePerUnit.Amount, currency),
            input.EstimatedSlippagePerUnit);
    }

    public static RiskMathInput WithSlippageCurrency(this RiskMathInput input, string currency)
    {
        return new RiskMathInput(input.InstrumentId, input.Direction, input.Equity,
            input.RiskFraction, input.EntryPrice, input.ProtectiveStopPrice,
            input.FeePerUnit,
            new MoneyPerQuantityUnit(input.EstimatedSlippagePerUnit.Amount, currency));
    }
}
