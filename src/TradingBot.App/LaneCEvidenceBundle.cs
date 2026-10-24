using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

public sealed record LaneCEvidenceBundle
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public string BundleIdentity { get; }
    public string ManifestId { get; }
    public CaptureManifestCheckpoint Checkpoint { get; }
    public string QualityReportIdentity { get; }
    public string ExclusionLedgerIdentity { get; }
    public LaneCFeatureResult Features { get; }
    public LaneCQualityReport QualityReport { get; }
    public LaneCCaptureExclusionLedger Exclusions { get; }
    public LaneCShadowResult Shadow { get; }
    public DecisionEvidence DecisionEvidence { get; }

    [JsonConstructor]
    public LaneCEvidenceBundle(int formatVersion, string bundleIdentity, string manifestId,
        CaptureManifestCheckpoint checkpoint, string qualityReportIdentity,
        string exclusionLedgerIdentity, LaneCFeatureResult features,
        LaneCQualityReport qualityReport, LaneCCaptureExclusionLedger exclusions,
        LaneCShadowResult shadow, DecisionEvidence decisionEvidence)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestId);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualityReportIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(exclusionLedgerIdentity);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(qualityReport);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(shadow);
        ArgumentNullException.ThrowIfNull(decisionEvidence);
        if (!StringComparer.Ordinal.Equals(manifestId, qualityReport.ManifestId)
            || checkpoint != qualityReport.Checkpoint
            || !StringComparer.Ordinal.Equals(qualityReportIdentity,
                qualityReport.ReportIdentity)
            || !StringComparer.Ordinal.Equals(exclusions.ManifestId, manifestId)
            || exclusions.Checkpoint != checkpoint
            || !StringComparer.Ordinal.Equals(exclusions.QualityReportIdentity,
                qualityReportIdentity)
            || !StringComparer.Ordinal.Equals(qualityReport.Quality.InputIdentity,
                features.InputIdentity)
            || qualityReport.Quality.Cursor != features.Cursor
            || !StringComparer.Ordinal.Equals(shadow.InputIdentity, features.InputIdentity)
            || shadow.Cursor != features.Cursor
            || decisionEvidence.Lane != DecisionLane.LaneC
            || decisionEvidence.Cursor != shadow.Cursor
            || !StringComparer.Ordinal.Equals(decisionEvidence.EvidenceId,
                shadow.ResultIdentity)
            || !StringComparer.Ordinal.Equals(exclusionLedgerIdentity,
                exclusions.LedgerIdentity))
        {
            throw new ArgumentException("Lane C evidence bundle links are inconsistent.",
                nameof(bundleIdentity));
        }
        string expectedIdentity = ComputeIdentity(manifestId, checkpoint,
            qualityReportIdentity, exclusionLedgerIdentity, features, qualityReport,
            exclusions, shadow, decisionEvidence);
        if (!StringComparer.Ordinal.Equals(bundleIdentity, expectedIdentity))
        {
            throw new ArgumentException("Lane C evidence bundle identity is invalid.",
                nameof(bundleIdentity));
        }
        FormatVersion = formatVersion;
        BundleIdentity = bundleIdentity;
        ManifestId = manifestId;
        Checkpoint = checkpoint;
        QualityReportIdentity = qualityReportIdentity;
        ExclusionLedgerIdentity = exclusionLedgerIdentity;
        Features = features;
        QualityReport = qualityReport;
        Exclusions = exclusions;
        Shadow = shadow;
        DecisionEvidence = decisionEvidence;
    }

    public static string ComputeIdentity(string manifestId,
        CaptureManifestCheckpoint checkpoint, string qualityReportIdentity,
        string exclusionLedgerIdentity, LaneCFeatureResult features,
        LaneCQualityReport qualityReport, LaneCCaptureExclusionLedger exclusions,
        LaneCShadowResult shadow, DecisionEvidence decisionEvidence)
    {
        string json = CoreJsonSerializer.Serialize(new
        {
            FormatVersion = CurrentFormatVersion,
            ManifestId = manifestId,
            Checkpoint = checkpoint,
            QualityReportIdentity = qualityReportIdentity,
            ExclusionLedgerIdentity = exclusionLedgerIdentity,
            Features = features,
            QualityReport = qualityReport,
            Exclusions = exclusions,
            Shadow = shadow,
            DecisionEvidence = decisionEvidence
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }
}

public static class LaneCEvidenceBundleBuilder
{
    public static LaneCEvidenceBundle Build(CaptureDatasetManifest manifest,
        LaneCQualityReport qualityReport, LaneCCaptureExclusionLedger exclusions,
        LaneCFeatureResult features, LaneCFeatureRuleTable rules, string provenance)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(qualityReport);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenance);
        LaneCShadowResult shadow = LaneCShadowEvaluator.Evaluate(features, rules);
        DecisionEvidence evidence = DecisionEvidence.FromLaneCShadow(shadow, provenance);
        string identity = LaneCEvidenceBundle.ComputeIdentity(manifest.ManifestId,
            manifest.Checkpoint, qualityReport.ReportIdentity, exclusions.LedgerIdentity,
            features, qualityReport, exclusions, shadow, evidence);
        return new LaneCEvidenceBundle(LaneCEvidenceBundle.CurrentFormatVersion, identity,
            manifest.ManifestId, manifest.Checkpoint, qualityReport.ReportIdentity,
            exclusions.LedgerIdentity, features, qualityReport, exclusions, shadow, evidence);
    }
}
