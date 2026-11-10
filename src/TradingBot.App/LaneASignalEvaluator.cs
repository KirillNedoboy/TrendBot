using TradingBot.Core;

namespace TradingBot.App;

/// <summary>Explicit caller-owned settings for passive Lane A signal evaluation.</summary>
public sealed record LaneASignalEvaluationConfiguration(
    PriceContextFeatureConfiguration PriceContext,
    FeatureQualityPolicy QualityPolicy,
    decimal? BoundaryMinimumChannelWidth);

/// <summary>All feature and setup evidence produced for one closed-bar cursor.</summary>
public sealed record LaneASignalEvaluationResult(
    PriceContextFeatureResult PriceContext,
    FeatureQualityEvaluation Quality,
    BoundaryRejectionSetupResult BoundaryRejection,
    CenterRetestSetupResult CenterRetest)
{
    /// <summary>Same-cut four-state passive view of both setup families.</summary>
    public LaneASetupObservationProjection SetupObservations =>
        new(BoundaryRejection, CenterRetest);
}

/// <summary>Connects price-context and quality calculations to the two passive Lane A setup evaluators.</summary>
public static class LaneASignalEvaluator
{
    public static LaneASignalEvaluationResult Evaluate(PriceContextFeatureInput input,
        CausalEventCursor cursor, LaneASignalEvaluationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.PriceContext);
        ArgumentNullException.ThrowIfNull(configuration.QualityPolicy);

        PriceContextFeatureResult context = PriceContextFeatureCalculator.Calculate(
            input, cursor, configuration.PriceContext);
        FeatureQualityEvaluation quality = FeatureQualityEvaluator.Evaluate(
            context, configuration.QualityPolicy);
        BoundaryRejectionSetupResult boundary = BoundaryRejectionSetupEvaluator.Evaluate(
            context, quality, new BoundaryRejectionSetupConfiguration(
                new BoundaryRejectionSetupVersion(
                    BoundaryRejectionSetupResult.CurrentAlgorithmVersion),
                configuration.BoundaryMinimumChannelWidth));
        CenterRetestSetupResult center = CenterRetestSetupEvaluator.Evaluate(
            context, quality, new CenterRetestSetupConfiguration(
                new CenterRetestSetupVersion(CenterRetestSetupResult.CurrentAlgorithmVersion)));
        return new LaneASignalEvaluationResult(context, quality, boundary, center);
    }
}
