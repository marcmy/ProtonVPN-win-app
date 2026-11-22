namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthAggregate(
    double? AverageLatencyMilliseconds,
    double PacketLossPercent,
    int SuccessfulSamples,
    int TotalSamples,
    double ServerLoad,
    double Score,
    ServerHealthGrade Grade,
    int MeasurementCount,
    bool IsLossEstablished = false,
    bool IsLatencyEstimate = false,
    double? VariationMilliseconds = null,
    bool HasObservedReply = false)
{
    public int MissedSamples => TotalSamples - SuccessfulSamples;
    public bool HasLossEvidence => TotalSamples > 0 && HasObservedReply;
}
