using System.Globalization;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Identifies the supported rolling moving-average formulas.</summary>
public enum RollingMovingAverageKind
{
    Sma,
    Ema,
    Wma,
    Rma
}

/// <summary>Identifies the supported ATR smoothing formulas.</summary>
public enum RollingAtrMode
{
    Sma,
    Rma
}

/// <summary>Describes the state of one rolling indicator value.</summary>
public enum RollingIndicatorState
{
    Ready,
    UnknownWarmup,
    UnknownConfiguration,
    UnknownInput,
    UnknownZeroDenominator,
    UnknownOverflow,
    UnknownQuality
}

/// <summary>Describes the amount of input needed by one rolling indicator.</summary>
public enum RollingIndicatorCompleteness
{
    Complete,
    Warmup,
    Incomplete,
    Unknown
}

/// <summary>Identifies one output in a rolling-indicator result.</summary>
public enum RollingIndicatorKind
{
    Atr,
    MovingAverage,
    RelativeVolume
}

/// <summary>Identifies the explicit rolling calculation algorithm version.</summary>
public sealed record RollingIndicatorVersion
{
    public string Value { get; }

    [JsonConstructor]
    public RollingIndicatorVersion(string value)
    {
        ContractGuard.RequireText(value, nameof(value));
        Value = value;
    }
}

/// <summary>
/// Explicit rolling-indicator configuration. Nullable fields intentionally
/// represent omitted configuration; the calculator never supplies defaults.
/// </summary>
public sealed record RollingIndicatorConfiguration
{
    public string? Timeframe { get; }

    public RollingIndicatorVersion? AlgorithmVersion { get; }

    public int? AtrPeriod { get; }

    public RollingAtrMode? AtrMode { get; }

    public int? MovingAveragePeriod { get; }

    public RollingMovingAverageKind? MovingAverageKind { get; }

    public int? RelativeVolumePeriod { get; }

    [JsonIgnore]
    public RollingIndicatorVersion? Version => AlgorithmVersion;

    [JsonIgnore]
    public int? MaPeriod => MovingAveragePeriod;

    [JsonIgnore]
    public RollingMovingAverageKind? MaKind => MovingAverageKind;

    [JsonIgnore]
    public int? RvolPeriod => RelativeVolumePeriod;

    /// <summary>Creates an explicitly incomplete configuration.</summary>
    public RollingIndicatorConfiguration()
        : this(null, (RollingIndicatorVersion?)null, null, null, null, null, null)
    {
    }

    [JsonConstructor]
    public RollingIndicatorConfiguration(string? timeframe,
        RollingIndicatorVersion? algorithmVersion, int? atrPeriod,
        RollingAtrMode? atrMode, int? movingAveragePeriod,
        RollingMovingAverageKind? movingAverageKind, int? relativeVolumePeriod)
    {
        Timeframe = timeframe;
        AlgorithmVersion = algorithmVersion;
        AtrPeriod = atrPeriod;
        AtrMode = atrMode;
        MovingAveragePeriod = movingAveragePeriod;
        MovingAverageKind = movingAverageKind;
        RelativeVolumePeriod = relativeVolumePeriod;
    }
}

/// <summary>
/// Immutable bar history supplied to the rolling calculator. HistoryStart is
/// explicit so recursive seeds cannot silently depend on an unknown prefix.
/// </summary>
public sealed record RollingIndicatorInput
{
    public string SourceId { get; }

    public InstrumentId InstrumentId { get; }

    public string Timeframe { get; }

    public UtcTimestamp HistoryStart { get; }

    public ImmutableValueList<ScreenerBar> ClosedBars { get; }

    [JsonIgnore]
    public ImmutableValueList<ScreenerBar> Bars => ClosedBars;

    public RollingIndicatorInput(string sourceId, InstrumentId instrumentId,
        string timeframe, UtcTimestamp historyStart, IEnumerable<ScreenerBar> closedBars)
        : this(sourceId, instrumentId, timeframe, historyStart,
            SnapshotBars(sourceId, instrumentId, closedBars))
    {
    }

    [JsonConstructor]
    public RollingIndicatorInput(string sourceId, InstrumentId instrumentId,
        string timeframe, UtcTimestamp historyStart,
        ImmutableValueList<ScreenerBar> closedBars)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireText(timeframe, nameof(timeframe));
        ArgumentNullException.ThrowIfNull(historyStart);
        ArgumentNullException.ThrowIfNull(closedBars);

        ScreenerBar[] snapshot = closedBars.ToArray();
        if (snapshot.Any(static item => item is null))
        {
            throw new ArgumentException("Rolling bars cannot contain null values.",
                nameof(closedBars));
        }

        if (snapshot.Any(item => !StringComparer.Ordinal.Equals(item.SourceId, sourceId)))
        {
            throw new ArgumentException("Every rolling bar must use the input source.",
                nameof(closedBars));
        }

        if (snapshot.Any(item => item.InstrumentId != instrumentId))
        {
            throw new ArgumentException("Every rolling bar must use the input instrument.",
                nameof(closedBars));
        }

        SourceId = sourceId;
        InstrumentId = instrumentId;
        Timeframe = timeframe;
        HistoryStart = historyStart;
        ClosedBars = new ImmutableValueList<ScreenerBar>(CanonicalBars(snapshot));
    }

    private static ImmutableValueList<ScreenerBar> SnapshotBars(string sourceId,
        InstrumentId instrumentId, IEnumerable<ScreenerBar> closedBars)
    {
        ArgumentNullException.ThrowIfNull(closedBars);
        return new ImmutableValueList<ScreenerBar>(closedBars);
    }

    private static IEnumerable<ScreenerBar> CanonicalBars(IEnumerable<ScreenerBar> bars) =>
        bars.OrderBy(static item => item.Bar.OpenTime.Value)
            .ThenBy(static item => item.Bar.CloseTime.Value)
            .ThenBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.AvailabilityTime.Value)
            .ThenBy(static item => item.ReceiveTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.Bar.EventId, StringComparer.Ordinal)
            .ThenBy(static item => item.Bar.Open.Value)
            .ThenBy(static item => item.Bar.High.Value)
            .ThenBy(static item => item.Bar.Low.Value)
            .ThenBy(static item => item.Bar.Close.Value)
            .ThenBy(static item => item.Bar.Volume.Value)
            .ThenBy(static item => item.QuoteVolume);
}

/// <summary>Canonical quality and causal provenance for the rolling input.</summary>
public sealed record RollingBarQuality
{
    public ImmutableValueList<ScreenerBar> EligibleBars { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonConstructor]
    public RollingBarQuality(ImmutableValueList<ScreenerBar> eligibleBars,
        ImmutableValueList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(eligibleBars);
        ArgumentNullException.ThrowIfNull(reasons);
        if (eligibleBars.Any(static item => item is null))
        {
            throw new ArgumentException("Eligible rolling bars cannot contain null values.",
                nameof(eligibleBars));
        }

        if (reasons.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Rolling quality reasons cannot be blank.",
                nameof(reasons));
        }

        EligibleBars = new ImmutableValueList<ScreenerBar>(eligibleBars);
        Reasons = StableReasons(reasons);
    }

    public RollingBarQuality(IEnumerable<ScreenerBar> eligibleBars, IEnumerable<string> reasons)
        : this(new ImmutableValueList<ScreenerBar>(RequireValues(eligibleBars)),
            new ImmutableValueList<string>(RequireValues(reasons)))
    {
    }

    private static IEnumerable<T> RequireValues<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values;
    }

    private static ImmutableValueList<string> StableReasons(IEnumerable<string> reasons) =>
        new(reasons.Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal));
}

/// <summary>
/// One rolling value with explicit lookback, warmup, quality and seed
/// provenance. A null numeric value is never replaced by a numeric sentinel.
/// </summary>
public sealed record RollingIndicatorValue
{
    public RollingIndicatorKind Kind { get; }

    public RollingIndicatorState State { get; }

    public decimal? Value { get; }

    public int? Period { get; }

    public int RequiredBars { get; }

    public int AvailableBars { get; }

    public RollingIndicatorCompleteness Completeness { get; }

    public ImmutableValueList<ScreenerBar> Provenance { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => State == RollingIndicatorState.Ready && Value.HasValue;

    [JsonIgnore]
    public int? Lookback => Period;

    [JsonIgnore]
    public int RequiredBarCount => RequiredBars;

    [JsonIgnore]
    public int AvailableBarCount => AvailableBars;

    [JsonConstructor]
    public RollingIndicatorValue(RollingIndicatorKind kind, RollingIndicatorState state,
        decimal? value, int? period, int requiredBars, int availableBars,
        RollingIndicatorCompleteness completeness,
        ImmutableValueList<ScreenerBar> provenance,
        ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(kind, nameof(kind));
        ContractGuard.RequireDefined(state, nameof(state));
        ContractGuard.RequireDefined(completeness, nameof(completeness));
        if (requiredBars < 0 || availableBars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBars),
                "Rolling bar counts cannot be negative.");
        }

        if (period is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period,
                "A rolling period must be positive when present.");
        }

        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(reasons);
        if (provenance.Any(static item => item is null))
        {
            throw new ArgumentException("Rolling provenance cannot contain null values.",
                nameof(provenance));
        }

        if (state == RollingIndicatorState.Ready && !value.HasValue)
        {
            throw new ArgumentException("A ready rolling value requires a number.", nameof(value));
        }

        if (state != RollingIndicatorState.Ready && value.HasValue)
        {
            throw new ArgumentException("An unknown rolling value cannot carry a number.",
                nameof(value));
        }

        Kind = kind;
        State = state;
        Value = value;
        Period = period;
        RequiredBars = requiredBars;
        AvailableBars = availableBars;
        Completeness = completeness;
        Provenance = new ImmutableValueList<ScreenerBar>(provenance);
        Reasons = StableReasons(reasons);
    }

    internal static RollingIndicatorValue Create(RollingIndicatorKind kind,
        RollingIndicatorState state, decimal? value, int? period, int requiredBars,
        int availableBars, RollingIndicatorCompleteness completeness,
        IEnumerable<ScreenerBar> provenance, IEnumerable<string> reasons) =>
        new(kind, state, value, period, requiredBars, availableBars, completeness,
            new ImmutableValueList<ScreenerBar>(provenance),
            new ImmutableValueList<string>(reasons));

    private static ImmutableValueList<string> StableReasons(IEnumerable<string> reasons) =>
        new(reasons.Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal));
}

/// <summary>Pure result of one rolling-indicator calculation at one cursor.</summary>
public sealed record RollingIndicatorResult
{
    public RollingIndicatorConfiguration Configuration { get; }

    public RollingIndicatorInput Input { get; }

    public CausalEventCursor Cursor { get; }

    public RollingBarQuality Quality { get; }

    public RollingIndicatorValue Atr { get; }

    public RollingIndicatorValue MovingAverage { get; }

    public RollingIndicatorValue RelativeVolume { get; }

    [JsonIgnore]
    public ImmutableValueList<ScreenerBar> EligibleBars => Quality.EligibleBars;

    [JsonIgnore]
    public ImmutableValueList<string> Reasons => Quality.Reasons;

    [JsonIgnore]
    public RollingIndicatorValue RVOL => RelativeVolume;

    [JsonConstructor]
    public RollingIndicatorResult(RollingIndicatorConfiguration configuration,
        RollingIndicatorInput input, CausalEventCursor cursor, RollingBarQuality quality,
        RollingIndicatorValue atr, RollingIndicatorValue movingAverage,
        RollingIndicatorValue relativeVolume)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(atr);
        ArgumentNullException.ThrowIfNull(movingAverage);
        ArgumentNullException.ThrowIfNull(relativeVolume);

        Configuration = configuration;
        Input = input;
        Cursor = cursor;
        Quality = quality;
        Atr = atr;
        MovingAverage = movingAverage;
        RelativeVolume = relativeVolume;
    }
}

/// <summary>
/// Calculates only ATR, moving average and RVOL from one immutable causal bar
/// snapshot. It performs no I/O, clock reads, persistence, ranking or trading.
/// </summary>
public static class RollingIndicatorCalculator
{
    public const string CurrentAlgorithmVersion = "rolling-v1";

    public static RollingIndicatorResult Calculate(RollingIndicatorInput input,
        CausalEventCursor cursor, RollingIndicatorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(configuration);

        List<string> configurationReasons = [];
        bool configurationValid = ValidateConfiguration(input, cursor, configuration,
            configurationReasons);
        if (!configurationValid)
        {
            RollingBarQuality emptyQuality = new([], configurationReasons);
            return new RollingIndicatorResult(configuration, input, cursor, emptyQuality,
                UnknownConfiguration(RollingIndicatorKind.Atr, configuration.AtrPeriod,
                    configurationReasons),
                UnknownConfiguration(RollingIndicatorKind.MovingAverage,
                    configuration.MovingAveragePeriod, configurationReasons),
                UnknownConfiguration(RollingIndicatorKind.RelativeVolume,
                    configuration.RelativeVolumePeriod, configurationReasons));
        }

        if (input.HistoryStart.Value > cursor.AsOf.Value)
        {
            List<string> inputReasons = ["history-start-after-cursor"];
            RollingBarQuality emptyQuality = new([], inputReasons);
            return new RollingIndicatorResult(input: input, cursor: cursor,
                configuration: configuration, quality: emptyQuality,
                atr: UnknownInput(RollingIndicatorKind.Atr, configuration.AtrPeriod!.Value,
                    inputReasons),
                movingAverage: UnknownInput(RollingIndicatorKind.MovingAverage,
                    configuration.MovingAveragePeriod!.Value, inputReasons),
                relativeVolume: UnknownInput(RollingIndicatorKind.RelativeVolume,
                    configuration.RelativeVolumePeriod!.Value, inputReasons));
        }

        List<string> reasons = [];
        ScreenerBar[] causallyEligible = FilterCausally(input, cursor, reasons);
        ScreenerBar[] canonical = Deduplicate(causallyEligible, reasons);
        AddGaps(canonical, configuration.Timeframe!, reasons);

        RollingBarQuality quality = new(canonical, reasons);
        bool blockedByQuality = HasBlockingQuality(reasons);
        RollingIndicatorValue atr = CalculateAtr(canonical, configuration, blockedByQuality,
            reasons);
        RollingIndicatorValue movingAverage = CalculateMovingAverage(canonical, configuration,
            blockedByQuality, reasons);
        RollingIndicatorValue relativeVolume = CalculateRelativeVolume(canonical,
            configuration, blockedByQuality, reasons);
        return new RollingIndicatorResult(configuration, input, cursor, quality, atr,
            movingAverage, relativeVolume);
    }

    private static bool ValidateConfiguration(RollingIndicatorInput input,
        CausalEventCursor cursor, RollingIndicatorConfiguration configuration,
        List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(configuration.Timeframe))
        {
            reasons.Add("configuration-timeframe-missing");
        }
        else if (!StringComparer.Ordinal.Equals(configuration.Timeframe, input.Timeframe))
        {
            reasons.Add("configuration-timeframe-mismatch");
        }
        else if (!TryParseTimeframe(configuration.Timeframe, out _))
        {
            reasons.Add("configuration-timeframe-invalid");
        }

        if (configuration.AlgorithmVersion is null)
        {
            reasons.Add("configuration-algorithm-version-missing");
        }
        else if (!StringComparer.Ordinal.Equals(configuration.AlgorithmVersion.Value,
            CurrentAlgorithmVersion))
        {
            reasons.Add("configuration-algorithm-version-unsupported");
        }

        ValidatePeriod(configuration.AtrPeriod, "atr-period", reasons, requiresNextBar: true);
        ValidatePeriod(configuration.MovingAveragePeriod, "moving-average-period", reasons);
        ValidatePeriod(configuration.RelativeVolumePeriod, "relative-volume-period", reasons,
            requiresNextBar: true);

        if (configuration.AtrMode is null || !Enum.IsDefined(configuration.AtrMode.Value))
        {
            reasons.Add("configuration-atr-mode-invalid");
        }

        if (configuration.MovingAverageKind is null
            || !Enum.IsDefined(configuration.MovingAverageKind.Value))
        {
            reasons.Add("configuration-moving-average-kind-invalid");
        }

        return reasons.Count == 0;
    }

    private static void ValidatePeriod(int? period, string name, List<string> reasons,
        bool requiresNextBar = false)
    {
        if (period is null)
        {
            reasons.Add($"configuration-{name}-missing");
        }
        else if (period <= 0)
        {
            reasons.Add($"configuration-{name}-invalid");
        }
        else if (requiresNextBar && period == int.MaxValue)
        {
            reasons.Add($"configuration-{name}-too-large");
        }
    }

    private static ScreenerBar[] FilterCausally(RollingIndicatorInput input,
        CausalEventCursor cursor, List<string> reasons)
    {
        List<ScreenerBar> eligible = [];
        foreach (ScreenerBar bar in input.ClosedBars)
        {
            if (bar.EventTime.Value > cursor.AsOf.Value
                || bar.AvailabilityTime.Value > cursor.AsOf.Value
                || bar.StableSequence > cursor.MaxStableSequence
                || bar.CloseTime.Value > cursor.AsOf.Value)
            {
                reasons.Add("future");
                if (bar.AvailabilityTime.Value > cursor.AsOf.Value)
                {
                    reasons.Add("late-availability");
                }

                if (bar.CloseTime.Value > cursor.AsOf.Value)
                {
                    reasons.Add("bar-close-after-cursor");
                }

                if (bar.StableSequence > cursor.MaxStableSequence)
                {
                    reasons.Add("future-sequence");
                }

                continue;
            }

            if (bar.Bar.OpenTime.Value < input.HistoryStart.Value)
            {
                reasons.Add("history-start");
                continue;
            }

            eligible.Add(bar);
        }

        return eligible.ToArray();
    }

    private static ScreenerBar[] Deduplicate(IEnumerable<ScreenerBar> bars,
        List<string> reasons)
    {
        List<ScreenerBar> canonical = [];
        foreach (IGrouping<DateTimeOffset, ScreenerBar> group in bars
            .GroupBy(static bar => bar.Bar.OpenTime.Value)
            .OrderBy(static group => group.Key))
        {
            ScreenerBar[] items = group.ToArray();
            ScreenerBar first = items[0];
            if (items.Any(item => !SamePayload(first, item)))
            {
                reasons.Add("conflict");
                reasons.Add($"conflict:{FormatTime(new UtcTimestamp(group.Key))}");
                continue;
            }

            if (items.Length > 1)
            {
                reasons.Add("duplicate");
            }

            canonical.Add(items
                .OrderByDescending(static item => item.AvailabilityTime.Value)
                .ThenByDescending(static item => item.ReceiveTime.Value)
                .ThenBy(static item => item.StableSequence)
                .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
                .First());
        }

        return canonical
            .OrderBy(static item => item.Bar.OpenTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool SamePayload(ScreenerBar left, ScreenerBar right) =>
        left.Bar == right.Bar
        && left.QuoteVolume == right.QuoteVolume
        && left.IsClosed == right.IsClosed;

    private static void AddGaps(IEnumerable<ScreenerBar> bars, string timeframe,
        List<string> reasons)
    {
        ScreenerBar[] snapshot = bars.ToArray();
        if (snapshot.Length < 2 || !TryParseTimeframe(timeframe, out TimeSpan interval))
        {
            if (snapshot.Length >= 2)
            {
                reasons.Add("gap-timeframe-unknown");
            }

            return;
        }

        for (int index = 1; index < snapshot.Length; index++)
        {
            TimeSpan distance = snapshot[index].Bar.OpenTime.Value
                - snapshot[index - 1].Bar.OpenTime.Value;
            if (distance > interval)
            {
                reasons.Add("gap");
                reasons.Add($"gap:{FormatTime(snapshot[index - 1].Bar.OpenTime)}"
                    + $":{FormatTime(snapshot[index].Bar.OpenTime)}");
            }
        }
    }

    private static RollingIndicatorValue CalculateMovingAverage(ScreenerBar[] bars,
        RollingIndicatorConfiguration configuration, bool blockedByQuality,
        IEnumerable<string> qualityReasons)
    {
        int period = configuration.MovingAveragePeriod!.Value;
        int available = bars.Length;
        int required = period;
        if (blockedByQuality)
        {
            return Unknown(RollingIndicatorKind.MovingAverage,
                RollingIndicatorState.UnknownQuality, period, required, available,
                bars, qualityReasons);
        }

        if (available < required)
        {
            return Unknown(RollingIndicatorKind.MovingAverage,
                RollingIndicatorState.UnknownWarmup, period, required, available,
                bars, ["warmup"]);
        }

        try
        {
            decimal value;
            ScreenerBar[] provenance;
            switch (configuration.MovingAverageKind!.Value)
            {
                case RollingMovingAverageKind.Sma:
                    provenance = TakeLatest(bars, period);
                    value = Mean(provenance.Select(static item => item.Close.Value));
                    break;
                case RollingMovingAverageKind.Wma:
                    provenance = TakeLatest(bars, period);
                    value = WeightedMean(provenance.Select(static item => item.Close.Value));
                    break;
                case RollingMovingAverageKind.Ema:
                    provenance = bars.Take(period).ToArray();
                    value = RecursiveMean(bars.Select(static item => item.Close.Value), period,
                        isRma: false);
                    break;
                case RollingMovingAverageKind.Rma:
                    provenance = bars.Take(period).ToArray();
                    value = RecursiveMean(bars.Select(static item => item.Close.Value), period,
                        isRma: true);
                    break;
                default:
                    return Unknown(RollingIndicatorKind.MovingAverage,
                        RollingIndicatorState.UnknownConfiguration, period, required,
                        available, [], ["configuration-moving-average-kind-invalid"]);
            }

            return Ready(RollingIndicatorKind.MovingAverage, value, period, required,
                available, provenance, qualityReasons);
        }
        catch (OverflowException)
        {
            return Unknown(RollingIndicatorKind.MovingAverage,
                RollingIndicatorState.UnknownOverflow, period, required, available,
                TakeLatest(bars, period), ["decimal-overflow"]);
        }
    }

    private static RollingIndicatorValue CalculateAtr(ScreenerBar[] bars,
        RollingIndicatorConfiguration configuration, bool blockedByQuality,
        IEnumerable<string> qualityReasons)
    {
        int period = configuration.AtrPeriod!.Value;
        int required = checked(period + 1);
        int available = bars.Length;
        if (blockedByQuality)
        {
            return Unknown(RollingIndicatorKind.Atr, RollingIndicatorState.UnknownQuality,
                period, required, available, bars, qualityReasons);
        }

        if (available < required)
        {
            return Unknown(RollingIndicatorKind.Atr, RollingIndicatorState.UnknownWarmup,
                period, required, available, bars, ["warmup"]);
        }

        try
        {
            decimal[] trueRanges = TrueRanges(bars);
            decimal value;
            ScreenerBar[] provenance;
            if (configuration.AtrMode == RollingAtrMode.Sma)
            {
                provenance = TakeLatest(bars, required);
                value = Mean(trueRanges[^period..]);
            }
            else if (configuration.AtrMode == RollingAtrMode.Rma)
            {
                provenance = bars.Take(required).ToArray();
                value = RecursiveMean(trueRanges, period, isRma: true);
            }
            else
            {
                return Unknown(RollingIndicatorKind.Atr,
                    RollingIndicatorState.UnknownConfiguration, period, required,
                    available, [], ["configuration-atr-mode-invalid"]);
            }

            return Ready(RollingIndicatorKind.Atr, value, period, required, available,
                provenance, qualityReasons);
        }
        catch (OverflowException)
        {
            return Unknown(RollingIndicatorKind.Atr, RollingIndicatorState.UnknownOverflow,
                period, required, available, TakeLatest(bars, required),
                ["decimal-overflow"]);
        }
    }

    private static RollingIndicatorValue CalculateRelativeVolume(ScreenerBar[] bars,
        RollingIndicatorConfiguration configuration, bool blockedByQuality,
        IEnumerable<string> qualityReasons)
    {
        int period = configuration.RelativeVolumePeriod!.Value;
        int required = checked(period + 1);
        int available = bars.Length;
        ScreenerBar[] provenance = TakeLatest(bars, required);
        if (blockedByQuality)
        {
            return Unknown(RollingIndicatorKind.RelativeVolume,
                RollingIndicatorState.UnknownQuality, period, required, available, bars,
                qualityReasons);
        }

        if (available < required)
        {
            return Unknown(RollingIndicatorKind.RelativeVolume,
                RollingIndicatorState.UnknownWarmup, period, required, available, bars,
                ["warmup"]);
        }

        try
        {
            decimal sum = 0m;
            for (int index = available - period - 1; index < available - 1; index++)
            {
                sum = checked(sum + bars[index].Bar.Volume.Value);
            }

            if (sum == 0m)
            {
                return Unknown(RollingIndicatorKind.RelativeVolume,
                    RollingIndicatorState.UnknownZeroDenominator, period, required,
                    available, provenance, ["zero-denominator"]);
            }

            decimal mean = checked(sum / period);
            decimal value = checked(bars[^1].Bar.Volume.Value / mean);
            return Ready(RollingIndicatorKind.RelativeVolume, value, period, required,
                available, provenance, qualityReasons);
        }
        catch (OverflowException)
        {
            return Unknown(RollingIndicatorKind.RelativeVolume,
                RollingIndicatorState.UnknownOverflow, period, required, available,
                provenance, ["decimal-overflow"]);
        }
    }

    private static decimal[] TrueRanges(ScreenerBar[] bars)
    {
        decimal[] ranges = new decimal[bars.Length];
        for (int index = 0; index < bars.Length; index++)
        {
            decimal previousClose = index == 0
                ? bars[index].Close.Value
                : bars[index - 1].Close.Value;
            decimal highLow = checked(bars[index].Bar.High.Value - bars[index].Bar.Low.Value);
            decimal highGap = Abs(checked(bars[index].Bar.High.Value - previousClose));
            decimal lowGap = Abs(checked(bars[index].Bar.Low.Value - previousClose));
            ranges[index] = Math.Max(highLow, Math.Max(highGap, lowGap));
        }

        return ranges;
    }

    private static decimal RecursiveMean(IEnumerable<decimal> values, int period, bool isRma)
    {
        decimal[] snapshot = values.ToArray();
        decimal previous = Mean(snapshot[..period]);
        decimal divisor = isRma ? period : period + 1m;
        decimal factor = isRma ? 0m : checked(2m / divisor);
        for (int index = period; index < snapshot.Length; index++)
        {
            decimal adjustment = isRma
                ? checked((snapshot[index] - previous) / divisor)
                : checked(factor * (snapshot[index] - previous));
            previous = checked(previous + adjustment);
        }

        return previous;
    }

    private static decimal Mean(IEnumerable<decimal> values)
    {
        decimal sum = 0m;
        int count = 0;
        foreach (decimal value in values)
        {
            sum = checked(sum + value);
            count = checked(count + 1);
        }

        return count == 0 ? throw new DivideByZeroException() : checked(sum / count);
    }

    private static decimal WeightedMean(IEnumerable<decimal> values)
    {
        decimal numerator = 0m;
        decimal denominator = 0m;
        int weight = 0;
        foreach (decimal value in values)
        {
            weight = checked(weight + 1);
            numerator = checked(numerator + checked(value * weight));
            denominator = checked(denominator + weight);
        }

        return denominator == 0m
            ? throw new DivideByZeroException()
            : checked(numerator / denominator);
    }

    private static decimal Abs(decimal value) => value < 0m ? checked(-value) : value;

    private static ScreenerBar[] TakeLatest(ScreenerBar[] bars, int count)
    {
        int start = Math.Max(0, bars.Length - count);
        ScreenerBar[] result = new ScreenerBar[bars.Length - start];
        for (int index = start; index < bars.Length; index++)
        {
            result[index - start] = bars[index];
        }

        return result;
    }

    private static RollingIndicatorValue Ready(RollingIndicatorKind kind, decimal value,
        int period, int required, int available, IEnumerable<ScreenerBar> provenance,
        IEnumerable<string> reasons) => RollingIndicatorValue.Create(kind,
            RollingIndicatorState.Ready, value, period, required, available,
            reasons.Any() ? RollingIndicatorCompleteness.Incomplete
                : RollingIndicatorCompleteness.Complete, provenance, reasons);

    private static RollingIndicatorValue Unknown(RollingIndicatorKind kind,
        RollingIndicatorState state, int? period, int required, int available,
        IEnumerable<ScreenerBar> provenance, IEnumerable<string> reasons) =>
        RollingIndicatorValue.Create(kind, state, null, period, required, available,
            state == RollingIndicatorState.UnknownWarmup
                ? RollingIndicatorCompleteness.Warmup
                : state == RollingIndicatorState.UnknownConfiguration
                    ? RollingIndicatorCompleteness.Unknown
                    : RollingIndicatorCompleteness.Incomplete,
            provenance, reasons);

    private static RollingIndicatorValue UnknownConfiguration(RollingIndicatorKind kind,
        int? period, IEnumerable<string> reasons)
    {
        int? validPeriod = period is > 0 ? period : null;
        return Unknown(kind, RollingIndicatorState.UnknownConfiguration, validPeriod,
            validPeriod ?? 0, 0, [], reasons);
    }

    private static RollingIndicatorValue UnknownInput(RollingIndicatorKind kind, int period,
        IEnumerable<string> reasons)
    {
        int required = kind is RollingIndicatorKind.Atr or RollingIndicatorKind.RelativeVolume
            ? checked(period + 1)
            : period;
        return Unknown(kind, RollingIndicatorState.UnknownInput, period, required, 0, [], reasons);
    }

    private static bool HasBlockingQuality(IEnumerable<string> reasons) => reasons.Any(reason =>
        StringComparer.Ordinal.Equals(reason, "conflict")
        || StringComparer.Ordinal.Equals(reason, "gap")
        || StringComparer.Ordinal.Equals(reason, "gap-timeframe-unknown"));

    private static bool TryParseTimeframe(string timeframe, out TimeSpan interval)
    {
        interval = default;
        string value = timeframe.Trim();
        string suffix;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            suffix = "ms";
        }
        else if (value.Length > 0)
        {
            suffix = value[^1].ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            return false;
        }

        string number = value[..^suffix.Length];
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture,
            out long count) || count <= 0)
        {
            return false;
        }

        try
        {
            interval = suffix.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(count),
                "s" => TimeSpan.FromSeconds(count),
                "m" => TimeSpan.FromMinutes(count),
                "h" => TimeSpan.FromHours(count),
                "d" => TimeSpan.FromDays(count),
                "w" => TimeSpan.FromDays(checked(count * 7L)),
                _ => default
            };
            return interval > TimeSpan.Zero;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string FormatTime(UtcTimestamp timestamp) =>
        timestamp.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
