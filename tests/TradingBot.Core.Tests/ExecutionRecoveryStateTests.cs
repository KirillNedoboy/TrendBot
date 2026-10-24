using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class ExecutionRecoveryStateTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void FreshCompleteNoOrderEvidenceRebindsUnknownForExplicitRetry()
    {
        OrderIntent intent = CreateIntent("intent-recover", "client-recover");
        ExecutionStateSnapshot state = ExecutionStateMachine.Claim(
            ExecutionStateMachine.Start(intent), 1);
        state = ExecutionStateMachine.Apply(state,
            ExecutionObservation.Unknown("receipt-unknown", intent.IntentId)).State;

        ExecutionStateSnapshot recovered = ExecutionStateMachine.RecoverForRetry(state,
            ExecutionRecoveryEvidence.FreshNoOrder(intent.IntentId, 4));

        Assert.Equal(ExecutionLifecycleState.IntentPersisted, recovered.State);
        Assert.False(recovered.RequiresReconciliation);
        Assert.Equal(4, recovered.ExpectedReconciliationRevision);
        Assert.Equal(1, recovered.DispatchAttempt);
    }

    [Fact]
    public void LimitedHistoryCannotAuthorizeRetry()
    {
        OrderIntent intent = CreateIntent("intent-limited", "client-limited");
        ExecutionStateSnapshot state = ExecutionStateMachine.Claim(
            ExecutionStateMachine.Start(intent), 1);
        state = ExecutionStateMachine.Apply(state,
            ExecutionObservation.Unknown("receipt-unknown", intent.IntentId)).State;

        Assert.Throws<InvalidOperationException>(() => ExecutionStateMachine.RecoverForRetry(state,
            ExecutionRecoveryEvidence.FreshNoOrder(intent.IntentId, 0, historyComplete: false)));
    }

    [Fact]
    public void KnownOrderOrFillCannotBeClearedForRetry()
    {
        OrderIntent intent = CreateIntent("intent-known", "client-known");
        ExecutionStateSnapshot state = ExecutionStateMachine.Claim(
            ExecutionStateMachine.Start(intent), 1);
        state = ExecutionStateMachine.Apply(state,
            ExecutionObservation.Accepted("receipt-accepted", intent.IntentId, "exchange-1")).State;
        state = ExecutionStateMachine.Apply(state,
            ExecutionObservation.Timeout("receipt-timeout", intent.IntentId)).State;

        Assert.Throws<InvalidOperationException>(() => ExecutionStateMachine.RecoverForRetry(state,
            ExecutionRecoveryEvidence.FreshNoOrder(intent.IntentId, 1)));
    }

    private static OrderIntent CreateIntent(string intentId, string clientOrderId) =>
        new(intentId, "run-1", "setup-1", clientOrderId, Instrument, Direction.Long,
            OrderRole.Entry, 1, new Quantity(1m), new Price(100m));
}
