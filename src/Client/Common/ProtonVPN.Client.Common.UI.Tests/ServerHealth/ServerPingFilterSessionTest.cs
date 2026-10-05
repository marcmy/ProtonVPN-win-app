using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerPingFilterSessionTest
{
    private readonly FakeServerHealthClock _clock = new();
    private ServerHealthHistoryStore _store = null!;

    [TestInitialize]
    public void Initialize() => _store = new(_clock);

    [TestCleanup]
    public void Cleanup() => _store.Dispose();

    [TestMethod]
    public void Options_ExposeRequestedThresholdsInOrder()
    {
        ServerPingFilterSession filter = new(_store);

        CollectionAssert.AreEqual(
            new int?[] { null, 150, 100, 75, 50, 25 },
            filter.Options.Select(option => option.MaxLatencyMilliseconds).ToArray());
    }

    [TestMethod]
    public void Matches_AllowsUnmeasuredServerWhenFilterIsDisabled()
    {
        ServerPingFilterSession filter = new(_store);
        QueueServerHealthSource source = CreateSource("all");

        Assert.IsTrue(filter.Matches(source));
    }

    [TestMethod]
    public void Matches_RejectsUnmeasuredServerWhenThresholdIsActive()
    {
        ServerPingFilterSession filter = new(_store);
        filter.SelectedOption = filter.Options.Single(option => option.MaxLatencyMilliseconds == 50);
        QueueServerHealthSource source = CreateSource("threshold");

        Assert.IsFalse(filter.Matches(source));
    }

    [TestMethod]
    public async Task SelectingThresholds_UsesSavedLatencyWithoutStartingProbes()
    {
        ServerPingFilterSession filter = new(_store);
        QueueServerHealthSource source = CreateSource("saved");
        source.Enqueue(new ServerHealthProbeMeasurement(40, 4, 4, _clock.UtcNow, true, null, 0.25));
        await _store.ProbeAsync(source, CancellationToken.None);
        // Rolling graph history expires, but the saved ping remains usable immediately.
        _clock.Advance(TimeSpan.FromDays(1));
        foreach (ServerPingFilterOption option in filter.Options)
        {
            filter.SelectedOption = option;
            Assert.AreEqual(option.MaxLatencyMilliseconds is null or >= 40, filter.Matches(source));
        }
        Assert.AreEqual(1, source.ProbeCount);
        Assert.AreEqual(40d, filter.GetAverageLatencyMilliseconds(source));
    }

    private static QueueServerHealthSource CreateSource(string suffix) =>
        new()
        {
            HealthServerId = $"server-{suffix}",
            HealthProbeAddress = "192.0.2.1",
            HealthServerLoad = 0.25,
        };
}
