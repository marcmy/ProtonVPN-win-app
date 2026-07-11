using System;
using System.Collections.Generic;
using System.Linq;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthGraphPoint(
    DateTimeOffset CheckedAt,
    double? LatencyMilliseconds,
    double PacketLossPercent,
    double ServerLoad,
    int SuccessfulSamples,
    int TotalSamples,
    bool WasRetried,
    bool IsConfirmedOutage,
    bool IsScoreDriver,
    string? Error);

public static class ServerHealthGraphSeries
{
    public static IReadOnlyList<ServerHealthGraphPoint> Create(ServerHealthSnapshot snapshot)
    {
        int scoreStart = Math.Max(
            0,
            snapshot.Measurements.Count - ServerHealthCalculator.ScoreMeasurementCount);
        return snapshot.Measurements
            .Select((measurement, index) => (
                Measurement: measurement,
                IsScoreDriver: index >= scoreStart))
            .OrderBy(item => item.Measurement.CheckedAt)
            .Select(item => new ServerHealthGraphPoint(
                item.Measurement.CheckedAt,
                item.Measurement.AverageLatencyMilliseconds,
                item.Measurement.PacketLossPercent,
                item.Measurement.ServerLoad,
                item.Measurement.SuccessfulSamples,
                item.Measurement.TotalSamples,
                item.Measurement.WasRetried,
                item.Measurement.IsConfirmedOutage,
                item.IsScoreDriver,
                item.Measurement.Error))
            .ToArray();
    }
}
