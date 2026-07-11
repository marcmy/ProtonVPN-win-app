namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthAggregate(
    double? AverageLatencyMilliseconds,
    double PacketLossPercent,
    int SuccessfulSamples,
    int TotalSamples,
    double ServerLoad,
    double Score,
    ServerHealthGrade Grade,
    int MeasurementCount);
