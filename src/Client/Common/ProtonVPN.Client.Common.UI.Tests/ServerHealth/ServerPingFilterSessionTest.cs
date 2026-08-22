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

    private static QueueServerHealthSource CreateSource(string suffix) =>
        new()
        {
            HealthServerId = $"server-{suffix}",
            HealthProbeAddress = $"192.0.2.{suffix.Length + 1}",
            HealthServerLoad = 0.25,
        };
}
