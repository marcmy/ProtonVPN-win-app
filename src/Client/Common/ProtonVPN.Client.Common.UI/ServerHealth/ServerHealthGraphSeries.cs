using System;
using System.Collections.Generic;
using System.Linq;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthGraphPoint(
    ServerHealthProbeMeasurement Measurement,
    IReadOnlyList<ServerHealthObservation> Observations,
    bool IsInRollingWindow,
    bool IsLatest)
{
    public DateTimeOffset CheckedAt => Measurement.CheckedAt;

    public double? LatencyMilliseconds =>
        Measurement.UsedPhysicalRoute && Measurement.AverageLatencyMilliseconds is double latency &&
        double.IsFinite(latency) && latency >= 0 ? latency : null;

    public int TotalSamples => Measurement.IsQualifiedFixedCheck ? Measurement.TotalSamples : 0;

    public int? MissedSamples => TotalSamples == 0 ? null : TotalSamples - Measurement.SuccessfulSamples;
}

public static class ServerHealthGraphSeries
{
    public static IReadOnlyList<ServerHealthGraphPoint> Create(ServerHealthSnapshot snapshot)
    {
        HashSet<ServerHealthProbeMeasurement> rollingWindow = new(ReferenceEqualityComparer.Instance);
        if (!snapshot.IsCached && !snapshot.IsStale)
        {
            rollingWindow.UnionWith(ServerHealthCalculator.GetQualifiedWindow(snapshot.Measurements));
        }

        IEnumerable<ServerHealthProbeMeasurement> measurements = snapshot.Measurements;
        if (snapshot.LatestMeasurement is ServerHealthProbeMeasurement latest &&
            !measurements.Contains(latest, ReferenceEqualityComparer.Instance))
        {
            measurements = measurements.Append(latest);
        }

        return measurements.OrderBy(measurement => measurement.CheckedAt)
            .Select(measurement => new ServerHealthGraphPoint(
                measurement,
                measurement.UsedPhysicalRoute && measurement.Observations is not null
                    ? measurement.Observations.OfType<ServerHealthObservation>().OrderBy(observation => observation.SentAt).ToArray()
                    : [],
                rollingWindow.Contains(measurement),
                ReferenceEquals(measurement, snapshot.LatestMeasurement)))
            .ToArray();
    }
}
