using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record SavedServerHealth(ServerHealthHistoryKey Key, ServerHealthProbeMeasurement Measurement);

internal static class ServerHealthDiskCache
{
    internal const int MaximumEntries = 4096;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    internal static IReadOnlyList<SavedServerHealth> Load(string path, DateTimeOffset now)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length > 2 * 1024 * 1024) { return []; }
            return (JsonSerializer.Deserialize<List<SavedServerHealth>>(File.ReadAllText(path)) ?? [])
                .Where(item => item is not null && item.Measurement is not null &&
                    !string.IsNullOrWhiteSpace(item.Key.ServerId) && item.Key.ServerId.Length <= 256 &&
                    IPAddress.TryParse(item.Key.ProbeAddress, out _) &&
                    item.Measurement.CheckedAt <= now.AddMinutes(1) && item.Measurement.CheckedAt >= now - Retention &&
                    item.Measurement.TotalSamples is > 0 and <= 64 && item.Measurement.SuccessfulSamples >= 0 &&
                    item.Measurement.SuccessfulSamples <= item.Measurement.TotalSamples &&
                    (item.Measurement.AverageLatencyMilliseconds is null ||
                        (double.IsFinite(item.Measurement.AverageLatencyMilliseconds.Value) && item.Measurement.AverageLatencyMilliseconds >= 0)) &&
                    double.IsFinite(item.Measurement.ServerLoad) && item.Measurement.ServerLoad is >= 0 and <= 1)
                .OrderByDescending(item => item.Measurement.CheckedAt)
                .DistinctBy(item => ServerHealthHistoryKey.Create(item.Key.ServerId, item.Key.ProbeAddress))
                .Take(MaximumEntries).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return []; } // Cached telemetry must never prevent startup or change VPN behavior.
    }

    internal static void Save(string path, IEnumerable<SavedServerHealth> measurements)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(measurements.OrderByDescending(item => item.Measurement.CheckedAt).Take(MaximumEntries)));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        finally
        {
            try { if (File.Exists(temporary)) { File.Delete(temporary); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
