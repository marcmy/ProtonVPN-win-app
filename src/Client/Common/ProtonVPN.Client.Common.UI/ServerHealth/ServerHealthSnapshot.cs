using System;
using System.Collections.Generic;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthSnapshot(
    ServerHealthHistoryKey Key,
    IReadOnlyList<ServerHealthProbeMeasurement> Measurements,
    ServerHealthAggregate? Aggregate,
    ServerHealthProbeMeasurement? LatestMeasurement,
    bool IsChecking,
    bool IsRechecking,
    string? PendingError,
    bool IsCached = false,
    bool IsStale = false,
    double? SavedLatencyMilliseconds = null,
    DateTimeOffset? FreshUntil = null)
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

    public int ConfidenceCount => Aggregate?.MeasurementCount ?? 0;

    public double? CurrentLatencyMilliseconds =>
        IsStale || IsCached || LatestMeasurement?.IsCompleteFailure != false
            ? null
            : Aggregate?.AverageLatencyMilliseconds;
}
