using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Backtesting;

public enum SignalObservationClock { Decision, ChannelPublished }
public enum SignalPassage { TargetFirst, InvalidationFirst, Ambiguous, Neither, Unknown }
public enum SignalObservationState { Complete, Censored, MissingPath, UnknownPublication }

/// <summary>A closed, available OHLC interval; detailed points, if supplied, must cover the entire bar in time order.</summary>
public sealed record SignalOutcomeBar(DateTimeOffset Open, DateTimeOffset Close,
    DateTimeOffset AvailableAt, decimal OpenPrice, decimal High, decimal Low,
    decimal ClosePrice, IReadOnlyList<SignalOutcomePoint>? DetailedPath = null);

public sealed record SignalOutcomePoint(DateTimeOffset At, decimal Price);

/// <summary>Frozen, non-execution observation rules. Horizon and expiry are absolute UTC times.</summary>
public sealed record SignalOutcomePolicy(string Version, DateTimeOffset Horizon,
    DateTimeOffset? ExpiresAt, DateTimeOffset? InvalidatedAt,
    decimal? TargetPrice, decimal? InvalidationPrice);

/// <summary>Canonical, content-addressed observation; no fill or trading result is implied.</summary>
public sealed record SignalObservedOutcome(
    string Identity, string CanonicalHash, string SignalId, string SignalRecordHash,
    string EvidenceRecordHash, SignalObservationClock Clock, string? Channel,
    DateTimeOffset? Boundary, string MethodVersion, DateTimeOffset Horizon,
    DateTimeOffset EffectiveEnd, decimal? ObservationReferencePrice,
    DateTimeOffset? ObservationReferenceAt, SignalObservationState State,
    string? Reason, decimal? SignedReturn, decimal? Mae, decimal? Mfe,
    SignalPassage Passage, DateTimeOffset? FirstPassageAt,
    DateTimeOffset? FirstPassageEarliest, DateTimeOffset? FirstPassageLatest,
    int CoveredBars, string CanonicalJson);

public static class SignalOutcomeObserver
{
    public const string MethodVersion = "signal-outcome-observer-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SignalObservedOutcome Observe(SignalRecord signal,
        SignalEvidenceRecord evidence, SignalObservationClock clock, string? channel,
        DateTimeOffset? publishedAt, SignalOutcomePolicy policy,
        IEnumerable<SignalOutcomeBar> bars, DateTimeOffset availableThrough)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(bars);
        signal.ValidateEvidence(evidence);
        if (!Enum.IsDefined(clock) || string.IsNullOrWhiteSpace(policy.Version))
            throw new ArgumentException("An observation clock and policy version are required.");
        if (clock == SignalObservationClock.Decision && (channel is not null || publishedAt is not null)
            || clock == SignalObservationClock.ChannelPublished && string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Publication requires a named channel; decision time has no channel.");

        DateTimeOffset decisionAt = signal.DecisionAt.Value;
        DateTimeOffset? boundary = clock == SignalObservationClock.Decision
            ? decisionAt : publishedAt?.ToUniversalTime();
        if (boundary < decisionAt || policy.Horizon <= decisionAt
            || policy.ExpiresAt < decisionAt || policy.InvalidatedAt < decisionAt)
            throw new ArgumentException("Observation times cannot precede the decision or invert the horizon.");
        decimal reference = signal.ReferencePrice.Price.Value;
        if (policy.TargetPrice is <= 0 || policy.InvalidationPrice is <= 0
            || policy.TargetPrice is not null && policy.InvalidationPrice is not null
                && (signal.Identity.Direction == Direction.Long
                    ? policy.TargetPrice <= reference || policy.InvalidationPrice >= reference
                    : policy.TargetPrice >= reference || policy.InvalidationPrice <= reference))
            throw new ArgumentException("Target and invalidation must straddle the reference in the declared direction.");

        DateTimeOffset end = policy.Horizon.ToUniversalTime();
        if (policy.ExpiresAt is { } expiry && expiry < end) end = expiry.ToUniversalTime();
        if (policy.InvalidatedAt is { } invalidated && invalidated < end) end = invalidated.ToUniversalTime();
        var identityMaterial = new
        {
            signalId = signal.Identity.SignalId,
            signalRecordHash = signal.RecordHash,
            evidenceRecordHash = evidence.RecordHash,
            clock,
            channel,
            boundary,
            policy.Version,
            policy.Horizon,
            policy.ExpiresAt,
            policy.InvalidatedAt,
            policy.TargetPrice,
            policy.InvalidationPrice
        };
        string identity = Hash(JsonSerializer.Serialize(identityMaterial, JsonOptions));
        SignalObservationState state = boundary is null
            ? SignalObservationState.UnknownPublication : SignalObservationState.Complete;
        string? reason = boundary is null ? "publication-unobserved" : null;
        decimal? signedReturn = null, mae = null, mfe = null;
        SignalPassage passage = boundary is null ? SignalPassage.Unknown : SignalPassage.Neither;
        DateTimeOffset? firstPassageAt = null, firstPassageEarliest = null,
            firstPassageLatest = null;
        int covered = 0;
        decimal? observationReference = clock == SignalObservationClock.Decision
            ? reference : null;
        DateTimeOffset? observationReferenceAt = clock == SignalObservationClock.Decision
            ? decisionAt : null;
        SignalOutcomeBar[] orderedBars = bars.OrderBy(static b => b.Open).ToArray();
        string pathHash = Hash(JsonSerializer.Serialize(orderedBars, JsonOptions));
        if (boundary is not null)
        {
            DateTimeOffset cursor = boundary.Value;
            bool ambiguous = false;
            DateTimeOffset? previousClose = null;
            foreach (SignalOutcomeBar bar in orderedBars)
            {
                ValidateBar(bar);
                if (previousClose > bar.Open)
                    throw new ArgumentException("Overlapping outcome bars are not a usable path.", nameof(bars));
                previousClose = bar.Close;
                if (bar.Close <= boundary) continue;
                if (bar.Open < boundary)
                {
                    cursor = bar.Close;
                    continue;
                }
                if (bar.Open >= end) break;
                if (bar.Open > cursor)
                {
                    state = SignalObservationState.Censored;
                    reason = "path-gap";
                    break;
                }
                if (bar.Close > end || bar.AvailableAt > availableThrough)
                {
                    state = SignalObservationState.Censored;
                    reason = "incomplete-or-unavailable-bar";
                    break;
                }
                if (observationReference is null)
                {
                    observationReference = bar.OpenPrice;
                    observationReferenceAt = bar.Open;
                }
                covered++;
                cursor = bar.Close;
                decimal direction = signal.Identity.Direction == Direction.Long ? 1m : -1m;
                decimal basis = observationReference.Value;
                decimal adverse = direction * (bar.Low / basis - 1m);
                decimal favorable = direction * (bar.High / basis - 1m);
                if (direction < 0) (adverse, favorable) = (favorable, adverse);
                mae = mae is null ? adverse : Math.Min(mae.Value, adverse);
                mfe = mfe is null ? favorable : Math.Max(mfe.Value, favorable);
                signedReturn = direction * (bar.ClosePrice / basis - 1m);
                if (passage == SignalPassage.Neither)
                {
                    SignalPassage hit = PassageFor(bar, signal.Identity.Direction, policy);
                    if (hit == SignalPassage.Ambiguous) ambiguous = true;
                    if (hit != SignalPassage.Neither)
                    {
                        passage = hit;
                        if (bar.DetailedPath is not null
                            && hit is SignalPassage.TargetFirst or SignalPassage.InvalidationFirst)
                            firstPassageAt = FindFirstHit(bar, signal.Identity.Direction, policy);
                        firstPassageEarliest = firstPassageAt ?? bar.Open;
                        firstPassageLatest = firstPassageAt ?? bar.Close;
                    }
                }
            }
            if (covered == 0 && state == SignalObservationState.Complete)
            {
                state = SignalObservationState.MissingPath;
                reason = "no-eligible-subsequent-bar";
            }
            else if (state == SignalObservationState.Complete && cursor < end)
            {
                state = SignalObservationState.Censored;
                reason = "path-ends-before-horizon";
            }
            if (ambiguous && passage == SignalPassage.Ambiguous) reason ??= "same-bar-first-passage-bounds";
            if (state != SignalObservationState.Complete)
            {
                signedReturn = null;
                if (passage == SignalPassage.Neither) passage = SignalPassage.Unknown;
            }
        }
        var material = new
        {
            identity,
            identityMaterial,
            signalId = signal.Identity.SignalId,
            signalRecordHash = signal.RecordHash,
            evidenceRecordHash = evidence.RecordHash,
            clock,
            channel,
            boundary,
            methodVersion = MethodVersion,
            horizon = policy.Horizon,
            effectiveEnd = end,
            pathHash,
            observationReferencePrice = observationReference,
            observationReferenceAt,
            state,
            reason,
            signedReturn,
            mae,
            mfe,
            passage,
            firstPassageAt,
            firstPassageEarliest,
            firstPassageLatest,
            coveredBars = covered
        };
        string json = JsonSerializer.Serialize(material, JsonOptions);
        return new SignalObservedOutcome(identity, Hash(json), signal.Identity.SignalId,
            signal.RecordHash, evidence.RecordHash, clock, channel, boundary,
            MethodVersion, policy.Horizon, end, observationReference,
            observationReferenceAt, state, reason, signedReturn, mae,
            mfe, passage, firstPassageAt, firstPassageEarliest, firstPassageLatest,
            covered, json);
    }

    private static void ValidateBar(SignalOutcomeBar bar)
    {
        if (bar.Open.Offset != TimeSpan.Zero || bar.Close.Offset != TimeSpan.Zero
            || bar.AvailableAt.Offset != TimeSpan.Zero || bar.Close <= bar.Open
            || bar.AvailableAt < bar.Close || bar.OpenPrice <= 0 || bar.Low <= 0
            || bar.High < bar.Low || bar.OpenPrice < bar.Low || bar.OpenPrice > bar.High
            || bar.ClosePrice < bar.Low || bar.ClosePrice > bar.High)
            throw new ArgumentException("Invalid closed UTC outcome bar.");
        if (bar.DetailedPath is not { } path) return;
        if (path.Count < 2 || path[0].At != bar.Open || path[0].Price != bar.OpenPrice
            || path[^1].At != bar.Close || path[^1].Price != bar.ClosePrice
            || path.Any(point => point.Price < bar.Low || point.Price > bar.High)
            || path.Zip(path.Skip(1)).Any(pair => pair.First.At >= pair.Second.At)
            || path.Min(point => point.Price) != bar.Low
            || path.Max(point => point.Price) != bar.High)
            throw new ArgumentException("Detailed path does not cover the entire bar and its extrema.");
    }

    private static SignalPassage PassageFor(SignalOutcomeBar bar, Direction direction,
        SignalOutcomePolicy policy)
    {
        bool target = policy.TargetPrice is { } tp && (direction == Direction.Long
            ? bar.High >= tp : bar.Low <= tp);
        bool invalid = policy.InvalidationPrice is { } stop && (direction == Direction.Long
            ? bar.Low <= stop : bar.High >= stop);
        if (!target && !invalid) return SignalPassage.Neither;
        if (bar.DetailedPath is null)
            return target && invalid ? SignalPassage.Ambiguous
                : target ? SignalPassage.TargetFirst : SignalPassage.InvalidationFirst;
        foreach (SignalOutcomePoint point in bar.DetailedPath)
        {
            bool t = target && (direction == Direction.Long
                ? point.Price >= policy.TargetPrice : point.Price <= policy.TargetPrice);
            bool i = invalid && (direction == Direction.Long
                ? point.Price <= policy.InvalidationPrice : point.Price >= policy.InvalidationPrice);
            if (t || i) return t && i ? SignalPassage.Ambiguous
                : t ? SignalPassage.TargetFirst : SignalPassage.InvalidationFirst;
        }
        return SignalPassage.Unknown;
    }

    private static DateTimeOffset? FindFirstHit(SignalOutcomeBar bar, Direction direction,
        SignalOutcomePolicy policy) => bar.DetailedPath?.First(point =>
            policy.TargetPrice is { } tp && (direction == Direction.Long
                ? point.Price >= tp : point.Price <= tp)
            || policy.InvalidationPrice is { } stop && (direction == Direction.Long
                ? point.Price <= stop : point.Price >= stop)).At;

    internal static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
