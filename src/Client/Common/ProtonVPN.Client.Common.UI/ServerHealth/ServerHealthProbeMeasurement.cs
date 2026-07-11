namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthProbeMeasurement(
    double? AverageLatencyMilliseconds,
    int SuccessfulSamples,
    int TotalSamples,
    DateTimeOffset CheckedAt,
    bool UsedPhysicalRoute,
    string? Error,
    double ServerLoad,
    bool WasRetried = false,
    bool IsConfirmedOutage = false)
{
    public double PacketLossPercent => TotalSamples <= 0
        ? 100
        : (TotalSamples - SuccessfulSamples) * 100d / TotalSamples;

    public bool IsCompleteFailure =>
        SuccessfulSamples <= 0 || AverageLatencyMilliseconds is null;
}
