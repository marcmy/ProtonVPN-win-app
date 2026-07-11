namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthSnapshot(
    ServerHealthHistoryKey Key,
    IReadOnlyList<ServerHealthProbeMeasurement> Measurements,
    ServerHealthAggregate? Aggregate,
    ServerHealthProbeMeasurement? LatestMeasurement,
    bool IsChecking,
    bool IsRechecking,
    string? PendingError)
{
    public static ServerHealthSnapshot Empty(ServerHealthHistoryKey key) =>
        new(key, [], null, null, false, false, null);

    public static ServerHealthSnapshot CreateRecorded(
        ServerHealthHistoryKey key,
        IReadOnlyList<ServerHealthProbeMeasurement> measurements,
        ServerHealthAggregate aggregate) =>
        new(
            key,
            measurements,
            aggregate,
            measurements.Count == 0 ? null : measurements[^1],
            false,
            false,
            null);

    public int ConfidenceCount => Math.Min(Measurements.Count, 3);
}
