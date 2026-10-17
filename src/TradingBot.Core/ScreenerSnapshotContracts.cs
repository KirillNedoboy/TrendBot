using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Immutable, versioned output of one minimum screener calculation. The
/// snapshot carries the complete calculation configuration, causal cursor,
/// normalized input observations, and feature quality metadata required to
/// reproduce the result without a clock or another data source.
/// </summary>
public sealed record ScreenerSnapshot
{
    public const int CurrentFormatVersion = 1;

    public const string CurrentFormulaVersionValue = "screener-v1";

    public static ScreenerFeatureVersion CurrentFormulaVersion { get; } =
        new(CurrentFormulaVersionValue);

    public int FormatVersion { get; }

    public ScreenerFeatureVersion FormulaVersion { get; }

    public ScreenerFeatureConfiguration Configuration { get; }

    public CausalEventCursor Cursor { get; }

    public ScreenerFeatureInput Input { get; }

    public ImmutableValueList<ScreenerFeatureValue> Features { get; }

    /// <summary>
    /// Gets the lower-case hexadecimal SHA-256 identity of the canonical
    /// snapshot JSON. The identity is intentionally excluded from that JSON.
    /// </summary>
    [JsonIgnore]
    public string SnapshotId { get; }

    /// <summary>Alias for the immutable snapshot identity.</summary>
    [JsonIgnore]
    public string Identity => SnapshotId;

    /// <summary>Gets a fresh binary identity suitable for a BINARY SQLite key.</summary>
    [JsonIgnore]
    public byte[] IdentityBytes => Convert.FromHexString(SnapshotId);

    /// <summary>Reconstructs the feature result carried by this snapshot.</summary>
    [JsonIgnore]
    public ScreenerFeatureResult Result =>
        new(FormulaVersion, Input, Cursor, Features);

    /// <summary>Creates a current-format snapshot from a feature result.</summary>
    public static ScreenerSnapshot Create(ScreenerFeatureResult result,
        ScreenerFeatureConfiguration configuration) => new(result, configuration);

    /// <summary>Alias for <see cref="Create"/> used by persistence callers.</summary>
    public static ScreenerSnapshot FromResult(ScreenerFeatureResult result,
        ScreenerFeatureConfiguration configuration) => new(result, configuration);

    /// <summary>Creates a current-format snapshot from a feature result.</summary>
    public ScreenerSnapshot(ScreenerFeatureResult result,
        ScreenerFeatureConfiguration configuration)
        : this(CurrentFormatVersion, ResolveFormulaVersion(result, configuration),
            configuration, result?.Cursor ?? throw new ArgumentNullException(nameof(result)),
            result.Input, result.Features)
    {
    }

    /// <summary>Creates a current-format snapshot from explicit contract values.</summary>
    public ScreenerSnapshot(ScreenerFeatureVersion formulaVersion,
        ScreenerFeatureConfiguration configuration, CausalEventCursor cursor,
        ScreenerFeatureInput input, IEnumerable<ScreenerFeatureValue> features)
        : this(CurrentFormatVersion, formulaVersion, configuration, cursor, input, features)
    {
    }

    /// <summary>Deserializes and validates a versioned snapshot payload.</summary>
    [JsonConstructor]
    public ScreenerSnapshot(int formatVersion, ScreenerFeatureVersion formulaVersion,
        ScreenerFeatureConfiguration configuration, CausalEventCursor cursor,
        ScreenerFeatureInput input, ImmutableValueList<ScreenerFeatureValue> features)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                $"Screener snapshot format version {formatVersion} is not supported.");
        }

        ArgumentNullException.ThrowIfNull(formulaVersion);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(features);
        ValidateFormulaVersion(formulaVersion);
        if (configuration.Version is null
            || !StringComparer.Ordinal.Equals(configuration.Version.Value,
                formulaVersion.Value))
        {
            throw new ArgumentException(
                "The snapshot formula version must match the configured formula version.",
                nameof(configuration));
        }

        ScreenerFeatureInput normalizedInput = NormalizeInput(input);
        ImmutableValueList<ScreenerFeatureValue> normalizedFeatures =
            NormalizeFeatures(features);
        _ = new ScreenerFeatureResult(formulaVersion, normalizedInput, cursor,
            normalizedFeatures);

        FormatVersion = formatVersion;
        FormulaVersion = formulaVersion;
        Configuration = configuration;
        Cursor = cursor;
        Input = normalizedInput;
        Features = normalizedFeatures;
        SnapshotId = ComputeIdentity(CoreJsonSerializer.Serialize(this));
    }

    /// <summary>Creates a snapshot from an enumerable feature collection.</summary>
    public ScreenerSnapshot(int formatVersion, ScreenerFeatureVersion formulaVersion,
        ScreenerFeatureConfiguration configuration, CausalEventCursor cursor,
        ScreenerFeatureInput input, IEnumerable<ScreenerFeatureValue> features)
        : this(formatVersion, formulaVersion, configuration, cursor, input,
            SnapshotFeatures(features))
    {
    }

    private static ScreenerFeatureVersion ResolveFormulaVersion(
        ScreenerFeatureResult? result, ScreenerFeatureConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(configuration);
        ScreenerFeatureVersion? configured = configuration.Version;
        if (configured is null)
        {
            throw new ArgumentException(
                "A screener snapshot requires an explicit configured formula version.",
                nameof(configuration));
        }

        if (result.Version is not null
            && !StringComparer.Ordinal.Equals(result.Version.Value, configured.Value))
        {
            throw new ArgumentException(
                "The feature result and configuration formula versions must match.",
                nameof(result));
        }

        return configured;
    }

    private static ImmutableValueList<ScreenerFeatureValue> SnapshotFeatures(
        IEnumerable<ScreenerFeatureValue> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        return new ImmutableValueList<ScreenerFeatureValue>(features);
    }

    private static ScreenerFeatureInput NormalizeInput(ScreenerFeatureInput input)
    {
        return new ScreenerFeatureInput(input.SourceId, input.InstrumentId, input.EventTime,
            input.ReceiveTime, input.AvailabilityTime, input.StableSequence,
            input.ClosedBars
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(static item => item.InstrumentId.Value, StringComparer.Ordinal)
                .ThenBy(static item => item.EventTime.Value)
                .ThenBy(static item => item.ReceiveTime.Value)
                .ThenBy(static item => item.AvailabilityTime.Value)
                .ThenBy(static item => item.StableSequence)
                .ThenBy(static item => item.Bar.EventId, StringComparer.Ordinal)
                .ThenBy(static item => item.Bar.OpenTime.Value)
                .ThenBy(static item => item.Bar.CloseTime.Value)
                .ThenBy(static item => item.Bar.Open.Value)
                .ThenBy(static item => item.Bar.High.Value)
                .ThenBy(static item => item.Bar.Low.Value)
                .ThenBy(static item => item.Bar.Close.Value)
                .ThenBy(static item => item.Bar.Volume.Value)
                .ThenBy(static item => item.QuoteVolume)
                .ThenBy(static item => item.IsClosed),
            input.OpenInterest
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(static item => item.InstrumentId.Value, StringComparer.Ordinal)
                .ThenBy(static item => item.EventTime.Value)
                .ThenBy(static item => item.ReceiveTime.Value)
                .ThenBy(static item => item.AvailabilityTime.Value)
                .ThenBy(static item => item.StableSequence)
                .ThenBy(static item => item.Value),
            input.Quotes
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(static item => item.InstrumentId.Value, StringComparer.Ordinal)
                .ThenBy(static item => item.EventTime.Value)
                .ThenBy(static item => item.ReceiveTime.Value)
                .ThenBy(static item => item.AvailabilityTime.Value)
                .ThenBy(static item => item.StableSequence)
                .ThenBy(static item => item.Bid is null ? decimal.MinValue : item.Bid.Value)
                .ThenBy(static item => item.Ask is null ? decimal.MinValue : item.Ask.Value)
                .ThenBy(static item => item.IsReady));
    }

    private static ImmutableValueList<ScreenerFeatureValue> NormalizeFeatures(
        IEnumerable<ScreenerFeatureValue> features)
    {
        ScreenerFeatureValue[] snapshot = features.ToArray();
        if (snapshot.Any(static feature => feature is null))
        {
            throw new ArgumentException("Screener snapshot features cannot contain null values.",
                nameof(features));
        }

        return new ImmutableValueList<ScreenerFeatureValue>(snapshot
            .OrderBy(static feature => feature.Kind)
            .Select(NormalizeFeature));
    }

    private static ScreenerFeatureValue NormalizeFeature(ScreenerFeatureValue feature)
    {
        return new ScreenerFeatureValue(feature.Kind, feature.State, feature.Value,
            feature.Reason, feature.Completeness, feature.Freshness, feature.Age,
            feature.LatestAvailabilityTime,
            feature.Reasons.OrderBy(static reason => reason, StringComparer.Ordinal));
    }

    private static void ValidateFormulaVersion(ScreenerFeatureVersion version)
    {
        if (!StringComparer.Ordinal.Equals(version.Value, CurrentFormulaVersionValue))
        {
            throw new ArgumentOutOfRangeException(nameof(version), version.Value,
                $"Screener formula version {version.Value} is not supported.");
        }
    }

    private static string ComputeIdentity(string canonicalJson)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
