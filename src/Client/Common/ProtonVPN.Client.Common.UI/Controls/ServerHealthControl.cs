/*
 * Copyright (c) 2026 Proton AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ProtonVPN.Client.Common.UI.Controls;

public interface IServerHealthSource
{
    string? HealthProbeAddress { get; }
}

public sealed class ServerHealthControl : Grid
{
    private static readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _delayBetweenSamples = TimeSpan.FromMilliseconds(100);
    private const int PROBE_TIMEOUT_IN_MILLISECONDS = 800;
    private const int PROBE_SAMPLE_COUNT = 4;

    private static readonly SemaphoreSlim _probeSlots = new(8, 8);
    private static readonly ConcurrentDictionary<string, ProbeCacheEntry> _probeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Task<ProbeMeasurement>>> _probesInProgress = new(StringComparer.OrdinalIgnoreCase);

    private readonly Border[] _bars;

    private CancellationTokenSource? _probeCancellationTokenSource;
    private ProbeMeasurement? _lastMeasurement;
    private string? _probeAddress;
    private double _serverLoad;
    private bool _isLoaded;

    public string? ProbeAddress
    {
        get => _probeAddress;
        set
        {
            string? normalizedValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_probeAddress, normalizedValue, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _probeAddress = normalizedValue;
            RestartProbeLoop();
        }
    }

    public double ServerLoad
    {
        get => _serverLoad;
        set
        {
            _serverLoad = Math.Clamp(value, 0, 1);
            if (_lastMeasurement is not null)
            {
                ApplyMeasurement(_lastMeasurement);
            }
        }
    }

    public ServerHealthControl()
    {
        Width = 24;
        Height = 16;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        ColumnSpacing = 2;
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        _bars =
        [
            CreateBar(4),
            CreateBar(7),
            CreateBar(10),
            CreateBar(13),
        ];

        for (int i = 0; i < _bars.Length; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(_bars[i], i);
            Children.Add(_bars[i]);
        }

        SetCheckingState();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static Border CreateBar(double height)
    {
        return new Border
        {
            Width = 4,
            Height = height,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(1),
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        RestartProbeLoop();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopProbeLoop();
    }

    private void RestartProbeLoop()
    {
        StopProbeLoop();
        _lastMeasurement = null;

        if (!_isLoaded || string.IsNullOrWhiteSpace(ProbeAddress))
        {
            SetUnavailableState("No probe address is available for this server.");
            return;
        }

        SetCheckingState();
        _probeCancellationTokenSource = new CancellationTokenSource();
        _ = RunProbeLoopAsync(_probeCancellationTokenSource.Token);
    }

    private void StopProbeLoop()
    {
        _probeCancellationTokenSource?.Cancel();
        _probeCancellationTokenSource?.Dispose();
        _probeCancellationTokenSource = null;
    }

    private async Task RunProbeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && ProbeAddress is not null)
            {
                ProbeMeasurement measurement = await GetMeasurementAsync(ProbeAddress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                _lastMeasurement = measurement;
                ApplyMeasurement(measurement);

                await Task.Delay(_refreshInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetUnavailableState("The health check could not be completed.");
            }
        }
    }

    private static async Task<ProbeMeasurement> GetMeasurementAsync(string address, CancellationToken cancellationToken)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        if (_probeCache.TryGetValue(address, out ProbeCacheEntry? cachedEntry) && utcNow - cachedEntry.CreatedAt <= _cacheLifetime)
        {
            return cachedEntry.Measurement;
        }

        Lazy<Task<ProbeMeasurement>> pendingProbe = _probesInProgress.GetOrAdd(
            address,
            static key => new Lazy<Task<ProbeMeasurement>>(
                () => ProbeAsync(key),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            ProbeMeasurement measurement = await pendingProbe.Value.WaitAsync(cancellationToken);
            _probeCache[address] = new ProbeCacheEntry(measurement, DateTimeOffset.UtcNow);
            return measurement;
        }
        finally
        {
            if (pendingProbe.IsValueCreated && pendingProbe.Value.IsCompleted)
            {
                _probesInProgress.TryRemove(address, out _);
            }
        }
    }

    private static async Task<ProbeMeasurement> ProbeAsync(string address)
    {
        await _probeSlots.WaitAsync();
        try
        {
            List<long> successfulRoundTrips = [];

            using Ping ping = new();
            for (int sampleIndex = 0; sampleIndex < PROBE_SAMPLE_COUNT; sampleIndex++)
            {
                try
                {
                    PingReply reply = await ping.SendPingAsync(address, PROBE_TIMEOUT_IN_MILLISECONDS);
                    if (reply.Status == IPStatus.Success)
                    {
                        successfulRoundTrips.Add(reply.RoundtripTime);
                    }
                }
                catch (Exception exception) when (exception is PingException or ArgumentException or InvalidOperationException)
                {
                }

                if (sampleIndex < PROBE_SAMPLE_COUNT - 1)
                {
                    await Task.Delay(_delayBetweenSamples);
                }
            }

            double? averageLatency = successfulRoundTrips.Count > 0
                ? successfulRoundTrips.Average()
                : null;
            double packetLossPercent = (PROBE_SAMPLE_COUNT - successfulRoundTrips.Count) * 100d / PROBE_SAMPLE_COUNT;

            return new ProbeMeasurement(
                averageLatency,
                packetLossPercent,
                successfulRoundTrips.Count,
                PROBE_SAMPLE_COUNT,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _probeSlots.Release();
        }
    }

    private void ApplyMeasurement(ProbeMeasurement measurement)
    {
        if (measurement.SuccessfulSamples == 0 || measurement.AverageLatencyMilliseconds is null)
        {
            SetUnavailableState(
                "No ICMP replies were received. The server may block ping; this does not necessarily mean it is offline.",
                measurement.CheckedAt);
            return;
        }

        double score = CalculateScore(
            measurement.AverageLatencyMilliseconds.Value,
            measurement.PacketLossPercent,
            ServerLoad);

        (string label, int activeBars, string brushKey, Color fallbackColor) = score switch
        {
            >= 85 => ("Excellent", 4, "SignalSuccessColorBrush", Color.FromArgb(255, 29, 171, 131)),
            >= 65 => ("Good", 3, "SignalSuccessColorBrush", Color.FromArgb(255, 29, 171, 131)),
            >= 40 => ("Fair", 2, "SignalWarningColorBrush", Color.FromArgb(255, 245, 166, 35)),
            _ => ("Poor", 1, "SignalDangerColorBrush", Color.FromArgb(255, 220, 65, 80)),
        };

        SetBars(activeBars, GetThemeBrush(brushKey, fallbackColor));

        string toolTip =
            $"Server health: {label}\n" +
            $"Latency: {measurement.AverageLatencyMilliseconds.Value:0} ms\n" +
            $"Packet loss: {measurement.PacketLossPercent:0}% ({measurement.SuccessfulSamples}/{measurement.TotalSamples} replies)\n" +
            $"Server load: {ServerLoad:P0}\n" +
            $"Last checked: {measurement.CheckedAt.ToLocalTime():T}";

        SetToolTip(toolTip);
    }

    private static double CalculateScore(double latencyMilliseconds, double packetLossPercent, double serverLoad)
    {
        double latencyScore = latencyMilliseconds switch
        {
            <= 40 => 100,
            <= 80 => 85,
            <= 140 => 65,
            <= 220 => 40,
            <= 350 => 20,
            _ => 5,
        };

        double reliabilityScore = Math.Clamp(100 - packetLossPercent * 2, 0, 100);
        double loadScore = 100 - Math.Clamp(serverLoad, 0, 1) * 100;
        double score = latencyScore * 0.45 + reliabilityScore * 0.45 + loadScore * 0.10;

        if (packetLossPercent >= 50)
        {
            return Math.Min(score, 39);
        }

        if (packetLossPercent >= 25)
        {
            return Math.Min(score, 64);
        }

        return score;
    }

    private void SetCheckingState()
    {
        SetBars(0, GetThemeBrush("TextWeakColorBrush", Color.FromArgb(255, 120, 120, 130)));
        SetToolTip("Server health: Checking…\nMeasuring latency and packet loss with 4 ICMP samples.");
    }

    private void SetUnavailableState(string reason, DateTimeOffset? checkedAt = null)
    {
        SetBars(0, GetThemeBrush("TextWeakColorBrush", Color.FromArgb(255, 120, 120, 130)));

        string toolTip = $"Server health: Unavailable\n{reason}\nServer load: {ServerLoad:P0}";
        if (checkedAt is not null)
        {
            toolTip += $"\nLast checked: {checkedAt.Value.ToLocalTime():T}";
        }

        SetToolTip(toolTip);
    }

    private void SetBars(int activeBarCount, Brush activeBrush)
    {
        Brush inactiveBrush = GetThemeBrush("TextWeakColorBrush", Color.FromArgb(255, 120, 120, 130));

        for (int i = 0; i < _bars.Length; i++)
        {
            bool isActive = i < activeBarCount;
            _bars[i].Background = isActive ? activeBrush : inactiveBrush;
            _bars[i].Opacity = isActive ? 1 : 0.2;
        }
    }

    private void SetToolTip(string text)
    {
        ToolTipService.SetToolTip(this, text);
        AutomationProperties.SetName(this, text.Replace('\n', ' '));
    }

    private static Brush GetThemeBrush(string resourceKey, Color fallbackColor)
    {
        try
        {
            if (Application.Current.Resources[resourceKey] is Brush brush)
            {
                return brush;
            }
        }
        catch (KeyNotFoundException)
        {
        }

        return new SolidColorBrush(fallbackColor);
    }

    private sealed record ProbeMeasurement(
        double? AverageLatencyMilliseconds,
        double PacketLossPercent,
        int SuccessfulSamples,
        int TotalSamples,
        DateTimeOffset CheckedAt);

    private sealed record ProbeCacheEntry(ProbeMeasurement Measurement, DateTimeOffset CreatedAt);
}
