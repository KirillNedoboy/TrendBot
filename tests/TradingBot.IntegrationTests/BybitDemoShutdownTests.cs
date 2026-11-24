using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoShutdownTests
{
    [Fact]
    public async Task SignalStopReturnsBeforeBlockingCallbacksAndRetainsTheirCompletion()
    {
        using CancellationTokenSource stop = new();
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = stop.Token.Register(() =>
        {
            entered.TrySetResult();
            release.Wait();
        });
        Task? dispatch = null;
        Task<Task> request = Task.Factory.StartNew(
            () => BybitDemoCommand.RequestSignalStopAsync(stop, ref dispatch),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Task callbacks = await request.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(stop.IsCancellationRequested);
            Assert.False(callbacks.IsCompleted);
            Assert.Same(callbacks, BybitDemoCommand.RequestSignalStopAsync(stop, ref dispatch));
        }
        finally
        {
            release.Set();
            Task callbacks = await request.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await callbacks.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }
}
