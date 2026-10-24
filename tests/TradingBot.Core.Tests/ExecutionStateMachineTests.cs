using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class ExecutionStateMachineTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void AcceptedPartialAndFilledFollowDeterministicQuantityTransitions()
    {
        OrderIntent intent = CreateIntent("intent-1", 1m);
        ExecutionStateSnapshot state = ExecutionStateMachine.Start(intent);

        state = Apply(state, ExecutionObservation.Accepted("receipt-accepted", "intent-1",
            "exchange-1"));
        Assert.Equal(ExecutionLifecycleState.Accepted, state.State);

        state = Apply(state, ExecutionObservation.PartialFill("receipt-partial", "intent-1",
            "exchange-1", 0.4m));
        Assert.Equal(ExecutionLifecycleState.PartiallyFilled, state.State);
        Assert.Equal(0.4m, state.FilledQuantity);

        state = Apply(state, ExecutionObservation.Filled("receipt-filled", "intent-1",
            "exchange-1", 0.6m));
        Assert.Equal(ExecutionLifecycleState.Filled, state.State);
        Assert.Equal(1m, state.FilledQuantity);
    }

    [Fact]
    public void ExactReceiptRetryIsIdempotentAndConflictingReceiptFailsClosed()
    {
        ExecutionStateSnapshot state = ExecutionStateMachine.Start(CreateIntent("intent-2", 1m));
        ExecutionObservation accepted = ExecutionObservation.Accepted("receipt-1", "intent-2",
            "exchange-2");

        ExecutionTransitionResult first = ExecutionStateMachine.Apply(state, accepted);
        ExecutionTransitionResult duplicate = ExecutionStateMachine.Apply(first.State, accepted);
        Assert.Equal(ExecutionApplyDisposition.Applied, first.Disposition);
        Assert.Equal(ExecutionApplyDisposition.AlreadyApplied, duplicate.Disposition);

        ExecutionTransitionResult conflict = ExecutionStateMachine.Apply(first.State,
            ExecutionObservation.Rejected("receipt-1", "intent-2", "exchange-2", "different"));
        Assert.Equal(ExecutionApplyDisposition.Conflict, conflict.Disposition);
        Assert.Equal(ExecutionLifecycleState.Conflict, conflict.State.State);
        Assert.True(conflict.State.RequiresReconciliation);
    }

    [Fact]
    public void RejectionCancelAndTimeoutStatesAreExplicit()
    {
        ExecutionStateSnapshot rejected = ExecutionStateMachine.Start(CreateIntent("intent-3", 1m));
        rejected = Apply(rejected, ExecutionObservation.Accepted("accepted", "intent-3", "e-3"));
        rejected = Apply(rejected, ExecutionObservation.Rejected("rejected", "intent-3", "e-3",
            "FILTER"));
        Assert.Equal(ExecutionLifecycleState.Rejected, rejected.State);

        ExecutionStateSnapshot cancel = ExecutionStateMachine.Start(CreateIntent("intent-4", 1m));
        cancel = Apply(cancel, ExecutionObservation.Accepted("accepted", "intent-4", "e-4"));
        cancel = Apply(cancel, ExecutionObservation.CancelRequested("cancel-request", "intent-4",
            "e-4"));
        cancel = Apply(cancel, ExecutionObservation.Canceled("canceled", "intent-4", "e-4"));
        Assert.Equal(ExecutionLifecycleState.Canceled, cancel.State);

        ExecutionStateSnapshot timeout = ExecutionStateMachine.Start(CreateIntent("intent-5", 1m));
        timeout = ExecutionStateMachine.Claim(timeout, 1);
        timeout = Apply(timeout, ExecutionObservation.Timeout("timeout", "intent-5"));
        Assert.Equal(ExecutionLifecycleState.TimedOut, timeout.State);
        Assert.True(timeout.RequiresReconciliation);
    }

    [Fact]
    public void UnknownOutcomeBlocksNonReconciliationObservationAndAllowsExplicitRecovery()
    {
        ExecutionStateSnapshot state = ExecutionStateMachine.Start(CreateIntent("intent-6", 1m));
        state = ExecutionStateMachine.Claim(state, 1);
        state = Apply(state, ExecutionObservation.Unknown("unknown", "intent-6"));

        ExecutionTransitionResult blocked = ExecutionStateMachine.Apply(state,
            ExecutionObservation.Accepted("late-accepted", "intent-6", "e-6"));
        Assert.Equal(ExecutionApplyDisposition.Blocked, blocked.Disposition);
        Assert.Equal(ExecutionLifecycleState.Unknown, blocked.State.State);

        ExecutionTransitionResult recovered = ExecutionStateMachine.Apply(state,
            ExecutionObservation.Accepted("reconciled-accepted", "intent-6", "e-6", true));
        Assert.Equal(ExecutionApplyDisposition.Applied, recovered.Disposition);
        Assert.Equal(ExecutionLifecycleState.Accepted, recovered.State.State);
        Assert.False(recovered.State.RequiresReconciliation);
    }

    [Fact]
    public void PartialFillCannotExceedIntentAndTerminalStateCannotChange()
    {
        ExecutionStateSnapshot state = ExecutionStateMachine.Start(CreateIntent("intent-7", 1m));
        state = Apply(state, ExecutionObservation.Accepted("accepted", "intent-7", "e-7"));
        ExecutionTransitionResult overfill = ExecutionStateMachine.Apply(state,
            ExecutionObservation.PartialFill("overfill", "intent-7", "e-7", 1.1m));
        Assert.Equal(ExecutionApplyDisposition.Conflict, overfill.Disposition);
        Assert.Equal(ExecutionLifecycleState.Conflict, overfill.State.State);

        ExecutionStateSnapshot filled = Apply(state,
            ExecutionObservation.Filled("filled", "intent-7", "e-7", 1m));
        ExecutionTransitionResult lateCancel = ExecutionStateMachine.Apply(filled,
            ExecutionObservation.Canceled("late-cancel", "intent-7", "e-7"));
        Assert.Equal(ExecutionApplyDisposition.Conflict, lateCancel.Disposition);
        Assert.Equal(ExecutionLifecycleState.Conflict, lateCancel.State.State);
    }

    [Fact]
    public void ObservationIdentityIncludesIntentAndExchangeOrder()
    {
        ExecutionStateSnapshot state = ExecutionStateMachine.Start(CreateIntent("intent-8", 1m));
        Assert.Throws<ArgumentException>(() => ExecutionObservation.Accepted("", "intent-8", "e-8"));
        Assert.Throws<InvalidOperationException>(() => ExecutionStateMachine.Apply(state,
            ExecutionObservation.Accepted("r", "other", "e-8")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExecutionObservation.PartialFill("r",
            "intent-8", "e-8", 0m));
        Assert.Equal(ExecutionLifecycleState.IntentPersisted, state.State);
    }

    private static ExecutionStateSnapshot Apply(ExecutionStateSnapshot state,
        ExecutionObservation observation)
    {
        ExecutionTransitionResult result = ExecutionStateMachine.Apply(state, observation);
        Assert.Equal(ExecutionApplyDisposition.Applied, result.Disposition);
        return result.State;
    }

    private static OrderIntent CreateIntent(string intentId, decimal quantity) =>
        new(intentId, "run-1", "setup-1", $"client-{intentId}", Instrument,
            Direction.Long, OrderRole.Entry, 1, new Quantity(quantity), new Price(100m));
}
