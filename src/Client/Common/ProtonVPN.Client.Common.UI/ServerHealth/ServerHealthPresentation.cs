namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthPresentation(
    string GradeText,
    int ActiveBarCount,
    string LatencyText,
    string PacketLossText,
    string LoadText,
    string ConfidenceText,
    string RouteText,
    string LastCheckedText,
    string LossCountText = "",
    string VariationText = "—",
    string StatusText = "",
    bool IsLossEstablished = false)
{
    public static ServerHealthPresentation FromSnapshot(ServerHealthSnapshot snapshot)
    {
        ServerHealthProbeMeasurement? latest = snapshot.LatestMeasurement;
        ServerHealthAggregate? aggregate = snapshot.Aggregate;
        bool isSaved = snapshot.IsCached || snapshot.IsStale;
        bool hasCurrentLoss = !isSaved && latest?.UsedPhysicalRoute == true && aggregate?.HasLossEvidence == true;
        bool isEstablished = hasCurrentLoss && aggregate!.IsLossEstablished;
        string status = GetStatus(snapshot);
        string confidence = hasCurrentLoss
            ? $"Last {aggregate!.MeasurementCount} fixed {(aggregate.MeasurementCount == 1 ? "check" : "checks")}"
            : isSaved ? "Saved measurement" : "Waiting for comparable checks";
        bool showGrade = isEstablished && latest?.IsCompleteFailure == false;
        double? latency = aggregate?.AverageLatencyMilliseconds ?? snapshot.SavedLatencyMilliseconds;
        string latencyText = FormatLatency(latency);
        if (!isSaved && latest?.IsCompleteFailure == true && latency is not null)
        {
            latencyText += " · last known";
        }
        return new(
            showGrade ? aggregate!.Grade.ToString() : status,
            showGrade ? aggregate!.Grade switch
            {
                ServerHealthGrade.Excellent => 4,
                ServerHealthGrade.Good => 3,
                ServerHealthGrade.Fair => 2,
                _ => 1,
            } : 0,
            latencyText,
            isEstablished ? $"{aggregate!.PacketLossPercent:0}%" : "—",
            aggregate is null ? "—" : $"{aggregate.ServerLoad:P0}",
            confidence,
            latest?.UsedPhysicalRoute == true ? "Direct ICMP · physical adapter" : latest?.Error ?? snapshot.PendingError ?? "Direct ICMP",
            latest is null ? status
                : $"{(isSaved ? "Saved" : "Checked")} {latest.CheckedAt.ToLocalTime():g}",
            hasCurrentLoss ? FormatLossCount(aggregate!.MissedSamples, aggregate.TotalSamples) : string.Empty,
            !isSaved ? FormatLatency(aggregate?.VariationMilliseconds) : "—",
            status,
            isEstablished);
    }

    public static string FormatLossCount(int missed, int total) => $"{missed} of {total} missed";

    public static string FormatLatency(double? latency) => latency is double value ? $"{value:0} ms" : "—";

    private static string GetStatus(ServerHealthSnapshot snapshot)
    {
        if (snapshot.IsCached || snapshot.IsStale)
        {
            return snapshot.IsChecking ? "Refreshing saved measurement" : "Saved";
        }
        if (snapshot.LatestMeasurement is not { } latest)
        {
            return snapshot.IsChecking ? "Checking…" : "Not measured";
        }
        if (latest.IsNoReply)
        {
            return "No ICMP reply";
        }
        if (latest.IsCompleteFailure)
        {
            return "Check unavailable";
        }
        if (snapshot.IsChecking)
        {
            return "Refreshing…";
        }
        if (!latest.IsDiscovery && latest.Observations is not null && !latest.IsQualifiedFixedCheck)
        {
            return "Check incomplete";
        }
        return snapshot.Aggregate?.IsLatencyEstimate == true ? "Estimate" :
            snapshot.Aggregate?.IsLossEstablished == true ? string.Empty : "Sampling";
    }
}
