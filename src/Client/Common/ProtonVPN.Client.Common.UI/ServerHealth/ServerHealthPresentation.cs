namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthPresentation(
    string GradeText,
    int ActiveBarCount,
    string LatencyText,
    string PacketLossText,
    string LoadText,
    string ConfidenceText,
    string RouteText,
    string LastCheckedText)
{
    public static ServerHealthPresentation FromSnapshot(ServerHealthSnapshot snapshot)
    {
        if (snapshot.Aggregate is null)
        {
            string state = snapshot.IsRechecking ? "Rechecking…" : "Checking…";
            return new(
                state,
                0,
                "—",
                "—",
                "—",
                "Waiting for first check",
                snapshot.PendingError ?? "Physical adapter (direct)",
                state);
        }

        ServerHealthAggregate aggregate = snapshot.Aggregate;
        string confidence = aggregate.MeasurementCount >= 3
            ? "Based on 3 checks"
            : $"Based on {aggregate.MeasurementCount} of 3 checks";
        return new(
            aggregate.Grade.ToString(),
            aggregate.Grade switch
            {
                ServerHealthGrade.Excellent => 4,
                ServerHealthGrade.Good => 3,
                ServerHealthGrade.Fair => 2,
                _ => 1,
            },
            aggregate.AverageLatencyMilliseconds is null
                ? "—"
                : $"{aggregate.AverageLatencyMilliseconds.Value:0} ms",
            $"{aggregate.PacketLossPercent:0.#}%",
            $"{aggregate.ServerLoad:P0}",
            confidence,
            snapshot.LatestMeasurement?.UsedPhysicalRoute == true
                ? "Physical adapter (direct)"
                : snapshot.LatestMeasurement?.Error ?? "Route unavailable",
            snapshot.LatestMeasurement is null
                ? "Waiting for first check"
                : $"Updated {snapshot.LatestMeasurement.CheckedAt.ToLocalTime():T}");
    }
}
