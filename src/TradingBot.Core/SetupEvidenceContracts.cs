using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Per-cursor Lane A family evaluation state carried by setup evidence. This is
/// deliberately separate from the Phase 8 lifecycle <see cref="SetupState"/>.
/// </summary>
public enum SetupEvidenceState
{
    Ready,
    NoSetup,
    Invalidated,
    UnknownConfiguration,
    UnknownInput,
    UnknownWarmup,
    UnknownQuality,
    UnknownOverflow,
    UnknownZeroDenominator
}

/// <summary>
/// Compact causal provenance for one setup-evidence direction. The complete
/// price-context and quality input remain in <see cref="SetupEvidenceSnapshot"/>.
/// </summary>
public sealed record SetupEvidenceInputSequence
{
    public CausalEventCursor Cursor { get; }

    public string SourceId { get; }

    public InstrumentId InstrumentId { get; }

    public string SetupTimeframe { get; }

    public string HtfTimeframe { get; }

    public UtcTimestamp SetupHistoryStart { get; }

    public UtcTimestamp HtfHistoryStart { get; }

    public ScreenerBar? SignalBar { get; }

    public string FamilyAlgorithmVersion { get; }

    public string? PriceContextFormulaVersion { get; }

    public string QualityAlgorithmVersion { get; }

    [JsonIgnore]
    public long? StableSequence => SignalBar?.StableSequence;

    [JsonIgnore]
    public UtcTimestamp? EventTime => SignalBar?.EventTime;

    [JsonIgnore]
    public UtcTimestamp? ReceiveTime => SignalBar?.ReceiveTime;

    [JsonIgnore]
    public UtcTimestamp? AvailabilityTime => SignalBar?.AvailabilityTime;

    [JsonIgnore]
    public UtcTimestamp? CloseTime => SignalBar?.CloseTime;

    [JsonConstructor]
    public SetupEvidenceInputSequence(CausalEventCursor cursor, string sourceId,
        InstrumentId instrumentId, string setupTimeframe, string htfTimeframe,
        UtcTimestamp setupHistoryStart, UtcTimestamp htfHistoryStart,
        ScreenerBar? signalBar, string familyAlgorithmVersion,
        string? priceContextFormulaVersion, string qualityAlgorithmVersion)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireText(setupTimeframe, nameof(setupTimeframe));
        ContractGuard.RequireText(htfTimeframe, nameof(htfTimeframe));
        ArgumentNullException.ThrowIfNull(setupHistoryStart);
        ArgumentNullException.ThrowIfNull(htfHistoryStart);
        ContractGuard.RequireText(familyAlgorithmVersion,
            nameof(familyAlgorithmVersion));
        if (priceContextFormulaVersion is not null)
        {
            ContractGuard.RequireText(priceContextFormulaVersion,
                nameof(priceContextFormulaVersion));
        }

        ContractGuard.RequireText(qualityAlgorithmVersion,
            nameof(qualityAlgorithmVersion));
        if (signalBar is not null
            && (!StringComparer.Ordinal.Equals(signalBar.SourceId, sourceId)
                || signalBar.InstrumentId != instrumentId))
        {
            throw new ArgumentException(
                "The signal bar must use the evidence source and instrument.",
                nameof(signalBar));
        }

        Cursor = cursor;
        SourceId = sourceId;
        InstrumentId = instrumentId;
        SetupTimeframe = setupTimeframe;
        HtfTimeframe = htfTimeframe;
        SetupHistoryStart = setupHistoryStart;
        HtfHistoryStart = htfHistoryStart;
        SignalBar = signalBar;
        FamilyAlgorithmVersion = familyAlgorithmVersion;
        PriceContextFormulaVersion = priceContextFormulaVersion;
        QualityAlgorithmVersion = qualityAlgorithmVersion;
    }
}

/// <summary>
/// Explicit invalidation evidence for one setup direction. The family
/// observation keeps the literal conditions; this record states whether the
/// evidence is authoritatively invalidated and why.
/// </summary>
public sealed record SetupEvidenceInvalidation
{
    public SetupEvidenceState State { get; }

    public bool IsInvalidated { get; }

    public bool ClosedThrough { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonConstructor]
    public SetupEvidenceInvalidation(SetupEvidenceState state, bool isInvalidated,
        bool closedThrough, ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(state, nameof(state));
        ArgumentNullException.ThrowIfNull(reasons);
        bool expectedInvalidated = state == SetupEvidenceState.Invalidated;
        if (isInvalidated != expectedInvalidated)
        {
            throw new ArgumentException(
                "IsInvalidated must match the evidence state.",
                nameof(isInvalidated));
        }

        if (closedThrough != isInvalidated)
        {
            throw new ArgumentException(
                "ClosedThrough is only valid for invalidated evidence.",
                nameof(closedThrough));
        }

        if (isInvalidated && reasons.Count == 0)
        {
            throw new ArgumentException(
                "Invalidated evidence requires at least one reason.",
                nameof(reasons));
        }

        State = state;
        IsInvalidated = isInvalidated;
        ClosedThrough = closedThrough;
        Reasons = new ImmutableValueList<string>(StableReasons(reasons));
    }

    private static IEnumerable<string> StableReasons(IEnumerable<string> reasons) =>
        reasons.Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal);
}
