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
            .OrderBy(m => m.CheckedAt)
            .TakeLast(SCORE_MEASUREMENT_COUNT)
            .ToArray();
        int successful = measurements.Sum(m => Math.Max(0, m.SuccessfulSamples));
        int total = measurements.Sum(m => Math.Max(0, m.TotalSamples));
        double loss = total == 0 ? 100 : (total - successful) * 100d / total;
        double weightedLatency = measurements
            .Where(m => m.AverageLatencyMilliseconds is not null && m.SuccessfulSamples > 0)
            .Sum(m => m.AverageLatencyMilliseconds!.Value * m.SuccessfulSamples);
        double? latency = successful == 0 ? null : weightedLatency / successful;
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
