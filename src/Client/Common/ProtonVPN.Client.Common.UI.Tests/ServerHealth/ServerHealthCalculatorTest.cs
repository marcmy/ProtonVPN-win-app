using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthCalculatorTest
{
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
        Assert.IsTrue(result.Grade is ServerHealthGrade.Good or ServerHealthGrade.Excellent);
    }

    [TestMethod]
    public void Aggregate_WeightsLatencyBySuccessfulReplies()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(20, 4, 4, 0.20, 0),
            Measurement(100, 1, 4, 0.20, 30),
        ];

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

        Assert.AreEqual(36d, result.AverageLatencyMilliseconds!.Value, 0.001);
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
    [DataRow(1, "Based on 1 of 3 checks")]
    [DataRow(2, "Based on 2 of 3 checks")]
    [DataRow(3, "Based on 3 checks")]
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
        new(
            latency,
            successful,
            total,
            DateTimeOffset.UnixEpoch.AddSeconds(seconds),
            true,
            outage ? "No replies." : null,
            load,
            WasRetried: outage,
            IsConfirmedOutage: outage);
}
