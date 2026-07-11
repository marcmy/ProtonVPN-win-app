using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthCalculatorTest
{
    [TestMethod]
    public void Aggregate_OneMissAcrossThreeBatches_UsesOneOfTwelveLoss()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(40, 4, 4, 0.20, 0),
            Measurement(42, 3, 4, 0.25, 30),
            Measurement(41, 4, 4, 0.30, 60),
        ];

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

        Assert.AreEqual(11, result.SuccessfulSamples);
        Assert.AreEqual(12, result.TotalSamples);
        Assert.AreEqual(100d / 12d, result.PacketLossPercent, 0.001);
        Assert.AreEqual(3, result.MeasurementCount);
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

        Assert.AreEqual(36, result.AverageLatencyMilliseconds!.Value, 0.001);
    }

    [TestMethod]
    public void Aggregate_UsesNewestThreeBatchesAndNewestLoad()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(null, 0, 4, 0.10, 0, true),
            Measurement(30, 4, 4, 0.20, 30),
            Measurement(31, 4, 4, 0.30, 60),
            Measurement(32, 4, 4, 0.40, 90),
        ];

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

        Assert.AreEqual(12, result.SuccessfulSamples);
        Assert.AreEqual(12, result.TotalSamples);
        Assert.AreEqual(0, result.PacketLossPercent, 0.001);
        Assert.AreEqual(0.40, result.ServerLoad, 0.001);
    }

    [TestMethod]
    public void Aggregate_ConfirmedOutageWithoutReplies_IsPoorAndHasNoLatency()
    {
        ServerHealthProbeMeasurement outage = Measurement(null, 0, 4, 0.20, 0, true);

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate([outage]);

        Assert.IsNull(result.AverageLatencyMilliseconds);
        Assert.AreEqual(100, result.PacketLossPercent);
        Assert.AreEqual(ServerHealthGrade.Poor, result.Grade);
    }

    [TestMethod]
    public void Aggregate_ThreeCleanFastBatches_CanBeExcellent()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(20, 4, 4, 0.10, 0),
            Measurement(22, 4, 4, 0.10, 30),
            Measurement(21, 4, 4, 0.10, 60),
        ];

        Assert.AreEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate(measurements).Grade);
    }

    [TestMethod]
    public void Aggregate_ConfirmedOutage_RemainsUntilThreeNewerBatchesReplaceIt()
    {
        ServerHealthProbeMeasurement outage = Measurement(null, 0, 4, 0.10, 0, true);
        ServerHealthProbeMeasurement first = Measurement(20, 4, 4, 0.10, 30);
        ServerHealthProbeMeasurement second = Measurement(20, 4, 4, 0.10, 60);
        ServerHealthProbeMeasurement third = Measurement(20, 4, 4, 0.10, 90);

        Assert.AreNotEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate([outage, first]).Grade);
        Assert.AreNotEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate([outage, first, second]).Grade);
        Assert.AreEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate([outage, first, second, third]).Grade);
    }

    [DataTestMethod]
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
