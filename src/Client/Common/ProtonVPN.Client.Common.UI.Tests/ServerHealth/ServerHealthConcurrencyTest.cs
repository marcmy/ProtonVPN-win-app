using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthConcurrencyTest
{
    [TestMethod]
    public async Task RetryDelay_DoesNotOccupyTheOnlyProbeSlot()
    {
        FakeServerHealthClock clock = new() { BlockDelays = true };
        QueueServerHealthSource failingSource = new()
        {
            HealthServerId = "server-a",
            HealthProbeAddress = "10.0.0.1",
            HealthServerLoad = 0.20,
        };
        failingSource.Enqueue(new ServerHealthProbeMeasurement(
            null,
            0,
            4,
            clock.UtcNow,
            false,
            "first failure",
            failingSource.HealthServerLoad));
        failingSource.Enqueue(new ServerHealthProbeMeasurement(
            40,
            4,
            4,
            clock.UtcNow,
            true,
            null,
            failingSource.HealthServerLoad));

        QueueServerHealthSource healthySource = new()
        {
            HealthServerId = "server-b",
            HealthProbeAddress = "10.0.0.2",
            HealthServerLoad = 0.20,
        };
        healthySource.Enqueue(new ServerHealthProbeMeasurement(
            30,
            4,
            4,
            clock.UtcNow,
            true,
            null,
            healthySource.HealthServerLoad));

        using ServerHealthHistoryStore store = new(clock, maximumConcurrentProbes: 1);
        Task<ServerHealthSnapshot> retrying =
            store.ProbeAsync(failingSource, CancellationToken.None);
        Assert.IsTrue(SpinWait.SpinUntil(
            () => clock.Delays.Count == 1,
            TimeSpan.FromSeconds(1)));

        ServerHealthSnapshot healthy = await store
            .ProbeAsync(healthySource, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, healthySource.ProbeCount);
        Assert.AreEqual(0d, healthy.Aggregate!.PacketLossPercent, 0.001);

        clock.CompleteDelay();
        await retrying;
    }
}
