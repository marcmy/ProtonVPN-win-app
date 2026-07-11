using System;
using System.Collections.Generic;
using System.Linq;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public static class ServerHealthCalculator
{
    private const int SCORE_MEASUREMENT_COUNT = 3;

    public static ServerHealthAggregate Aggregate(
        IReadOnlyList<ServerHealthProbeMeasurement> retained)
    {
        if (retained.Count == 0)
        {
            throw new ArgumentException("At least one measurement is required.", nameof(retained));
        }

        ServerHealthProbeMeasurement[] measurements = retained
            .TakeLast(SCORE_MEASUREMENT_COUNT)
            .ToArray();
        int total = measurements.Sum(measurement => Math.Max(0, measurement.TotalSamples));
        int successful = measurements.Sum(measurement => Math.Clamp(
            measurement.SuccessfulSamples,
            0,
            Math.Max(0, measurement.TotalSamples)));
        double loss = total == 0 ? 100 : (total - successful) * 100d / total;
        ServerHealthProbeMeasurement[] latencyMeasurements = measurements
            .Where(measurement => measurement.AverageLatencyMilliseconds is not null && measurement.SuccessfulSamples > 0)
            .ToArray();
        int latencySamples = latencyMeasurements.Sum(measurement => Math.Clamp(
            measurement.SuccessfulSamples,
            0,
            Math.Max(0, measurement.TotalSamples)));
        double weightedLatency = latencyMeasurements.Sum(measurement =>
            measurement.AverageLatencyMilliseconds!.Value *
            Math.Clamp(measurement.SuccessfulSamples, 0, Math.Max(0, measurement.TotalSamples)));
        double? latency = latencySamples == 0 ? null : weightedLatency / latencySamples;
        double load = Math.Clamp(measurements[^1].ServerLoad, 0, 1);
        double score = CalculateScore(latency, loss, load);
        ServerHealthGrade grade = score switch
        {
            >= 85 => ServerHealthGrade.Excellent,
            >= 65 => ServerHealthGrade.Good,
            >= 40 => ServerHealthGrade.Fair,
            _ => ServerHealthGrade.Poor,
        };

        return new(latency, loss, successful, total, load, score, grade, measurements.Length);
    }

    public static double CalculateScore(double? latency, double loss, double load)
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
        double loadScore = 100 - Math.Clamp(load, 0, 1) * 100;
        double score = latencyScore * 0.45 + reliabilityScore * 0.45 + loadScore * 0.10;
        if (loss >= 50)
        {
            return Math.Min(score, 39);
        }
        if (loss >= 25)
        {
            return Math.Min(score, 64);
        }
        return score;
    }
}
