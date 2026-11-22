using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthCalculatorTest
{
    [TestMethod]
    public void Aggregate_LocalRouteFailureDoesNotCountAsPacketLoss()
    {
        ServerHealthProbeMeasurement failure = Measurement(null, 0, 4, 0.10, 30) with
        {
            UsedPhysicalRoute = false,
            Error = "Route unavailable",
        };
        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(
            [Measurement(20, 4, 4, 0.10, 0), failure]);

        Assert.AreEqual(4, result.TotalSamples);
        Assert.AreEqual(0d, result.PacketLossPercent);
    }

    [TestMethod]
    public void Aggregate_OneMissAcrossSixBatches_UsesOneOfTwentyFourLoss()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(40, 4, 4, 0.20, 0),
            Measurement(42, 3, 4, 0.25, 30),
            Measurement(41, 4, 4, 0.30, 60),
            Measurement(39, 4, 4, 0.20, 90),
            Measurement(40, 4, 4, 0.20, 120),
            Measurement(41, 4, 4, 0.20, 150),
        ];

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

        Assert.AreEqual(23, result.SuccessfulSamples);
        Assert.AreEqual(24, result.TotalSamples);
        Assert.AreEqual(100d / 24d, result.PacketLossPercent, 0.001);
        Assert.AreEqual(6, result.MeasurementCount);
        Assert.AreEqual(ServerHealthGrade.Good, result.Grade);
        Assert.IsTrue(result.IsLossEstablished);
    }

    [TestMethod]
    public void Aggregate_UsesMedianInsteadOfHighLatencyOutliers()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(20, 4, 4, 0.20, 0),
            Measurement(100, 1, 4, 0.20, 30),
        ];

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

        Assert.AreEqual(20d, result.AverageLatencyMilliseconds!.Value, 0.001);
    }

    [TestMethod]
    public void Aggregate_MixedDiscoveryAndLegacyResultsOnlyPublishQualifiedEvidence()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            new(19, 1, 1, DateTimeOffset.UnixEpoch, true, null, 0.20, IsDiscovery: true),
            Measurement(20, 4, 4, 0.20, 30),
            Measurement(21, 4, 4, 0.20, 60) with { Observations = null },
            Measurement(22, 4, 4, 0.20, 90),
            Measurement(24, 3, 4, 0.20, 120),
        ];
        ServerHealthAggregate aggregate = ServerHealthCalculator.Aggregate(measurements);
        ServerHealthSnapshot snapshot = ServerHealthSnapshot.CreateRecorded(
            ServerHealthHistoryKey.Create("server-1", "192.0.2.1"), measurements, aggregate);
        ServerHealthPresentation presentation = ServerHealthPresentation.FromSnapshot(snapshot);

        Assert.AreEqual(12, aggregate.TotalSamples);
        Assert.AreEqual("8%", presentation.PacketLossText);
        Assert.AreEqual("1 of 12 missed", presentation.LossCountText);
        Assert.AreEqual(25d, snapshot.LatestMeasurement!.PacketLossPercent);
        Assert.AreEqual(3, presentation.ActiveBarCount);
    }

    [TestMethod]
    public void Aggregate_ThreeRapidBurstsDoNotPublishLossAsEstablished()
    {
        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(
            [Measurement(20, 4, 4, 0.2, 0), Measurement(20, 4, 4, 0.2, 1), Measurement(20, 4, 4, 0.2, 2)]);
        Assert.IsFalse(result.IsLossEstablished);
    }

    [TestMethod]
    public void FromSnapshot_PreviouslyResponsiveEndpointCanReportCompleteRecentIcmpLoss()
    {
        ServerHealthProbeMeasurement first = Measurement(20, 4, 4, 0, 0) with { IsDiscovery = true };
        ServerHealthProbeMeasurement[] measurements =
        [
            first,
            Measurement(null, 0, 4, 0, 30),
            Measurement(null, 0, 4, 0, 60),
            Measurement(null, 0, 4, 0, 90),
        ];
        var snapshot = ServerHealthSnapshot.CreateRecorded(
            ServerHealthHistoryKey.Create("responsive", "192.0.2.1"),
            measurements, ServerHealthCalculator.Aggregate(measurements));
        ServerHealthPresentation presentation = ServerHealthPresentation.FromSnapshot(snapshot);

        Assert.AreEqual("No ICMP reply", presentation.StatusText);
        Assert.AreEqual("100%", presentation.PacketLossText);
        Assert.AreEqual("12 of 12 missed", presentation.LossCountText);
        Assert.AreEqual(0, presentation.ActiveBarCount);
    }

    [TestMethod]
    public void Aggregate_UsesNewestSixBatchesAndNewestLoad()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(null, 0, 4, 0.10, 0, true),
            Measurement(30, 4, 4, 0.20, 30),
            Measurement(31, 4, 4, 0.30, 60),
            Measurement(32, 4, 4, 0.40, 90),
            Measurement(33, 4, 4, 0.50, 120),
            Measurement(34, 4, 4, 0.60, 150),
            Measurement(35, 4, 4, 0.70, 180),
        ];

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

        Assert.AreEqual(24, result.SuccessfulSamples);
        Assert.AreEqual(24, result.TotalSamples);
        Assert.AreEqual(0d, result.PacketLossPercent, 0.001);
        Assert.AreEqual(0.70, result.ServerLoad, 0.001);
        Assert.AreEqual(6, result.MeasurementCount);
    }

    [TestMethod]
    public void Aggregate_UsesRecordedOrderWhenTheClockMovesBackward()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(null, 0, 4, 0.10, 0, true),
            Measurement(30, 4, 4, 0.20, 30),
            Measurement(31, 4, 4, 0.30, 60),
            Measurement(32, 4, 4, 0.40, 90),
            Measurement(33, 4, 4, 0.50, 120),
            Measurement(34, 4, 4, 0.60, 150),
            Measurement(35, 4, 4, 0.70, -30),
        ];

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

        Assert.AreEqual(24, result.SuccessfulSamples);
        Assert.AreEqual(24, result.TotalSamples);
        Assert.AreEqual(0d, result.PacketLossPercent, 0.001);
        Assert.AreEqual(0.70, result.ServerLoad, 0.001);
        Assert.AreEqual(6, result.MeasurementCount);
    }

    [TestMethod]
    public void Aggregate_ConfirmedOutageWithoutReplies_IsPoorAndHasNoLatency()
    {
        ServerHealthProbeMeasurement outage = Measurement(null, 0, 4, 0.20, 0, true);

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate([outage]);

        Assert.IsNull(result.AverageLatencyMilliseconds);
        Assert.AreEqual(100d, result.PacketLossPercent, 0.001);
        Assert.AreEqual(ServerHealthGrade.Poor, result.Grade);
    }

    [TestMethod]
    public void Aggregate_SixCleanFastBatches_CanBeExcellent()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(20, 4, 4, 0.10, 0),
            Measurement(22, 4, 4, 0.10, 30),
            Measurement(21, 4, 4, 0.10, 60),
            Measurement(20, 4, 4, 0.10, 90),
            Measurement(22, 4, 4, 0.10, 120),
            Measurement(21, 4, 4, 0.10, 150),
        ];

        Assert.AreEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate(measurements).Grade);
    }

    [TestMethod]
    public void Aggregate_ConfirmedOutage_RemainsUntilSixNewerBatchesReplaceIt()
    {
        ServerHealthProbeMeasurement outage = Measurement(null, 0, 4, 0.10, 0, true);
        ServerHealthProbeMeasurement[] recovery = Enumerable.Range(1, 6)
            .Select(index => Measurement(20, 4, 4, 0.10, index * 30))
            .ToArray();

        for (int count = 1; count < 6; count++)
        {
            Assert.AreNotEqual(
                ServerHealthGrade.Excellent,
                ServerHealthCalculator.Aggregate([outage, .. recovery.Take(count)]).Grade,
                $"The outage should still affect the grade after only {count} newer checks.");
        }

        Assert.AreEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate([outage, .. recovery]).Grade);
    }

    [TestMethod]
    [DataRow(1, "Last 1 fixed check")]
    [DataRow(2, "Last 2 fixed checks")]
    [DataRow(3, "Last 3 fixed checks")]
    [DataRow(4, "Last 4 fixed checks")]
    [DataRow(5, "Last 5 fixed checks")]
    [DataRow(6, "Last 6 fixed checks")]
    public void FromSnapshot_FormatsConfidence(int count, string expected)
    {
        ServerHealthProbeMeasurement[] measurements = Enumerable.Range(0, count)
            .Select(i => Measurement(40, 4, 4, 0.25, i * 30))
            .ToArray();
        ServerHealthSnapshot snapshot = ServerHealthSnapshot.CreateRecorded(
            ServerHealthHistoryKey.Create("server-1", "10.0.0.1"),
            measurements,
            ServerHealthCalculator.Aggregate(measurements));

        Assert.AreEqual(
            expected,
            ServerHealthPresentation.FromSnapshot(snapshot).ConfidenceText);
    }

    [TestMethod]
    public void HistoryKey_NormalizesIdentityAndAddress()
    {
        ServerHealthHistoryKey left =
            ServerHealthHistoryKey.Create(" us-ny#79 ", " 10.0.0.1 ");
        ServerHealthHistoryKey right =
            ServerHealthHistoryKey.Create("US-NY#79", "10.0.0.1");

        Assert.AreEqual(left, right);
    }

    private static ServerHealthProbeMeasurement Measurement(
        double? latency,
        int successful,
        int total,
        double load,
        int seconds,
        bool outage = false) =>
        ServerHealthSamples.FixedCheck(DateTimeOffset.UnixEpoch.AddSeconds(seconds), latency, successful, load)
            with { TotalSamples = total, Error = outage ? "No replies." : null };
}
