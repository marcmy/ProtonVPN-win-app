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
        ServerHealthProbeMeasurement[] ordered = snapshot.Measurements
            .OrderBy(measurement => measurement.CheckedAt)
            .ToArray();
        int scoreStart = Math.Max(0, ordered.Length - 3);
        return ordered.Select((measurement, index) => new ServerHealthGraphPoint(
            measurement.CheckedAt,
            measurement.AverageLatencyMilliseconds,
            measurement.PacketLossPercent,
            measurement.ServerLoad,
            measurement.SuccessfulSamples,
            measurement.TotalSamples,
            measurement.WasRetried,
            measurement.IsConfirmedOutage,
            index >= scoreStart,
            measurement.Error)).ToArray();
    }
}
