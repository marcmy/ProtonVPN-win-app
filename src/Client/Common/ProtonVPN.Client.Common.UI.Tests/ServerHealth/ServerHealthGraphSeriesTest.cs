using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthGraphSeriesTest
{
    [TestMethod]
    public void Create_OrdersPointsAndMarksLatestRecordedCheckAndRollingWindow()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            FixedCheck(90, 40, 40, 40, 40),
            FixedCheck(0, 50, 50, 50, 50),
            FixedCheck(60, null, null, null, null),
            FixedCheck(30, 45, 45, 45, null),
            FixedCheck(120, 42, 42, 42, 42),
            FixedCheck(150, 43, 43, 43, 43),
            new(35, 1, 1, DateTimeOffset.UnixEpoch.AddSeconds(180), true, null, 0.25, IsDiscovery: true,
                RouteContext: "route",
                Observations: [new(ServerHealthObservationOutcome.Reply, 35, DateTimeOffset.UnixEpoch.AddSeconds(180))]),
            FixedCheck(-30, 44, 44, 44, 44),
        ];

        IReadOnlyList<ServerHealthGraphPoint> result = ServerHealthGraphSeries.Create(CreateSnapshot(measurements));

        CollectionAssert.AreEqual(
            new[] { -30, 0, 30, 60, 90, 120, 150, 180 },
            result.Select(point => (int)(point.CheckedAt - DateTimeOffset.UnixEpoch).TotalSeconds).ToArray());
        CollectionAssert.AreEqual(
            new[] { true, true, true, true, false, true, true, false },
            result.Select(point => point.IsInRollingWindow).ToArray());
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddSeconds(-30), result.Single(point => point.IsLatest).CheckedAt);
        Assert.AreEqual(0, result[^1].TotalSamples);
        Assert.IsNull(result[^1].MissedSamples);
    }

    [TestMethod]
    public void Create_LatestMixedCheckShowsIndividualOutlierAndMissedEcho()
    {
        ServerHealthProbeMeasurement mixed = FixedCheck(120, 41, 320, 43, null);
        ServerHealthProbeMeasurement[] measurements =
        [
            FixedCheck(0, 36, 40, 35, 45),
            FixedCheck(30, 40, 41, 42, 43),
            FixedCheck(60, 40, 40, 42, 42),
            FixedCheck(90, 42, 41, 40, 39),
            mixed with { Observations = mixed.Observations!.Reverse().ToArray() },
        ];
        ServerHealthSnapshot snapshot = CreateSnapshot(measurements);

        ServerHealthGraphPoint latest = ServerHealthGraphSeries.Create(snapshot).Single(point => point.IsLatest);
        ServerHealthPresentation presentation = ServerHealthPresentation.FromSnapshot(snapshot);

        Assert.AreEqual("5%", presentation.PacketLossText);
        Assert.AreEqual("1 of 20 missed", presentation.LossCountText);
        Assert.AreEqual(1, latest.MissedSamples);
        Assert.AreEqual(4, latest.TotalSamples);
        CollectionAssert.AreEqual(new double?[] { 41, 320, 43, null },
            latest.Observations.Select(observation => observation.RoundTripTimeMilliseconds).ToArray());
        Assert.AreEqual(ServerHealthObservationOutcome.TimedOut, latest.Observations[^1].Outcome);
    }

    [TestMethod]
    public void Create_LegacyOutageHasNoMeasuredLossOrLatency()
    {
        ServerHealthProbeMeasurement outage = new(
            null, 0, 4, DateTimeOffset.UnixEpoch, true, "Confirmed outage", 0.25,
            WasRetried: true, IsConfirmedOutage: true);

        ServerHealthGraphPoint result = ServerHealthGraphSeries.Create(CreateSnapshot([outage])).Single();

        Assert.IsNull(result.LatencyMilliseconds);
        Assert.AreEqual(0, result.TotalSamples);
        Assert.IsNull(result.MissedSamples);
        Assert.HasCount(0, result.Observations);
    }

    [TestMethod]
    public void Create_SavedLegacyLatencyIsDisplayedOutsideTheRollingWindow()
    {
        ServerHealthProbeMeasurement saved = new(42, 3, 4, DateTimeOffset.UnixEpoch, true, null, 0.25);
        ServerHealthSnapshot snapshot = CreateSnapshot([saved]) with
        {
            Measurements = [],
            IsCached = true,
            IsStale = true,
        };

        ServerHealthGraphPoint point = ServerHealthGraphSeries.Create(snapshot).Single();

        Assert.AreEqual(42d, point.LatencyMilliseconds);
        Assert.IsNull(point.MissedSamples);
        Assert.IsFalse(point.IsInRollingWindow);
    }

    [TestMethod]
    public void Create_RouteFailureDoesNotDrawUnverifiedReplies()
    {
        ServerHealthProbeMeasurement invalidRoute = new(
            40, 1, 4, DateTimeOffset.UnixEpoch, false, "Route unavailable", 0.25,
            Observations: [new(ServerHealthObservationOutcome.Reply, 40, DateTimeOffset.UnixEpoch)]);

        ServerHealthGraphPoint result = ServerHealthGraphSeries.Create(CreateSnapshot([invalidRoute])).Single();

        Assert.IsNull(result.LatencyMilliseconds);
        Assert.IsNull(result.MissedSamples);
        Assert.HasCount(0, result.Observations);
    }

    private static ServerHealthSnapshot CreateSnapshot(ServerHealthProbeMeasurement[] measurements) =>
        ServerHealthSnapshot.CreateRecorded(
            ServerHealthHistoryKey.Create("server-1", "10.0.0.1"),
            measurements,
            ServerHealthCalculator.Aggregate(measurements));

    private static ServerHealthProbeMeasurement FixedCheck(int seconds, params double?[] roundTrips)
    {
        DateTimeOffset time = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        double[] replies = roundTrips.OfType<double>().ToArray();
        return new(
            replies.Length == 0 ? null : replies.Average(), replies.Length, roundTrips.Length,
            time, true, null, 0.25, RouteContext: "route",
            Observations: roundTrips.Select((rtt, index) => new ServerHealthObservation(
                rtt is null ? ServerHealthObservationOutcome.TimedOut : ServerHealthObservationOutcome.Reply,
                rtt, time.AddMilliseconds(index * 100))).ToArray());
    }
}
