using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthGraphSeriesTest
{
    [TestMethod]
    public void Create_OrdersPointsAndMarksNewestThree()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(90, 40, 4),
            Measurement(0, 50, 4),
            Measurement(60, null, 0, true),
            Measurement(30, 45, 3),
        ];
        ServerHealthSnapshot snapshot = ServerHealthSnapshot.CreateRecorded(
            ServerHealthHistoryKey.Create("server-1", "10.0.0.1"),
            measurements,
            ServerHealthCalculator.Aggregate(measurements));

        IReadOnlyList<ServerHealthGraphPoint> result = ServerHealthGraphSeries.Create(snapshot);

        CollectionAssert.AreEqual(
            new[] { 0, 30, 60, 90 },
            result.Select(point => (int)(point.CheckedAt - DateTimeOffset.UnixEpoch).TotalSeconds).ToArray());
        CollectionAssert.AreEqual(
            new[] { false, true, true, true },
            result.Select(point => point.IsScoreDriver).ToArray());
    }

    [TestMethod]
    public void Create_OutageHasLossButNoLatency()
    {
        ServerHealthProbeMeasurement outage = Measurement(0, null, 0, true);
        ServerHealthSnapshot snapshot = ServerHealthSnapshot.CreateRecorded(
            ServerHealthHistoryKey.Create("server-1", "10.0.0.1"),
            [outage],
            ServerHealthCalculator.Aggregate([outage]));

        ServerHealthGraphPoint result = ServerHealthGraphSeries.Create(snapshot).Single();

        Assert.IsNull(result.LatencyMilliseconds);
        Assert.AreEqual(100d, result.PacketLossPercent, 0.001);
        Assert.IsTrue(result.IsConfirmedOutage);
    }

    private static ServerHealthProbeMeasurement Measurement(
        int seconds,
        double? latency,
        int successful,
        bool outage = false) =>
        new(
            latency,
            successful,
            4,
            DateTimeOffset.UnixEpoch.AddSeconds(seconds),
            true,
            outage ? "Confirmed outage" : null,
            0.25,
            WasRetried: outage,
            IsConfirmedOutage: outage);
}
