using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthConcurrencyTest
{
    [TestMethod]
    public async Task CancelledQueuedWork_NeverUsesTheProbeSlot()
    {
        FakeServerHealthClock clock = new();
        var first = new QueueServerHealthSource { HealthServerId = "a", HealthProbeAddress = "10.0.0.1" };
        var cancelled = new QueueServerHealthSource { HealthServerId = "b", HealthProbeAddress = "10.0.0.2" };
        var third = new QueueServerHealthSource { HealthServerId = "c", HealthProbeAddress = "10.0.0.3" };
        TaskCompletionSource<ServerHealthProbeMeasurement> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Enqueue(token => gate.Task.WaitAsync(token));
        third.Enqueue(ServerHealthSamples.FixedCheck(clock.UtcNow));
        using ServerHealthHistoryStore store = new(clock, maximumConcurrentProbes: 1);
        using CancellationTokenSource cancellation = new();

        Task<ServerHealthSnapshot> active = store.ProbeAsync(first, CancellationToken.None);
        Task<ServerHealthSnapshot> dropped = store.ProbeAsync(cancelled, cancellation.Token);
        Task<ServerHealthSnapshot> waiting = store.ProbeAsync(third, CancellationToken.None);
        Assert.AreEqual(0, third.ProbeCount);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => dropped);
        gate.SetResult(ServerHealthSamples.FixedCheck(clock.UtcNow));
        await active;

        ServerHealthSnapshot completed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, cancelled.ProbeCount);
        Assert.AreEqual(1, third.ProbeCount);
        Assert.AreEqual(4, completed.Aggregate!.TotalSamples);
    }
}
