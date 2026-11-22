using System;
using System.Linq;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public enum ServerHealthObservationOutcome
{
    Reply,
    TimedOut,
    Unreachable,
    Unavailable,
}

public sealed record ServerHealthObservation(
    ServerHealthObservationOutcome Outcome,
    double? RoundTripTimeMilliseconds,
    DateTimeOffset SentAt);

public sealed record ServerHealthProbeMeasurement(
    double? AverageLatencyMilliseconds,
    int SuccessfulSamples,
    int TotalSamples,
    DateTimeOffset CheckedAt,
    bool UsedPhysicalRoute,
    string? Error,
    double ServerLoad,
    bool WasRetried = false,
    bool IsConfirmedOutage = false,
    bool IsDiscovery = false,
    string? RouteContext = null,
    ServerHealthObservation[]? Observations = null)
{
    public const int FixedSampleCount = 4;

    public bool IsQualifiedFixedCheck =>
        !IsDiscovery && UsedPhysicalRoute && Observations is { Length: FixedSampleCount } &&
        TotalSamples == FixedSampleCount && Observations.Count(IsValidReply) == SuccessfulSamples &&
        Observations.All(sample => IsValidReply(sample) || sample?.Outcome == ServerHealthObservationOutcome.TimedOut);

    public bool IsNoReply =>
        UsedPhysicalRoute && Observations is { Length: > 0 } &&
        Observations.All(sample => sample?.Outcome == ServerHealthObservationOutcome.TimedOut);

    internal bool HasVerifiedEchoReply => UsedPhysicalRoute && Observations?.Any(IsValidReply) == true;

    public double PacketLossPercent => IsQualifiedFixedCheck
        ? (TotalSamples - SuccessfulSamples) * 100d / TotalSamples
        : 0;

    public bool IsCompleteFailure =>
        SuccessfulSamples <= 0 || AverageLatencyMilliseconds is null;

    internal static bool IsValidReply(ServerHealthObservation? sample) =>
        sample is { Outcome: ServerHealthObservationOutcome.Reply, RoundTripTimeMilliseconds: double rtt } &&
        double.IsFinite(rtt) && rtt >= 0;
}
