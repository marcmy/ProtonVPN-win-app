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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProtonVPN.Client.Common.UI.ServerHealth;
using Windows.UI;

namespace ProtonVPN.Client.Common.UI.Controls;

public sealed class ServerHealthControl : Grid
{
    private static readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(60);

    private readonly Border[] _bars;
    private readonly ServerHealthHistoryStore _historyStore = ServerHealthHistorySession.Current;
    private readonly ServerHealthHistoryDetailsControl _detailsControl = new();

    private CancellationTokenSource? _probeCancellationTokenSource;
    private ServerHealthSnapshot? _snapshot;
    private ServerHealthHistoryKey? _historyKey;
    private IServerHealthSource? _probeSource;
    private double _serverLoad;
    private bool _isLoaded;

    public IServerHealthSource? ProbeSource
    {
        get => _probeSource;
        set
        {
            if (ReferenceEquals(_probeSource, value))
            {
                return;
            }

            _probeSource = value;
            RestartProbeLoop();
        }
    }

    public double ServerLoad
    {
        get => _serverLoad;
        set => _serverLoad = Math.Clamp(value, 0, 1);
    }

    public ServerHealthControl()
    {
        Width = 24;
        Height = 16;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        ColumnSpacing = 2;
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        ToolTipService.SetToolTip(this, _detailsControl);

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

    private static Border CreateBar(double height) =>
        new()
        {
            Width = 4,
            Height = height,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(1),
        };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _historyStore.SnapshotChanged += OnSnapshotChanged;
        RestartProbeLoop();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _historyStore.SnapshotChanged -= OnSnapshotChanged;
        StopProbeLoop();
    }

    private void RestartProbeLoop()
    {
        StopProbeLoop();
        RestoreSnapshot();

        if (!_isLoaded || ProbeSource is null || _historyKey is null)
        {
            return;
        }

        _probeCancellationTokenSource = new CancellationTokenSource();
        _ = RunProbeLoopAsync(_probeCancellationTokenSource.Token);
    }

    private void StopProbeLoop()
    {
        _probeCancellationTokenSource?.Cancel();
        _probeCancellationTokenSource?.Dispose();
        _probeCancellationTokenSource = null;
    }

    private bool TryGetHistoryKey(out ServerHealthHistoryKey key)
    {
        IServerHealthSource? source = ProbeSource;
        if (source is null || string.IsNullOrWhiteSpace(source.HealthProbeAddress))
        {
            key = default;
            return false;
        }

        key = ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress);
        return true;
    }

    private void RestoreSnapshot()
    {
        if (!TryGetHistoryKey(out ServerHealthHistoryKey key))
        {
            _historyKey = null;
            _snapshot = null;
            SetUnavailableState("No probe address is available for this server.");
            return;
        }

        _historyKey = key;
        ApplySnapshot(_historyStore.GetSnapshot(key));
    }

    private async Task RunProbeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IServerHealthSource? source = ProbeSource;
                if (source is null || string.IsNullOrWhiteSpace(source.HealthProbeAddress))
                {
                    return;
                }

                ApplySnapshot(await _historyStore.ProbeAsync(source, cancellationToken));
                await Task.Delay(_refreshInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetUnavailableState("The direct health check could not be completed.");
            }
        }
    }

    private void ApplySnapshot(ServerHealthSnapshot snapshot)
    {
        _snapshot = snapshot;
        _detailsControl.Snapshot = snapshot;
        ToolTipService.SetToolTip(this, _detailsControl);
        ServerHealthPresentation presentation = ServerHealthPresentation.FromSnapshot(snapshot);

        if (snapshot.Aggregate is null)
        {
            SetBars(0, GetThemeBrush("TextWeakColorBrush", Color.FromArgb(255, 120, 120, 130)));
        }
        else
        {
            (string resourceKey, Color fallback) = snapshot.Aggregate.Grade switch
            {
                ServerHealthGrade.Fair => ("SignalWarningColorBrush", Color.FromArgb(255, 245, 166, 35)),
                ServerHealthGrade.Poor => ("SignalDangerColorBrush", Color.FromArgb(255, 220, 65, 80)),
                _ => ("SignalSuccessColorBrush", Color.FromArgb(255, 29, 171, 131)),
            };
            SetBars(presentation.ActiveBarCount, GetThemeBrush(resourceKey, fallback));
        }

        AutomationProperties.SetName(
            this,
            $"Server health {presentation.GradeText}; " +
            $"latency {presentation.LatencyText}; " +
            $"packet loss {presentation.PacketLossText}; " +
            presentation.ConfidenceText);
    }

    private void OnSnapshotChanged(object? sender, ServerHealthSnapshotChangedEventArgs e)
    {
        if (_historyKey is not ServerHealthHistoryKey key || e.Snapshot.Key != key)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isLoaded && _historyKey == e.Snapshot.Key)
            {
                ApplySnapshot(e.Snapshot);
            }
        });
    }

    private void SetCheckingState()
    {
        SetBars(0, GetThemeBrush("TextWeakColorBrush", Color.FromArgb(255, 120, 120, 130)));
        _detailsControl.Snapshot = null;
        ToolTipService.SetToolTip(this, _detailsControl);
        AutomationProperties.SetName(this, "Server health checking");
    }

    private void SetUnavailableState(string reason)
    {
        SetBars(0, GetThemeBrush("TextWeakColorBrush", Color.FromArgb(255, 120, 120, 130)));
        string text = $"Server health: Unavailable\n{reason}\nServer load: {ServerLoad:P0}";
        ToolTipService.SetToolTip(this, text);
        AutomationProperties.SetName(this, text.Replace('\n', ' '));
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
}
