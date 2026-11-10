namespace TradingBot.Core;

/// <summary>Consumer-facing state for one passive Lane A family/direction observation.</summary>
public enum LaneASetupObservationStatus
{
    Confirmed,
    NoSetup,
    Unknown,
    Invalidated
}
/// <summary>Three-state result for overlap between same-direction family observations.</summary>
public enum LaneASetupOverlapStatus
{
    Confirmed,
    NoOverlap,
    Unknown
}

/// <summary>
/// Same-cut view of the existing Boundary Rejection and Center Retest results.
/// It preserves both evaluator results and never resolves an overlap by choosing
/// one family.
/// </summary>
public readonly record struct LaneASetupObservationProjection
{
    /// <summary>The original SETUP-01 result, including its exact evidence and reasons.</summary>
    public BoundaryRejectionSetupResult BoundaryRejection { get; }

    /// <summary>The original SETUP-02 result, including its exact evidence and reasons.</summary>
    public CenterRetestSetupResult CenterRetest { get; }

    /// <summary>The shared causal cut for both family observations.</summary>
    public CausalEventCursor Cursor => BoundaryRejection.Cursor;

    /// <summary>Normalized SETUP-01 LONG state at this cut.</summary>
    public LaneASetupObservationStatus BoundaryLongStatus => Normalize(BoundaryRejection.Long.State);

    /// <summary>Normalized SETUP-01 SHORT state at this cut.</summary>
    public LaneASetupObservationStatus BoundaryShortStatus => Normalize(BoundaryRejection.Short.State);

    /// <summary>Normalized SETUP-02 LONG state at this cut.</summary>
    public LaneASetupObservationStatus CenterRetestLongStatus => Normalize(CenterRetest.Long.State);

    /// <summary>Normalized SETUP-02 SHORT state at this cut.</summary>
    public LaneASetupObservationStatus CenterRetestShortStatus => Normalize(CenterRetest.Short.State);

    /// <summary>Three-state LONG overlap; unknown evidence is not reported as no overlap.</summary>
    public LaneASetupOverlapStatus LongOverlapStatus =>
        Combine(BoundaryLongStatus, CenterRetestLongStatus);

    /// <summary>Three-state SHORT overlap; unknown evidence is not reported as no overlap.</summary>
    public LaneASetupOverlapStatus ShortOverlapStatus =>
        Combine(BoundaryShortStatus, CenterRetestShortStatus);

    /// <summary>True only when both families independently confirm LONG at this cut.</summary>
    public bool HasLongOverlap => LongOverlapStatus == LaneASetupOverlapStatus.Confirmed;

    /// <summary>True only when both families independently confirm SHORT at this cut.</summary>
    public bool HasShortOverlap => ShortOverlapStatus == LaneASetupOverlapStatus.Confirmed;

    /// <summary>False when any family/direction has unknown required evidence.</summary>
    public bool IsComplete => BoundaryLongStatus != LaneASetupObservationStatus.Unknown
        && BoundaryShortStatus != LaneASetupObservationStatus.Unknown
        && CenterRetestLongStatus != LaneASetupObservationStatus.Unknown
        && CenterRetestShortStatus != LaneASetupObservationStatus.Unknown;

    /// <summary>
    /// Projects two existing evaluator results from the same exact context and
    /// quality snapshots into four explicit family/direction statuses.
    /// </summary>
    public LaneASetupObservationProjection(
        BoundaryRejectionSetupResult boundaryRejection,
        CenterRetestSetupResult centerRetest)
    {
        ArgumentNullException.ThrowIfNull(boundaryRejection);
        ArgumentNullException.ThrowIfNull(centerRetest);
        if (!object.ReferenceEquals(boundaryRejection.PriceContext,
                centerRetest.PriceContext)
            || !object.ReferenceEquals(boundaryRejection.Quality,
                centerRetest.Quality))
        {
            throw new ArgumentException(
                "Both setup families must use the same price-context and quality snapshots.",
                nameof(centerRetest));
        }

        BoundaryRejection = boundaryRejection;
        CenterRetest = centerRetest;
    }

    private static LaneASetupOverlapStatus Combine(
        LaneASetupObservationStatus first, LaneASetupObservationStatus second)
    {
        if (first == LaneASetupObservationStatus.Confirmed
            && second == LaneASetupObservationStatus.Confirmed)
        {
            return LaneASetupOverlapStatus.Confirmed;
        }

        if ((first == LaneASetupObservationStatus.Unknown
                && second is LaneASetupObservationStatus.Confirmed
                    or LaneASetupObservationStatus.Unknown)
            || (second == LaneASetupObservationStatus.Unknown
                && first == LaneASetupObservationStatus.Confirmed))
        {
            return LaneASetupOverlapStatus.Unknown;
        }

        return LaneASetupOverlapStatus.NoOverlap;
    }

    private static LaneASetupObservationStatus Normalize(
        BoundaryRejectionSetupState state) => state switch
        {
            BoundaryRejectionSetupState.Ready => LaneASetupObservationStatus.Confirmed,
            BoundaryRejectionSetupState.NoSetup => LaneASetupObservationStatus.NoSetup,
            BoundaryRejectionSetupState.Invalidated => LaneASetupObservationStatus.Invalidated,
            _ => LaneASetupObservationStatus.Unknown
        };

    private static LaneASetupObservationStatus Normalize(
        CenterRetestSetupState state) => state switch
        {
            CenterRetestSetupState.Ready => LaneASetupObservationStatus.Confirmed,
            CenterRetestSetupState.NoSetup => LaneASetupObservationStatus.NoSetup,
            CenterRetestSetupState.Invalidated => LaneASetupObservationStatus.Invalidated,
            _ => LaneASetupObservationStatus.Unknown
        };
}
