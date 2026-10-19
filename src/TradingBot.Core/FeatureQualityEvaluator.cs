namespace TradingBot.Core;

/// <summary>
/// Independently evaluates freshness, coverage, and derived readiness for the
/// existing rolling-v1 and price-context-v2 calculations. It performs no I/O or
/// clock reads and never changes a calculator result.
/// </summary>
public static class FeatureQualityEvaluator
{
    public static FeatureQualityEvaluation Evaluate(RollingIndicatorResult result,
        FeatureQualityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(policy);

        SeriesAssessment assessment = Assess(result.Quality.EligibleBars,
            result.Cursor, policy, FeatureQualityInputSeries.Rolling,
            result.Quality.Reasons);
        string formula = result.Configuration.AlgorithmVersion?.Value ?? "unknown";
        FeatureQualityEvidence[] features =
        [
            CreateRollingEvidence(FeatureQualityFeatureKind.RollingAtr, result.Atr,
                formula, assessment),
            CreateRollingEvidence(FeatureQualityFeatureKind.RollingMovingAverage,
                result.MovingAverage, formula, assessment),
            CreateRollingEvidence(FeatureQualityFeatureKind.RollingRelativeVolume,
                result.RelativeVolume, formula, assessment)
        ];
        return FeatureQualityEvaluation.Create(result.Cursor, policy,
            result.Configuration.AlgorithmVersion?.Value, null, features);
    }

    public static FeatureQualityEvaluation Evaluate(PriceContextFeatureResult result,
        FeatureQualityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(policy);

        SeriesAssessment setup = Assess(result.Quality.EligibleSetupBars,
            result.Cursor, policy, FeatureQualityInputSeries.Setup, result.Quality.Reasons);
        SeriesAssessment htf = Assess(result.Quality.EligibleHtfBars,
            result.Cursor, policy, FeatureQualityInputSeries.Htf, result.Quality.Reasons);
        SeriesAssessment priceVolume = Assess(result.Quality.EligiblePriceVolumeObservations,
            result.Cursor, policy, FeatureQualityInputSeries.PriceVolume,
            result.Quality.Reasons);
        string formula = result.Configuration.AlgorithmVersion?.Value ?? "unknown";
        FeatureQualityEvidence[] features =
        [
            CreatePriceEvidence(FeatureQualityFeatureKind.PriceContextConfirmedPivots,
                result.ConfirmedPivots.State, result.ConfirmedPivots.Completeness,
                result.ConfirmedPivots.RequiredBars, result.ConfirmedPivots.AvailableBars,
                formula, setup, result.ConfirmedPivots.Reasons),
            CreatePriceEvidence(FeatureQualityFeatureKind.PriceContextStructuralLevels,
                result.StructuralLevels.State, result.StructuralLevels.Completeness,
                result.StructuralLevels.RequiredBars, result.StructuralLevels.AvailableBars,
                formula, setup, result.StructuralLevels.Reasons),
            CreatePriceEvidence(FeatureQualityFeatureKind.PriceContextChannel,
                result.Channel.State, result.Channel.Completeness, result.Channel.RequiredBars,
                result.Channel.AvailableBars, formula, setup, result.Channel.Reasons),
            CreatePriceEvidence(FeatureQualityFeatureKind.PriceContextLocalTrend,
                result.LocalTrend.State, result.Channel.Completeness, result.Channel.RequiredBars,
                result.Channel.AvailableBars, formula, setup, result.LocalTrend.Reasons),
            CreatePriceEvidence(FeatureQualityFeatureKind.PriceContextHtfContext,
                result.HtfContext.State,
                result.HtfContext.State == PriceContextFeatureState.Ready
                    ? PriceContextFeatureCompleteness.Complete
                    : PriceContextFeatureCompleteness.Incomplete,
                RequiredHtfBars(result.Configuration), result.HtfContext.Provenance.Count,
                formula, htf, result.HtfContext.Reasons),
            CreatePriceEvidence(FeatureQualityFeatureKind.PriceContextVwap,
                result.Vwap.State, result.Vwap.Completeness,
                result.Vwap.RequiredObservations, result.Vwap.AvailableObservations, formula,
                priceVolume, result.Vwap.Reasons),
            CreatePriceEvidence(FeatureQualityFeatureKind.PriceContextPoc,
                result.Poc.State, result.Poc.Completeness,
                result.Poc.RequiredObservations, result.Poc.AvailableObservations, formula,
                priceVolume, result.Poc.Reasons)
        ];
        return FeatureQualityEvaluation.Create(result.Cursor, policy, null, formula, features);
    }

    public static FeatureQualityEvaluation Evaluate(RollingIndicatorResult rollingResult,
        PriceContextFeatureResult priceContextResult, FeatureQualityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(rollingResult);
        ArgumentNullException.ThrowIfNull(priceContextResult);
        ArgumentNullException.ThrowIfNull(policy);
        if (rollingResult.Cursor != priceContextResult.Cursor)
        {
            throw new ArgumentException(
                "Rolling and price-context results must use the same causal cursor.",
                nameof(priceContextResult));
        }

        FeatureQualityEvaluation rolling = Evaluate(rollingResult, policy);
        FeatureQualityEvaluation price = Evaluate(priceContextResult, policy);
        return FeatureQualityEvaluation.Create(rollingResult.Cursor, policy,
            rolling.RollingFormulaVersion, price.PriceContextFormulaVersion,
            rolling.Features.Concat(price.Features));
    }

    private static FeatureQualityEvidence CreateRollingEvidence(
        FeatureQualityFeatureKind kind, RollingIndicatorValue value, string formula,
        SeriesAssessment assessment)
    {
        int required = value.RequiredBars;
        int available = Math.Min(assessment.KnownObservationCount, value.AvailableBars);
        return CreateEvidence(kind, assessment.Series, formula, value.State.ToString(),
            value.Completeness.ToString(), required, available, assessment,
            value.Reasons);
    }

    private static FeatureQualityEvidence CreatePriceEvidence(
        FeatureQualityFeatureKind kind, PriceContextFeatureState state,
        PriceContextFeatureCompleteness completeness, int required, int available,
        string formula, SeriesAssessment assessment, IEnumerable<string> featureReasons) =>
        CreateEvidence(kind, assessment.Series, formula, state.ToString(),
            completeness.ToString(), required, Math.Min(assessment.KnownObservationCount,
                Math.Max(0, available)), assessment, featureReasons);

    private static FeatureQualityEvidence CreateEvidence(FeatureQualityFeatureKind kind,
        FeatureQualityInputSeries series, string formula, string originalState,
        string originalCompleteness, int required, int available,
        SeriesAssessment assessment, IEnumerable<string> featureReasons)
    {
        bool originalReady = StringComparer.Ordinal.Equals(originalState, "Ready");
        bool complete = StringComparer.Ordinal.Equals(originalCompleteness, "Complete");
        List<string> reasons = [.. assessment.Reasons, .. featureReasons];
        if (!complete)
        {
            reasons.Add("incomplete-input");
        }

        FeatureQualityCoverage featureCoverage = required <= 0
            ? FeatureQualityCoverage.Unknown
            : available >= required
                ? FeatureQualityCoverage.Complete
                : FeatureQualityCoverage.Partial;
        bool blockedByQuality = reasons.Any(IsBlockingReason);
        bool isReady = originalReady && complete
            && assessment.Freshness == FeatureQualityFreshness.Fresh
            && featureCoverage == FeatureQualityCoverage.Complete
            && !blockedByQuality;
        if (!originalReady)
        {
            reasons.Add($"original-state:{originalState.ToLowerInvariant()}");
        }

        return FeatureQualityEvidence.Create(kind, series, formula, originalState,
            originalCompleteness, FeatureQualityFreshness.Unknown, assessment.Freshness,
            assessment.MarketDataAge, assessment.AvailabilityAge, assessment.TimeToLive,
            featureCoverage, CoverageRatio(available, required), available, required,
            isReady, reasons);
    }

    private static SeriesAssessment Assess<T>(IEnumerable<T> observations,
        CausalEventCursor cursor, FeatureQualityPolicy policy, FeatureQualityInputSeries series,
        IEnumerable<string> existingReasons) where T : class
    {
        T[] values = observations.Where(static item => item is not null).Distinct().ToArray();
        DateTimeOffset? latestMarket = null;
        DateTimeOffset? latestAvailability = null;
        foreach (T value in values)
        {
            DateTimeOffset marketTime;
            DateTimeOffset availabilityTime;
            switch (value)
            {
                case ScreenerBar bar:
                    marketTime = bar.CloseTime.Value;
                    availabilityTime = bar.AvailabilityTime.Value;
                    break;
                case PriceVolumeObservation observation:
                    marketTime = observation.EventTime.Value;
                    availabilityTime = observation.AvailabilityTime.Value;
                    break;
                default:
                    throw new ArgumentException(
                        "Feature quality received an unsupported observation type.",
                        nameof(observations));
            }

            latestMarket = latestMarket is null || marketTime > latestMarket.Value
                ? marketTime
                : latestMarket;
            latestAvailability = latestAvailability is null
                || availabilityTime > latestAvailability.Value
                ? availabilityTime
                : latestAvailability;
        }

        TimeSpan? marketAge = Age(cursor.AsOf.Value, latestMarket);
        TimeSpan? availabilityAge = Age(cursor.AsOf.Value, latestAvailability);
        TimeSpan? ttl = policy.GetTtl(series);
        List<string> reasons = [.. existingReasons];
        FeatureQualityFreshness freshness;
        if (ttl is null)
        {
            freshness = FeatureQualityFreshness.Unknown;
            reasons.Add("ttl-missing:" + SeriesName(series));
        }
        else if (!FeatureQualityPolicy.IsValidTtl(ttl))
        {
            freshness = FeatureQualityFreshness.Unknown;
            reasons.Add("ttl-invalid:" + SeriesName(series));
        }
        else if (marketAge is null || availabilityAge is null)
        {
            freshness = FeatureQualityFreshness.Unknown;
            reasons.Add("age-unknown:" + SeriesName(series));
        }
        else if (marketAge.Value > ttl.Value || availabilityAge.Value > ttl.Value)
        {
            freshness = FeatureQualityFreshness.Stale;
            if (marketAge.Value > ttl.Value)
            {
                reasons.Add("market-data-stale:" + SeriesName(series));
            }

            if (availabilityAge.Value > ttl.Value)
            {
                reasons.Add("availability-stale:" + SeriesName(series));
            }
        }
        else
        {
            freshness = FeatureQualityFreshness.Fresh;
        }

        int known = values.Length;
        FeatureQualityCoverage coverage = known == 0
            ? FeatureQualityCoverage.Unknown
            : FeatureQualityCoverage.Complete;
        return new SeriesAssessment(series, freshness, marketAge, availabilityAge, ttl,
            coverage, known, new ImmutableValueList<string>(reasons));
    }

    private static int RequiredHtfBars(PriceContextFeatureConfiguration configuration)
    {
        int fast = configuration.HtfFastPeriod is > 0 ? configuration.HtfFastPeriod.Value : 0;
        int slow = configuration.HtfSlowPeriod is > 0 ? configuration.HtfSlowPeriod.Value : 0;
        return Math.Max(fast, slow);
    }

    private static decimal? CoverageRatio(int available, int required)
    {
        if (required <= 0)
        {
            return null;
        }

        return Math.Min(1m, checked((decimal)Math.Max(0, available) / required));
    }

    private static TimeSpan? Age(DateTimeOffset cursor, DateTimeOffset? latest)
    {
        if (latest is null)
        {
            return null;
        }

        TimeSpan age = cursor - latest.Value;
        return age < TimeSpan.Zero ? null : age;
    }

    private static bool IsBlockingReason(string reason) =>
        reason.Contains("gap", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("conflict", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("incomplete", StringComparison.OrdinalIgnoreCase);

    private static string SeriesName(FeatureQualityInputSeries series) => series switch
    {
        FeatureQualityInputSeries.Rolling => "rolling",
        FeatureQualityInputSeries.Setup => "setup",
        FeatureQualityInputSeries.Htf => "htf",
        FeatureQualityInputSeries.PriceVolume => "price-volume",
        _ => "unknown"
    };

    private sealed record SeriesAssessment(FeatureQualityInputSeries Series,
        FeatureQualityFreshness Freshness, TimeSpan? MarketDataAge,
        TimeSpan? AvailabilityAge, TimeSpan? TimeToLive, FeatureQualityCoverage Coverage,
        int KnownObservationCount, ImmutableValueList<string> Reasons);
}
