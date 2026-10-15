using Xunit;

namespace TradingBot.Exchange.Binance.Tests;

public sealed class BinancePrivateProtocolEvidenceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    // Synthetic test interval only; the project freshness TTL remains UNKNOWN.
    private static readonly TimeSpan SyntheticFreshnessWindow = TimeSpan.FromMinutes(5);

    // Synthetic test tolerance only; this is not an exchange or project contract.
    private static readonly TimeSpan SyntheticClockSkewTolerance = TimeSpan.FromSeconds(2);

    [Fact]
    public void IncompleteAndSyntheticStaleEvidenceReturnSeparateReasons()
    {
        BinancePrivateProtocolEvidenceSnapshot evidence = HealthyEvidence() with
        {
            EvidenceComplete = false,
            LastEvidenceAt = Now.AddMinutes(-6)
        };

        IReadOnlyList<BinancePrivateProtocolEvidenceDiagnostic> diagnostics =
            BinancePrivateProtocolEvidenceEvaluator.Diagnose(
                evidence, Now, SyntheticFreshnessWindow, SyntheticClockSkewTolerance);

        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.IncompleteEvidence);
        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.StaleEvidence);
    }

    [Fact]
    public void UnknownStatusIsReportedWithoutAssumingAnEnum()
    {
        IReadOnlyList<BinancePrivateProtocolEvidenceDiagnostic> diagnostics =
            BinancePrivateProtocolEvidenceEvaluator.Diagnose(
                HealthyEvidence() with { UnknownStatus = true },
                Now, SyntheticFreshnessWindow, SyntheticClockSkewTolerance);

        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.UnknownStatus);
    }

    [Fact]
    public void DisconnectAndListenKeyExpiryRemainSeparateReasons()
    {
        IReadOnlyList<BinancePrivateProtocolEvidenceDiagnostic> diagnostics =
            BinancePrivateProtocolEvidenceEvaluator.Diagnose(
                HealthyEvidence() with
                {
                    UserDataConnected = false,
                    ListenKeyActive = false
                },
                Now, SyntheticFreshnessWindow, SyntheticClockSkewTolerance);

        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.UserDataDisconnected);
        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.ListenKeyExpired);
    }

    [Fact]
    public void ClockSkewRateLimitAndAuthenticationFailureAreDiagnosticReasons()
    {
        IReadOnlyList<BinancePrivateProtocolEvidenceDiagnostic> diagnostics =
            BinancePrivateProtocolEvidenceEvaluator.Diagnose(
                HealthyEvidence() with
                {
                    ClockSkewMilliseconds = 2501,
                    RateLimitObserved = true,
                    AuthenticationFailureObserved = true
                },
                Now, SyntheticFreshnessWindow, SyntheticClockSkewTolerance);

        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.ClockSkew);
        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.RateLimited);
        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.AuthenticationFailure);
    }

    [Fact]
    public void AmbiguousOutcomeRequiresReconciliationAndMissingOrderIsNotNonExecution()
    {
        IReadOnlyList<BinancePrivateProtocolEvidenceDiagnostic> diagnostics =
            BinancePrivateProtocolEvidenceEvaluator.Diagnose(
                HealthyEvidence() with
                {
                    RequestOutcomeKnown = false,
                    OrderResponseExpected = true,
                    OrderResponsePresent = false
                },
                Now, SyntheticFreshnessWindow, SyntheticClockSkewTolerance);

        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.AmbiguousRequestOutcome);
        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.ReconciliationRequired);
        Assert.Contains(diagnostics, item =>
            item.Reason == BinancePrivateProtocolEvidenceReason.OrderResponseMissing);
        Assert.DoesNotContain(diagnostics, item =>
            item.Message.Contains("not executed", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(BinancePrivateHttp503Variant.UnknownOutcome,
        BinancePrivateProtocolEvidenceReason.Http503UnknownOutcome, true)]
    [InlineData(BinancePrivateHttp503Variant.ServiceUnavailable,
        BinancePrivateProtocolEvidenceReason.Http503ServiceUnavailable, false)]
    [InlineData(BinancePrivateHttp503Variant.InternalError,
        BinancePrivateProtocolEvidenceReason.Http503InternalError, false)]
    [InlineData(BinancePrivateHttp503Variant.SystemThrottled,
        BinancePrivateProtocolEvidenceReason.Http503SystemThrottled, false)]
    public void Http503VariantsReturnSpecificReasons(
        BinancePrivateHttp503Variant variant,
        BinancePrivateProtocolEvidenceReason expectedReason,
        bool reconciliationExpected)
    {
        IReadOnlyList<BinancePrivateProtocolEvidenceDiagnostic> diagnostics =
            BinancePrivateProtocolEvidenceEvaluator.Diagnose(
                HealthyEvidence() with { Http503Variant = variant },
                Now, SyntheticFreshnessWindow, SyntheticClockSkewTolerance);

        Assert.Contains(diagnostics, item => item.Reason == expectedReason);
        Assert.Equal(reconciliationExpected,
            diagnostics.Any(item =>
                item.Reason == BinancePrivateProtocolEvidenceReason.ReconciliationRequired));
    }

    [Fact]
    public void CompleteFreshEvidenceHasNoDiagnosticReasons()
    {
        IReadOnlyList<BinancePrivateProtocolEvidenceDiagnostic> diagnostics =
            BinancePrivateProtocolEvidenceEvaluator.Diagnose(
                HealthyEvidence(), Now, SyntheticFreshnessWindow, SyntheticClockSkewTolerance);

        Assert.Empty(diagnostics);
    }

    private static BinancePrivateProtocolEvidenceSnapshot HealthyEvidence()
    {
        return new BinancePrivateProtocolEvidenceSnapshot(
            EvidenceComplete: true,
            LastEvidenceAt: Now.AddSeconds(-1),
            UserDataConnected: true,
            ListenKeyActive: true,
            ClockSkewMilliseconds: 0,
            UnknownStatus: false,
            RateLimitObserved: false,
            AuthenticationFailureObserved: false,
            RequestOutcomeKnown: true,
            OrderResponseExpected: false,
            OrderResponsePresent: true,
            Http503Variant: BinancePrivateHttp503Variant.None);
    }
}
