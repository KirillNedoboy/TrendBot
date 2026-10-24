namespace TradingBot.Core;

/// <summary>Builds a quality assessment using only records available at one causal cursor.</summary>
public static class LaneCQualityEvaluator
{
    public static LaneCQualityEvidence Evaluate(LaneCFeatureInput input,
        LaneCFeatureConfiguration configuration, LaneCFeatureResult features,
        LaneCQualityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(policy);
        if (features.Cursor != input.Cursor)
        {
            throw new ArgumentException("Feature result must use the quality input cursor.",
                nameof(features));
        }

        LaneCFeatureResult calculated = LaneCFeatureEvaluator.Evaluate(input, configuration);
        if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(features),
            CoreJsonSerializer.Serialize(calculated)))
        {
            throw new ArgumentException("Feature result does not match the supplied input.",
                nameof(features));
        }

        CaptureEnvelope[] eligible = input.Records.Where(record =>
            record.StableSequence <= input.Cursor.MaxStableSequence
            && record.EventTime.Value <= input.Cursor.AsOf.Value
            && record.AvailabilityTime.Value <= input.Cursor.AsOf.Value)
            .OrderBy(static record => record.StableSequence).ToArray();
        long gaps = 0;
        long duplicates = 0;
        long conflicts = 0;
        long missing = 0;
        long clockSkew = 0;
        long stale = 0;
        List<LaneCQualityUnknownDiagnostic> unknown = [];

        foreach (CaptureEnvelope envelope in eligible)
        {
            HashSet<string> signals = new(StringComparer.Ordinal);
            foreach (string flag in envelope.Provenance.QualityFlags)
            {
                if (!TryMap(flag, out string? signal))
                {
                    unknown.Add(Unknown(flag, envelope));
                }
                else
                {
                    signals.Add(signal);
                }
            }

            if (envelope.Diagnostic is { } diagnostic)
            {
                if (TryMap(diagnostic.Code, out string? signal))
                {
                    signals.Add(signal);
                }
                else
                {
                    unknown.Add(Unknown(diagnostic.Code, envelope));
                }
            }

            gaps = checked(gaps + (signals.Contains("gap") ? 1 : 0));
            duplicates = checked(duplicates + (signals.Contains("duplicate") ? 1 : 0));
            conflicts = checked(conflicts + (signals.Contains("conflict") ? 1 : 0));
            missing = checked(missing + (signals.Contains("missing-field") ? 1 : 0));
            clockSkew = checked(clockSkew + (signals.Contains("clock-skew") ? 1 : 0));
            stale = checked(stale + (signals.Contains("stale") ? 1 : 0));
        }

        List<string> reasons = [];
        bool hasBook = eligible.Any(static row => row.Kind == CaptureRecordKind.Depth);
        bool hasTrades = eligible.Any(static row => row.Kind == CaptureRecordKind.Trade);
        if (policy.RequireOrderBook && !hasBook)
        {
            reasons.Add("required-order-book-unavailable");
        }

        if (policy.RequireTrades && !hasTrades)
        {
            reasons.Add("required-trades-unavailable");
        }

        bool providerPolicyUnknown = policy.MaximumProviderSilence is null
            && (policy.RequireOrderBook || policy.RequireTrades);
        if (providerPolicyUnknown)
        {
            reasons.Add("provider-silence-policy-unknown");
        }
        else if (policy.MaximumProviderSilence is { } maximumSilence)
        {
            if (policy.RequireOrderBook && hasBook && IsSilent(eligible, CaptureRecordKind.Depth,
                input.Cursor.AsOf.Value, maximumSilence))
            {
                reasons.Add("order-book-provider-stale");
            }

            if (policy.RequireTrades && hasTrades && IsSilent(eligible, CaptureRecordKind.Trade,
                input.Cursor.AsOf.Value, maximumSilence))
            {
                reasons.Add("trade-provider-stale");
            }
        }

        LaneCQualityState state = features.State switch
        {
            LaneCFeatureState.Conflict => LaneCQualityState.Conflict,
            LaneCFeatureState.SequenceGap => LaneCQualityState.SequenceGap,
            LaneCFeatureState.Stale => LaneCQualityState.Stale,
            LaneCFeatureState.InsufficientData or LaneCFeatureState.WarmingUp =>
                LaneCQualityState.InsufficientData,
            _ => LaneCQualityState.Ready
        };
        if (unknown.Count > 0 || providerPolicyUnknown || reasons.Count > 0
            || features.State != LaneCFeatureState.Ready)
        {
            if (state == LaneCQualityState.Ready)
            {
                state = LaneCQualityState.InsufficientData;
            }
        }

        if (gaps > 0)
        {
            reasons.Add("historical-gap-observed");
        }
        if (duplicates > 0)
        {
            reasons.Add("duplicate-observed");
        }
        if (conflicts > 0)
        {
            reasons.Add("conflict-observed");
            if (state is LaneCQualityState.Ready or LaneCQualityState.InsufficientData)
            {
                state = LaneCQualityState.Conflict;
            }
        }
        if (state is not (LaneCQualityState.Conflict or LaneCQualityState.SequenceGap)
            && (features.State == LaneCFeatureState.Stale
                || reasons.Contains("order-book-provider-stale", StringComparer.Ordinal)
                || reasons.Contains("trade-provider-stale", StringComparer.Ordinal)))
        {
            state = LaneCQualityState.Stale;
        }
        if (missing > 0)
        {
            reasons.Add("missing-field-observed");
        }
        if (clockSkew > 0)
        {
            reasons.Add("clock-skew-observed");
        }
        if (stale > 0)
        {
            reasons.Add("stale-observed");
        }
        if (unknown.Count > 0)
        {
            reasons.Add("unknown-diagnostic-observed");
        }

        ImmutableValueList<string> reasonList = new(reasons.Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal));
        LaneCQualitySignalCounts counts = new(gaps, duplicates, conflicts, missing,
            clockSkew, stale);
        ImmutableValueList<LaneCQualityUnknownDiagnostic> unknownList = new(unknown
            .Distinct().OrderBy(static item => item.StableSequence)
            .ThenBy(static item => item.Code, StringComparer.Ordinal));
        string algorithmVersion = LaneCQualityEvidence.CurrentAlgorithmVersion;
        string identity = LaneCQualityEvidence.ComputeIdentity(algorithmVersion,
            input.InstrumentId, features.InputIdentity, input.Cursor, state, reasonList,
            counts, unknownList);
        return new LaneCQualityEvidence(algorithmVersion, identity, input.InstrumentId,
            features.InputIdentity, input.Cursor, state, reasonList, counts, unknownList);
    }

    private static bool IsSilent(IEnumerable<CaptureEnvelope> records, CaptureRecordKind kind,
        DateTimeOffset asOf, TimeSpan maximumSilence)
    {
        CaptureEnvelope? latest = records.Where(row => row.Kind == kind)
            .MaxBy(static row => row.AvailabilityTime.Value);
        return latest is null || asOf - latest.AvailabilityTime.Value > maximumSilence;
    }

    private static LaneCQualityUnknownDiagnostic Unknown(string code,
        CaptureEnvelope envelope) => new(SafeToken(code), SafeToken(envelope.Provenance.Source),
            envelope.Provenance.StreamName is null ? null :
                SafeToken(envelope.Provenance.StreamName), envelope.StableSequence,
            envelope.EventTime, envelope.AvailabilityTime);

    private static string SafeToken(string value)
    {
        string sanitized = new(value.Where(static character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':').Take(128).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "redacted" : sanitized;
    }

    private static bool TryMap(string value, out string signal)
    {
        string normalized = new(value.Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToLowerInvariant).ToArray());
        signal = normalized.Contains("gap", StringComparison.Ordinal)
            || normalized.Contains("disconnect", StringComparison.Ordinal)
                ? "gap"
            : normalized.Contains("duplicate", StringComparison.Ordinal)
                ? "duplicate"
                : normalized.Contains("conflict", StringComparison.Ordinal)
                    ? "conflict"
                    : normalized.Contains("missingfield", StringComparison.Ordinal)
                        ? "missing-field"
                        : normalized.Contains("clockskew", StringComparison.Ordinal)
                            ? "clock-skew"
                            : normalized.Contains("stale", StringComparison.Ordinal)
                                ? "stale" : string.Empty;
        return signal.Length > 0;
    }
}
