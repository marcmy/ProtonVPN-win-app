using System;
using System.Collections.Generic;
using System.Linq;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public static class ServerHealthCalculator
{
    internal const int ScoreMeasurementCount = 6;
    private const int MinimumChecksForPublishedLoss = 3;
    internal static readonly TimeSpan MinimumFixedCheckSpacing = TimeSpan.FromSeconds(15);

    public static IReadOnlyList<ServerHealthProbeMeasurement> GetQualifiedWindow(
        IReadOnlyList<ServerHealthProbeMeasurement> retained) =>
        retained.Where(measurement => measurement.IsQualifiedFixedCheck).TakeLast(ScoreMeasurementCount).ToArray();

    public static ServerHealthAggregate Aggregate(
        IReadOnlyList<ServerHealthProbeMeasurement> retained, bool hasObservedReply = false)
    {
        if (retained.Count == 0)
        {
            throw new ArgumentException("At least one measurement is required.", nameof(retained));
        }

        IReadOnlyList<ServerHealthProbeMeasurement> fixedChecks = GetQualifiedWindow(retained);
        int total = fixedChecks.Sum(measurement => measurement.TotalSamples);
        int successful = fixedChecks.Sum(measurement => measurement.SuccessfulSamples);
        double loss = total == 0 ? 0 : (total - successful) * 100d / total;
        double[] roundTrips = fixedChecks.SelectMany(measurement => measurement.Observations!)
            .Where(ServerHealthProbeMeasurement.IsValidReply)
            .Select(sample => sample.RoundTripTimeMilliseconds!.Value)
            .Order()
            .ToArray();
        double? median = roundTrips.Length == 0 ? null : Median(roundTrips);
        double? latency = roundTrips.Length >= 3 ? median : retained
            .LastOrDefault(measurement => measurement.UsedPhysicalRoute && measurement.AverageLatencyMilliseconds is not null)
            ?.AverageLatencyMilliseconds;
        double? variation = roundTrips.Length >= 8 && fixedChecks.Count(check => check.SuccessfulSamples > 0) >= 2
            ? Median(roundTrips.Select(roundTrip => Math.Abs(roundTrip - median!.Value)).Order().ToArray())
            : null;
        double load = Math.Clamp(retained[^1].ServerLoad, 0, 1);
        hasObservedReply |= retained.Any(measurement => measurement.HasVerifiedEchoReply);
        bool isLossEstablished = hasObservedReply && HasSeparatedChecks(fixedChecks);
        double score = CalculateScore(latency, loss);
        ServerHealthGrade grade = score switch
        {
            >= 85 => ServerHealthGrade.Excellent,
            >= 65 => ServerHealthGrade.Good,
            >= 40 => ServerHealthGrade.Fair,
            _ => ServerHealthGrade.Poor,
        };

        return new(latency, loss, successful, total, load, score, grade, fixedChecks.Count,
            isLossEstablished, latency is not null && roundTrips.Length < 3, variation, hasObservedReply);
    }

    private static bool HasSeparatedChecks(IReadOnlyList<ServerHealthProbeMeasurement> checks)
    {
        int separated = 0;
        DateTimeOffset? previous = null;
        foreach (DateTimeOffset checkedAt in checks.Select(check => check.CheckedAt).Order())
        {
            if (previous is null || checkedAt - previous.Value >= MinimumFixedCheckSpacing)
            {
                if (++separated >= MinimumChecksForPublishedLoss)
                {
                    return true;
                }
                previous = checkedAt;
            }
        }
        return false;
    }

    private static double Median(double[] ordered) => ordered.Length % 2 == 0
        ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2
        : ordered[ordered.Length / 2];

    private static double CalculateScore(double? latency, double loss)
    {
        double latencyScore = latency switch
        {
            null => 0,
            <= 40 => 100,
            <= 80 => 85,
            <= 140 => 65,
            <= 220 => 40,
            <= 350 => 20,
            _ => 5,
        };
        double reliabilityScore = Math.Clamp(100 - loss * 2, 0, 100);
        double score = (latencyScore + reliabilityScore) / 2;
        return loss switch
        {
            >= 50 => Math.Min(score, 39),
            >= 25 => Math.Min(score, 64),
            > 0 => Math.Min(score, 84),
            _ => score,
        };
    }
}
