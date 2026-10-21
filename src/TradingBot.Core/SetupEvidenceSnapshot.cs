using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Canonical, versioned evidence for one Lane A setup direction. It retains the
/// complete causal price-context/quality snapshot, the selected family
/// configuration and observation, explicit invalidation evidence, and a
/// deterministic content identity. It contains no lifecycle, decision, risk,
/// order, or execution state.
/// </summary>
public sealed record SetupEvidenceSnapshot
{
    public const int CurrentFormatVersion = 1;

    public const string CurrentAlgorithmVersion = "setup-evidence-v1";

    public int FormatVersion { get; }

    public string AlgorithmVersion { get; }

    public SetupType SetupType { get; }

    public Direction Direction { get; }

    public PriceContextFeatureResult PriceContext { get; }

    public FeatureQualityEvaluation Quality { get; }

    public BoundaryRejectionSetupConfiguration? BoundaryRejectionConfiguration { get; }

    public CenterRetestSetupConfiguration? CenterRetestConfiguration { get; }

    public BoundaryRejectionSetupObservation? BoundaryRejectionObservation { get; }

    public CenterRetestSetupObservation? CenterRetestObservation { get; }

    public SetupEvidenceInputSequence InputSequence { get; }

    public SetupEvidenceInvalidation Invalidation { get; }

    /// <summary>
    /// Lower-case SHA-256 of this snapshot's canonical JSON. Identity is not part
    /// of that JSON, so equivalent evidence has the same identity.
    /// </summary>
    [JsonIgnore]
    public string SnapshotId { get; }

    [JsonIgnore]
    public string Identity => SnapshotId;

    [JsonIgnore]
    public byte[] IdentityBytes => Convert.FromHexString(SnapshotId);

    [JsonIgnore]
    public SetupEvidenceState State => Invalidation.State;

    [JsonIgnore]
    public bool IsReady => State == SetupEvidenceState.Ready;

    [JsonIgnore]
    public bool IsInvalidated => State == SetupEvidenceState.Invalidated;

    [JsonIgnore]
    public ScreenerBar? SignalBar => InputSequence.SignalBar;

    [JsonIgnore]
    public CausalEventCursor Cursor => InputSequence.Cursor;

    [JsonIgnore]
    public string FamilyAlgorithmVersion => InputSequence.FamilyAlgorithmVersion;

    /// <summary>Creates per-direction evidence from a Boundary Rejection result.</summary>
    public static SetupEvidenceSnapshot Create(BoundaryRejectionSetupResult result,
        Direction direction)
    {
        ArgumentNullException.ThrowIfNull(result);
        ContractGuard.RequireDefined(direction, nameof(direction));
        BoundaryRejectionSetupObservation observation = direction switch
        {
            Direction.Long => result.Long,
            Direction.Short => result.Short,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction,
                "The setup direction is unsupported.")
        };
        SetupEvidenceInputSequence inputSequence = BuildInputSequence(
            result.PriceContext, result.Quality, observation.SignalBar,
            BoundaryRejectionSetupResult.CurrentAlgorithmVersion);
        SetupEvidenceInvalidation invalidation = BuildInvalidation(
            MapState(observation.State), observation.IsInvalidated,
            observation.IsInvalidated && observation.Conditions.ClosedThroughBoundary,
            observation.Reasons);
        return new SetupEvidenceSnapshot(CurrentFormatVersion, CurrentAlgorithmVersion,
            SetupType.BoundaryRejection, direction, result.PriceContext, result.Quality,
            result.Configuration, null, observation, null, inputSequence, invalidation);
    }

    /// <summary>Creates per-direction evidence from a Center Retest result.</summary>
    public static SetupEvidenceSnapshot Create(CenterRetestSetupResult result,
        Direction direction)
    {
        ArgumentNullException.ThrowIfNull(result);
        ContractGuard.RequireDefined(direction, nameof(direction));
        CenterRetestSetupObservation observation = direction switch
        {
            Direction.Long => result.Long,
            Direction.Short => result.Short,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction,
                "The setup direction is unsupported.")
        };
        SetupEvidenceInputSequence inputSequence = BuildInputSequence(
            result.PriceContext, result.Quality, observation.SignalBar,
            CenterRetestSetupResult.CurrentAlgorithmVersion);
        SetupEvidenceInvalidation invalidation = BuildInvalidation(
            MapState(observation.State), observation.IsInvalidated,
            observation.IsInvalidated && observation.Conditions.ClosedThroughCenter,
            observation.Reasons);
        return new SetupEvidenceSnapshot(CurrentFormatVersion, CurrentAlgorithmVersion,
            SetupType.CenterRetest, direction, result.PriceContext, result.Quality,
            null, result.Configuration, null, observation, inputSequence, invalidation);
    }

    /// <summary>
    /// Re-evaluates the family from the retained input and verifies that the
    /// evidence identity is reproduced.
    /// </summary>
    public SetupEvidenceSnapshot Replay()
    {
        SetupEvidenceSnapshot replayed = SetupType switch
        {
            SetupType.BoundaryRejection => Create(
                BoundaryRejectionSetupEvaluator.Evaluate(PriceContext, Quality,
                    BoundaryRejectionConfiguration!), Direction),
            SetupType.CenterRetest => Create(
                CenterRetestSetupEvaluator.Evaluate(PriceContext, Quality,
                    CenterRetestConfiguration!), Direction),
            _ => throw new InvalidOperationException(
                "The setup evidence family is unsupported.")
        };
        if (!StringComparer.Ordinal.Equals(replayed.SnapshotId, SnapshotId))
        {
            throw new InvalidOperationException(
                "Setup evidence replay produced a different identity.");
        }

        return replayed;
    }

    [JsonConstructor]
    public SetupEvidenceSnapshot(int formatVersion, string algorithmVersion,
        SetupType setupType, Direction direction,
        PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality,
        BoundaryRejectionSetupConfiguration? boundaryRejectionConfiguration,
        CenterRetestSetupConfiguration? centerRetestConfiguration,
        BoundaryRejectionSetupObservation? boundaryRejectionObservation,
        CenterRetestSetupObservation? centerRetestObservation,
        SetupEvidenceInputSequence inputSequence,
        SetupEvidenceInvalidation invalidation)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The setup evidence format version is unsupported.");
        }

        if (!StringComparer.Ordinal.Equals(algorithmVersion,
                CurrentAlgorithmVersion))
        {
            throw new ArgumentException(
                "The setup evidence algorithm version is unsupported.",
                nameof(algorithmVersion));
        }

        ContractGuard.RequireDefined(setupType, nameof(setupType));
        ContractGuard.RequireDefined(direction, nameof(direction));
        ArgumentNullException.ThrowIfNull(priceContext);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(inputSequence);
        ArgumentNullException.ThrowIfNull(invalidation);
        if (priceContext.Cursor != quality.Cursor
            || priceContext.Cursor != inputSequence.Cursor)
        {
            throw new ArgumentException(
                "Price-context, quality, and evidence input must use the same cursor.",
                nameof(inputSequence));
        }

        bool hasBoundaryPayload = boundaryRejectionConfiguration is not null
            || boundaryRejectionObservation is not null;
        bool hasCenterPayload = centerRetestConfiguration is not null
            || centerRetestObservation is not null;
        if (hasBoundaryPayload == hasCenterPayload)
        {
            throw new ArgumentException(
                "Setup evidence requires exactly one family payload.",
                nameof(setupType));
        }

        switch (setupType)
        {
            case SetupType.BoundaryRejection:
                if (boundaryRejectionConfiguration is null
                    || boundaryRejectionObservation is null)
                {
                    throw new ArgumentException(
                        "Boundary evidence requires a configuration and observation.",
                        nameof(boundaryRejectionObservation));
                }

                ValidateBoundaryPayload(priceContext, quality, direction,
                    boundaryRejectionConfiguration, boundaryRejectionObservation,
                    inputSequence, invalidation);
                break;
            case SetupType.CenterRetest:
                if (centerRetestConfiguration is null
                    || centerRetestObservation is null)
                {
                    throw new ArgumentException(
                        "Center evidence requires a configuration and observation.",
                        nameof(centerRetestObservation));
                }

                ValidateCenterPayload(priceContext, quality, direction,
                    centerRetestConfiguration, centerRetestObservation,
                    inputSequence, invalidation);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setupType), setupType,
                    "The setup evidence family is unsupported.");
        }

        FormatVersion = formatVersion;
        AlgorithmVersion = algorithmVersion;
        SetupType = setupType;
        Direction = direction;
        PriceContext = priceContext;
        Quality = quality;
        BoundaryRejectionConfiguration = boundaryRejectionConfiguration;
        CenterRetestConfiguration = centerRetestConfiguration;
        BoundaryRejectionObservation = boundaryRejectionObservation;
        CenterRetestObservation = centerRetestObservation;
        InputSequence = inputSequence;
        Invalidation = invalidation;
        SnapshotId = ComputeIdentity(CoreJsonSerializer.Serialize(this));
    }

    private static void ValidateBoundaryPayload(
        PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality,
        Direction direction,
        BoundaryRejectionSetupConfiguration configuration,
        BoundaryRejectionSetupObservation observation,
        SetupEvidenceInputSequence inputSequence,
        SetupEvidenceInvalidation invalidation)
    {
        if (observation.Direction != direction)
        {
            throw new ArgumentException(
                "The boundary observation direction does not match the evidence.",
                nameof(observation));
        }

        if (configuration.MinimumChannelWidth != observation.MinimumChannelWidth)
        {
            throw new ArgumentException(
                "The boundary observation width does not match its configuration.",
                nameof(configuration));
        }

        if (!StringComparer.Ordinal.Equals(inputSequence.FamilyAlgorithmVersion,
                BoundaryRejectionSetupResult.CurrentAlgorithmVersion))
        {
            throw new ArgumentException(
                "The boundary evidence family version is unsupported.",
                nameof(inputSequence));
        }

        ValidateInputSequence(priceContext, quality, inputSequence,
            observation.SignalBar);
        SetupEvidenceInvalidation expected = BuildInvalidation(
            MapState(observation.State), observation.IsInvalidated,
            observation.IsInvalidated && observation.Conditions.ClosedThroughBoundary,
            observation.Reasons);
        if (!expected.Equals(invalidation))
        {
            throw new ArgumentException(
                "The boundary invalidation evidence does not match its observation.",
                nameof(invalidation));
        }

        BoundaryRejectionSetupResult replayed =
            BoundaryRejectionSetupEvaluator.Evaluate(priceContext, quality,
                configuration);
        BoundaryRejectionSetupObservation replayedObservation = direction switch
        {
            Direction.Long => replayed.Long,
            Direction.Short => replayed.Short,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction,
                "The setup direction is unsupported.")
        };
        if (!replayedObservation.Equals(observation))
        {
            throw new ArgumentException(
                "The boundary evidence does not match a fresh family evaluation.",
                nameof(observation));
        }
    }

    private static void ValidateCenterPayload(
        PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality,
        Direction direction, CenterRetestSetupConfiguration configuration,
        CenterRetestSetupObservation observation,
        SetupEvidenceInputSequence inputSequence,
        SetupEvidenceInvalidation invalidation)
    {
        if (observation.Direction != direction)
        {
            throw new ArgumentException(
                "The center observation direction does not match the evidence.",
                nameof(observation));
        }

        if (!StringComparer.Ordinal.Equals(inputSequence.FamilyAlgorithmVersion,
                CenterRetestSetupResult.CurrentAlgorithmVersion))
        {
            throw new ArgumentException(
                "The center evidence family version is unsupported.",
                nameof(inputSequence));
        }

        ValidateInputSequence(priceContext, quality, inputSequence,
            observation.SignalBar);
        SetupEvidenceInvalidation expected = BuildInvalidation(
            MapState(observation.State), observation.IsInvalidated,
            observation.IsInvalidated && observation.Conditions.ClosedThroughCenter,
            observation.Reasons);
        if (!expected.Equals(invalidation))
        {
            throw new ArgumentException(
                "The center invalidation evidence does not match its observation.",
                nameof(invalidation));
        }

        CenterRetestSetupResult replayed = CenterRetestSetupEvaluator.Evaluate(
            priceContext, quality, configuration);
        CenterRetestSetupObservation replayedObservation = direction switch
        {
            Direction.Long => replayed.Long,
            Direction.Short => replayed.Short,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction,
                "The setup direction is unsupported.")
        };
        if (!replayedObservation.Equals(observation))
        {
            throw new ArgumentException(
                "The center evidence does not match a fresh family evaluation.",
                nameof(observation));
        }
    }

    private static void ValidateInputSequence(
        PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality,
        SetupEvidenceInputSequence inputSequence, ScreenerBar? signalBar)
    {
        bool matches = StringComparer.Ordinal.Equals(inputSequence.SourceId,
                priceContext.Input.SourceId)
            && inputSequence.InstrumentId == priceContext.Input.InstrumentId
            && StringComparer.Ordinal.Equals(inputSequence.SetupTimeframe,
                priceContext.Input.SetupTimeframe)
            && StringComparer.Ordinal.Equals(inputSequence.HtfTimeframe,
                priceContext.Input.HtfTimeframe)
            && inputSequence.SetupHistoryStart == priceContext.Input.SetupHistoryStart
            && inputSequence.HtfHistoryStart == priceContext.Input.HtfHistoryStart
            && inputSequence.SignalBar == signalBar
            && StringComparer.Ordinal.Equals(inputSequence.PriceContextFormulaVersion,
                priceContext.Configuration.AlgorithmVersion?.Value)
            && StringComparer.Ordinal.Equals(inputSequence.QualityAlgorithmVersion,
                quality.AlgorithmVersion);
        if (!matches)
        {
            throw new ArgumentException(
                "The causal input sequence does not match the retained price-context input.",
                nameof(inputSequence));
        }
    }

    private static SetupEvidenceInputSequence BuildInputSequence(
        PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality,
        ScreenerBar? signalBar, string familyAlgorithmVersion) => new(
        priceContext.Cursor, priceContext.Input.SourceId,
        priceContext.Input.InstrumentId, priceContext.Input.SetupTimeframe,
        priceContext.Input.HtfTimeframe, priceContext.Input.SetupHistoryStart,
        priceContext.Input.HtfHistoryStart, signalBar, familyAlgorithmVersion,
        priceContext.Configuration.AlgorithmVersion?.Value,
        quality.AlgorithmVersion);

    private static SetupEvidenceInvalidation BuildInvalidation(
        SetupEvidenceState state, bool isInvalidated, bool closedThrough,
        IEnumerable<string> reasons) => new(state, isInvalidated, closedThrough,
        new ImmutableValueList<string>(reasons));

    private static SetupEvidenceState MapState(BoundaryRejectionSetupState state) =>
        state switch
        {
            BoundaryRejectionSetupState.Ready => SetupEvidenceState.Ready,
            BoundaryRejectionSetupState.NoSetup => SetupEvidenceState.NoSetup,
            BoundaryRejectionSetupState.Invalidated => SetupEvidenceState.Invalidated,
            BoundaryRejectionSetupState.UnknownConfiguration =>
                SetupEvidenceState.UnknownConfiguration,
            BoundaryRejectionSetupState.UnknownInput =>
                SetupEvidenceState.UnknownInput,
            BoundaryRejectionSetupState.UnknownWarmup =>
                SetupEvidenceState.UnknownWarmup,
            BoundaryRejectionSetupState.UnknownQuality =>
                SetupEvidenceState.UnknownQuality,
            BoundaryRejectionSetupState.UnknownOverflow =>
                SetupEvidenceState.UnknownOverflow,
            BoundaryRejectionSetupState.UnknownZeroDenominator =>
                SetupEvidenceState.UnknownZeroDenominator,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state,
                "The boundary setup state is unsupported.")
        };

    private static SetupEvidenceState MapState(CenterRetestSetupState state) =>
        state switch
        {
            CenterRetestSetupState.Ready => SetupEvidenceState.Ready,
            CenterRetestSetupState.NoSetup => SetupEvidenceState.NoSetup,
            CenterRetestSetupState.Invalidated => SetupEvidenceState.Invalidated,
            CenterRetestSetupState.UnknownConfiguration =>
                SetupEvidenceState.UnknownConfiguration,
            CenterRetestSetupState.UnknownInput =>
                SetupEvidenceState.UnknownInput,
            CenterRetestSetupState.UnknownWarmup =>
                SetupEvidenceState.UnknownWarmup,
            CenterRetestSetupState.UnknownQuality =>
                SetupEvidenceState.UnknownQuality,
            CenterRetestSetupState.UnknownOverflow =>
                SetupEvidenceState.UnknownOverflow,
            CenterRetestSetupState.UnknownZeroDenominator =>
                SetupEvidenceState.UnknownZeroDenominator,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state,
                "The center setup state is unsupported.")
        };

    private static string ComputeIdentity(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)))
            .ToLowerInvariant();
}
