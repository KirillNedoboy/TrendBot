using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Canonical, replayable feature-quality evidence for one causal cursor. The
/// complete calculator results retain their inputs/configuration while Quality
/// retains the independent feature-quality-v1 assessment.
/// </summary>
public sealed record FeatureQualitySnapshot
{
    public const int CurrentFormatVersion = 1;

    public const string CurrentAlgorithmVersion = "feature-quality-v1";

    public int FormatVersion { get; }

    public string AlgorithmVersion { get; }

    public RollingIndicatorResult? RollingResult { get; }

    public PriceContextFeatureResult? PriceContextResult { get; }

    public CausalEventCursor Cursor { get; }

    public FeatureQualityPolicy Policy { get; }

    public string? RollingFormulaVersion { get; }

    public string? PriceContextFormulaVersion { get; }

    public FeatureQualityEvaluation Quality { get; }

    /// <summary>
    /// Lower-case SHA-256 of this snapshot's canonical JSON. Identity is not
    /// part of that JSON, so equivalent snapshots have the same identity.
    /// </summary>
    [JsonIgnore]
    public string SnapshotId { get; }

    [JsonIgnore]
    public string Identity => SnapshotId;

    [JsonIgnore]
    public byte[] IdentityBytes => Convert.FromHexString(SnapshotId);

    [JsonIgnore]
    public ImmutableValueList<string> FormulaVersions => new(
        new[] { RollingFormulaVersion, PriceContextFormulaVersion }
            .Where(static value => value is not null)
            .Select(static value => value!));

    public static FeatureQualitySnapshot Create(RollingIndicatorResult result,
        FeatureQualityPolicy policy) => new(result, null, policy);

    public static FeatureQualitySnapshot Create(PriceContextFeatureResult result,
        FeatureQualityPolicy policy) => new(null, result, policy);

    public static FeatureQualitySnapshot Create(RollingIndicatorResult rollingResult,
        PriceContextFeatureResult priceContextResult, FeatureQualityPolicy policy) =>
        new(rollingResult, priceContextResult, policy);

    public static FeatureQualitySnapshot FromResults(RollingIndicatorResult result,
        FeatureQualityPolicy policy) => Create(result, policy);

    public static FeatureQualitySnapshot FromResults(PriceContextFeatureResult result,
        FeatureQualityPolicy policy) => Create(result, policy);

    public static FeatureQualitySnapshot FromResults(RollingIndicatorResult rollingResult,
        PriceContextFeatureResult priceContextResult, FeatureQualityPolicy policy) =>
        Create(rollingResult, priceContextResult, policy);

    public FeatureQualitySnapshot(RollingIndicatorResult result,
        FeatureQualityPolicy policy)
        : this(result, null, policy)
    {
    }

    public FeatureQualitySnapshot(PriceContextFeatureResult result,
        FeatureQualityPolicy policy)
        : this(null, result, policy)
    {
    }

    public FeatureQualitySnapshot(RollingIndicatorResult? rollingResult,
        PriceContextFeatureResult? priceContextResult, FeatureQualityPolicy policy)
        : this(CurrentFormatVersion, CurrentAlgorithmVersion, rollingResult,
            priceContextResult, ResolveCursor(rollingResult, priceContextResult), policy,
            ResolveRollingFormula(rollingResult), ResolvePriceContextFormula(priceContextResult),
            Evaluate(rollingResult, priceContextResult, policy))
    {
    }

    [JsonConstructor]
    public FeatureQualitySnapshot(int formatVersion, string algorithmVersion,
        RollingIndicatorResult? rollingResult, PriceContextFeatureResult? priceContextResult,
        CausalEventCursor cursor, FeatureQualityPolicy policy,
        string? rollingFormulaVersion, string? priceContextFormulaVersion,
        FeatureQualityEvaluation quality)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The feature quality snapshot format version is unsupported.");
        }

        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithmVersion), algorithmVersion,
                "The feature quality snapshot algorithm version is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(quality);
        if (rollingResult is null && priceContextResult is null)
        {
            throw new ArgumentException(
                "A feature quality snapshot requires at least one calculator result.",
                nameof(rollingResult));
        }

        if (rollingResult is not null && rollingResult.Cursor != cursor)
        {
            throw new ArgumentException(
                "The rolling result cursor must match the snapshot cursor.",
                nameof(rollingResult));
        }

        if (priceContextResult is not null && priceContextResult.Cursor != cursor)
        {
            throw new ArgumentException(
                "The price-context result cursor must match the snapshot cursor.",
                nameof(priceContextResult));
        }

        ValidateFormula(rollingFormulaVersion, RollingIndicatorCalculator.CurrentAlgorithmVersion,
            rollingResult is not null, nameof(rollingFormulaVersion));
        ValidateFormula(priceContextFormulaVersion,
            PriceContextFeatureCalculator.CurrentAlgorithmVersion,
            priceContextResult is not null, nameof(priceContextFormulaVersion));

        FeatureQualityEvaluation expected = Evaluate(rollingResult, priceContextResult, policy);
        if (!StringComparer.Ordinal.Equals(quality.AlgorithmVersion, CurrentAlgorithmVersion)
            || quality.Cursor != cursor || quality.Policy != policy
            || quality.RollingFormulaVersion != rollingFormulaVersion
            || quality.PriceContextFormulaVersion != priceContextFormulaVersion
            || !quality.Features.Equals(expected.Features))
        {
            throw new ArgumentException(
                "The persisted feature quality does not match its calculator results.",
                nameof(quality));
        }

        FormatVersion = formatVersion;
        AlgorithmVersion = algorithmVersion;
        RollingResult = rollingResult;
        PriceContextResult = priceContextResult;
        Cursor = cursor;
        Policy = policy;
        RollingFormulaVersion = rollingFormulaVersion;
        PriceContextFormulaVersion = priceContextFormulaVersion;
        Quality = quality;
        SnapshotId = ComputeIdentity(CoreJsonSerializer.Serialize(this));
    }

    /// <summary>Recomputes quality using only the stored immutable inputs.</summary>
    public FeatureQualityEvaluation RecalculateQuality() =>
        Evaluate(RollingResult, PriceContextResult, Policy);

    [JsonIgnore]
    public FeatureQualityEvaluation RecalculatedQuality => RecalculateQuality();

    private static FeatureQualityEvaluation Evaluate(RollingIndicatorResult? rollingResult,
        PriceContextFeatureResult? priceContextResult, FeatureQualityPolicy policy)
    {
        return (rollingResult, priceContextResult) switch
        {
            ({ } rolling, { } price) => FeatureQualityEvaluator.Evaluate(rolling, price, policy),
            ({ } rolling, null) => FeatureQualityEvaluator.Evaluate(rolling, policy),
            (null, { } price) => FeatureQualityEvaluator.Evaluate(price, policy),
            _ => throw new ArgumentException("At least one calculator result is required.",
                nameof(rollingResult))
        };
    }

    private static CausalEventCursor ResolveCursor(RollingIndicatorResult? rollingResult,
        PriceContextFeatureResult? priceContextResult) => rollingResult?.Cursor
            ?? priceContextResult?.Cursor
            ?? throw new ArgumentException("At least one calculator result is required.",
                nameof(rollingResult));

    private static string? ResolveRollingFormula(RollingIndicatorResult? result) =>
        result is null ? null : result.Configuration.AlgorithmVersion?.Value
            ?? throw new ArgumentException(
                "A rolling quality snapshot requires an explicit formula version.",
                nameof(result));

    private static string? ResolvePriceContextFormula(PriceContextFeatureResult? result) =>
        result is null ? null : result.Configuration.AlgorithmVersion?.Value
            ?? throw new ArgumentException(
                "A price-context quality snapshot requires an explicit formula version.",
                nameof(result));

    private static void ValidateFormula(string? actual, string expected, bool required,
        string parameterName)
    {
        if (!required)
        {
            if (actual is not null)
            {
                throw new ArgumentException(
                    "A formula version cannot be supplied without its calculator result.",
                    parameterName);
            }

            return;
        }

        if (!StringComparer.Ordinal.Equals(actual, expected))
        {
            throw new ArgumentException(
                $"The persisted formula version must be {expected}.", parameterName);
        }
    }

    private static string ComputeIdentity(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)))
            .ToLowerInvariant();
}
