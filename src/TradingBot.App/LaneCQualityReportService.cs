using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

public static class LaneCQualityReportService
{
    public static Task<LaneCQualityReport> BuildAsync(ParquetCaptureStore store,
        CaptureDatasetManifest manifest, LaneCFeatureInput input,
        LaneCFeatureConfiguration configuration, LaneCQualityPolicy policy,
        CaptureQualitySnapshotBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        return LaneCQualityReportBuilder.BuildAsync(store, manifest, input, configuration,
            features, policy, options, cancellationToken);
    }
}
