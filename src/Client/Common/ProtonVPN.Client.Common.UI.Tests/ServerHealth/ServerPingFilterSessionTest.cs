using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerPingFilterSessionTest
{
    [TestMethod]
    public void Options_ExposeRequestedThresholdsInOrder()
    {
        ServerPingFilterSession filter = new();

        CollectionAssert.AreEqual(
            new int?[] { null, 150, 100, 75, 50, 25 },
            filter.Options.Select(option => option.MaxLatencyMilliseconds).ToArray());
    }

    [TestMethod]
    public void Matches_AllowsUnmeasuredServerWhenFilterIsDisabled()
    {
        ServerPingFilterSession filter = new();
        QueueServerHealthSource source = CreateSource("all");

        Assert.IsTrue(filter.Matches(source));
    }

    [TestMethod]
    public void Matches_RejectsUnmeasuredServerWhenThresholdIsActive()
    {
        ServerPingFilterSession filter = new();
        filter.SelectedOption = filter.Options.Single(option => option.MaxLatencyMilliseconds == 50);
        QueueServerHealthSource source = CreateSource("threshold");

        Assert.IsFalse(filter.Matches(source));
    }

    [TestMethod]
    public async Task ProbeAsync_LimitsFilterAdmissionToFourConcurrentRequests()
    {
        ServerPingFilterSession filter = new();
        QueueServerHealthSource[] sources = Enumerable.Range(1, 5)
            .Select(index => CreateSource($"bounded-{index}"))
            .ToArray();
        TaskCompletionSource<ServerHealthProbeMeasurement>[] completions = Enumerable.Range(1, 5)
            .Select(_ => new TaskCompletionSource<ServerHealthProbeMeasurement>(
                TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

        for (int i = 0; i < sources.Length; i++)
        {
            int index = i;
            sources[i].Enqueue(_ => completions[index].Task);
        }

        Task<ServerHealthSnapshot>[] probes = sources
            .Select(source => filter.ProbeAsync(source, CancellationToken.None))
            .ToArray();

        Assert.IsTrue(SpinWait.SpinUntil(
            () => sources.Take(4).All(source => source.ProbeCount == 1),
            TimeSpan.FromSeconds(1)));
        Assert.AreEqual(0, sources[4].ProbeCount);

        completions[0].SetResult(Success());
        await probes[0];

        Assert.IsTrue(SpinWait.SpinUntil(
            () => sources[4].ProbeCount == 1,
            TimeSpan.FromSeconds(1)));

        for (int i = 1; i < completions.Length; i++)
        {
            completions[i].SetResult(Success());
        }

        await Task.WhenAll(probes);
    }

    private static ServerHealthProbeMeasurement Success() =>
        new(25, 4, 4, DateTimeOffset.UtcNow, true, null, 0.25);

    private static QueueServerHealthSource CreateSource(string suffix) =>
        new()
        {
            HealthServerId = $"server-{suffix}",
            HealthProbeAddress = $"192.0.2.{Math.Abs(suffix.GetHashCode()) % 200 + 1}",
            HealthServerLoad = 0.25,
        };
}
