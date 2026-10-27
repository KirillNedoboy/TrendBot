using System.Diagnostics.Metrics;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>
/// Publishes feature-quality readiness evidence through fixed BCL metrics.
/// Unknown age and coverage remain explicit sentinel values (-1); no dynamic
/// feature identifiers are emitted as metric tags.
/// </summary>
public sealed class FeatureQualityMetricsPublisher : IDisposable
{
    public const string MeterName = "TradingBot.Infrastructure.FeatureQuality";

    private readonly Meter _meter = new(MeterName, "1.0");
    private readonly Counter<long> _evaluationCounter;
    private readonly Counter<long> _readyCounter;
    private readonly Counter<long> _blockedCounter;
    private readonly Counter<long> _reasonCounter;
    private readonly ObservableGauge<long> _readyGauge;
    private readonly ObservableGauge<long> _reasonGauge;
    private readonly ObservableGauge<long> _marketDataAgeGauge;
    private readonly ObservableGauge<long> _availabilityAgeGauge;
    private readonly ObservableGauge<double> _knownCoverageGauge;
    private readonly ObservableGauge<long> _coverageKnownGauge;
    private readonly ObservableGauge<long> _freshnessGauge;
    private readonly HashSet<string> _publishedEvaluations = new(StringComparer.Ordinal);
    private FeatureQualityEvaluation? _latest;
    private bool _disposed;

    public FeatureQualityMetricsPublisher()
    {
        _evaluationCounter = _meter.CreateCounter<long>("feature_quality.evaluations");
        _readyCounter = _meter.CreateCounter<long>("feature_quality.ready");
        _blockedCounter = _meter.CreateCounter<long>("feature_quality.blocked");
        _reasonCounter = _meter.CreateCounter<long>("feature_quality.reasons");
        _readyGauge = _meter.CreateObservableGauge<long>("feature_quality.ready_count",
            () => CountReady(_latest));
        _reasonGauge = _meter.CreateObservableGauge<long>("feature_quality.reason_count",
            () => CountReasons(_latest));
        _marketDataAgeGauge = _meter.CreateObservableGauge<long>(
            "feature_quality.market_data_age_ms", () => MaxAge(_latest, marketData: true));
        _availabilityAgeGauge = _meter.CreateObservableGauge<long>(
            "feature_quality.availability_age_ms", () => MaxAge(_latest, marketData: false));
        _knownCoverageGauge = _meter.CreateObservableGauge<double>(
            "feature_quality.known_coverage", () => KnownCoverage(_latest));
        _coverageKnownGauge = _meter.CreateObservableGauge<long>(
            "feature_quality.coverage_known", () => CoverageKnown(_latest));
        _freshnessGauge = _meter.CreateObservableGauge<long>(
            "feature_quality.freshness_status", () => FreshnessStatus(_latest));
    }

    public Meter Meter => _meter;

    public void Publish(FeatureQualitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Publish(snapshot.Quality);
    }

    public void Publish(FeatureQualityEvaluation evaluation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(evaluation);
        _latest = evaluation;
        string fingerprint = CoreJsonSerializer.Serialize(evaluation);
        if (!_publishedEvaluations.Add(fingerprint))
        {
            return;
        }

        long ready = CountReady(evaluation);
        long reasons = CountReasons(evaluation);
        long blocked = evaluation.Features.Count - ready;
        _evaluationCounter.Add(1);
        _readyCounter.Add(ready);
        _blockedCounter.Add(blocked);
        _reasonCounter.Add(reasons);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meter.Dispose();
        GC.SuppressFinalize(this);
    }

    private static long CountReady(FeatureQualityEvaluation? evaluation) =>
        evaluation is null ? 0 : evaluation.Features.LongCount(static feature => feature.IsReady);

    private static long CountReasons(FeatureQualityEvaluation? evaluation) => evaluation is null
        ? 0
        : evaluation.Features.Sum(static feature => (long)feature.Reasons.Count);

    private static long MaxAge(FeatureQualityEvaluation? evaluation, bool marketData)
    {
        if (evaluation is null)
        {
            return -1;
        }

        TimeSpan?[] ages = evaluation.Features.Select(feature => marketData
            ? feature.MarketDataAge
            : feature.AvailabilityAge).ToArray();
        return ages.All(static age => age is null)
            ? -1
            : ages.Where(static age => age is not null)
                .Select(static age => Milliseconds(age!.Value))
                .DefaultIfEmpty(-1)
                .Max();
    }

    private static double KnownCoverage(FeatureQualityEvaluation? evaluation)
    {
        if (evaluation is null || evaluation.Features.Count == 0
            || evaluation.Features.Any(static feature => !feature.KnownCoverage.HasValue))
        {
            return -1d;
        }

        return evaluation.Features.Min(static feature =>
            (double)feature.KnownCoverage!.Value);
    }

    private static long CoverageKnown(FeatureQualityEvaluation? evaluation) =>
        evaluation is not null && evaluation.Features.Count > 0
        && evaluation.Features.All(static feature => feature.KnownCoverage.HasValue)
            ? 1
            : 0;

    private static long FreshnessStatus(FeatureQualityEvaluation? evaluation)
    {
        if (evaluation is null || evaluation.Features.Count == 0)
        {
            return 0;
        }

        if (evaluation.Features.Any(static feature =>
            feature.Freshness == FeatureQualityFreshness.Stale))
        {
            return 2;
        }

        return evaluation.Features.All(static feature =>
            feature.Freshness == FeatureQualityFreshness.Fresh) ? 1 : 0;
    }

    private static long Milliseconds(TimeSpan value) =>
        value.TotalMilliseconds >= long.MaxValue ? long.MaxValue :
        value.TotalMilliseconds <= 0d ? 0L : (long)value.TotalMilliseconds;
}
