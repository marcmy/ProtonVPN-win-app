using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ProtonVPN.Client.Common.UI.ServerHealth;
using Windows.Foundation;
using Windows.UI;

namespace ProtonVPN.Client.Common.UI.Controls;

public sealed class ServerHealthHistoryDetailsControl : ContentControl
{
    private const double PlotHeight = 84;
    private const double OutcomeRowHeight = 16;
    private const double TopInset = 7;

    public static readonly DependencyProperty SnapshotProperty =
        DependencyProperty.Register(
            nameof(Snapshot),
            typeof(ServerHealthSnapshot),
            typeof(ServerHealthHistoryDetailsControl),
            new PropertyMetadata(null, OnSnapshotChanged));

    private readonly StackPanel _layout = new() { Spacing = 8 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _latest = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _chartSection = new() { Spacing = 4 };
    private readonly TextBlock _chartTitle = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Canvas _chart = new() { Height = PlotHeight + OutcomeRowHeight, MaxWidth = 320 };
    private readonly TextBlock _chartLegend = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Text = "● Reply   ◆ Timeout   – No data",
    };

    private IReadOnlyList<ServerHealthGraphPoint> _points = [];

    public ServerHealthSnapshot? Snapshot
    {
        get => (ServerHealthSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public ServerHealthHistoryDetailsControl()
    {
        MaxWidth = 360;
        Padding = new Thickness(12);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = _layout;
        _layout.Children.Add(_summary);
        _layout.Children.Add(_latest);
        _layout.Children.Add(_chartSection);
        _layout.Children.Add(new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = "Round-trip ICMP to the physical entry endpoint. Tunnel and application traffic can behave differently.",
        });
        _chartSection.Children.Add(_chartTitle);
        _chartSection.Children.Add(_chart);
        _chartSection.Children.Add(_chartLegend);
        _chart.SizeChanged += (_, _) => RenderChart();
        ActualThemeChanged += (_, _) => RenderChart();
        Render();
    }

    private static void OnSnapshotChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((ServerHealthHistoryDetailsControl)dependencyObject).Render();

    private void Render()
    {
        if (Snapshot is not ServerHealthSnapshot snapshot)
        {
            _summary.Text = "Server health is unavailable";
            _latest.Text = string.Empty;
            _points = [];
        }
        else
        {
            ServerHealthPresentation presentation = ServerHealthPresentation.FromSnapshot(snapshot);
            string overview = presentation.GradeText == presentation.StatusText ||
                string.IsNullOrEmpty(presentation.StatusText)
                    ? presentation.GradeText
                    : $"{presentation.GradeText} • {presentation.StatusText}";
            string loss = presentation.IsLossEstablished
                ? $"{presentation.PacketLossText} • {presentation.LossCountText}"
                : string.IsNullOrEmpty(presentation.LossCountText) ? "—" : presentation.LossCountText;

            _points = ServerHealthGraphSeries.Create(snapshot);
            ServerHealthGraphPoint[] recent = _points.Where(point => point.IsInRollingWindow).ToArray();
            string timeRange = recent.Length < 2
                ? string.Empty
                : $" • {recent[0].CheckedAt.ToLocalTime():T}–{recent[^1].CheckedAt.ToLocalTime():T}";
            _summary.Text = $"{overview} • Ping {presentation.LatencyText}\n" +
                $"Recent ICMP loss  {loss}\n" +
                $"{presentation.ConfidenceText}{timeRange}\n" +
                $"RTT variation  {presentation.VariationText}";

            ServerHealthGraphPoint? latest = _points.FirstOrDefault(point => point.IsLatest);
            _latest.Text = (latest is null ? string.Empty : $"Latest check • {FormatCheck(latest)}\n") +
                $"{presentation.LastCheckedText}\n{presentation.RouteText}";
        }

        _latest.Visibility = string.IsNullOrEmpty(_latest.Text) ? Visibility.Collapsed : Visibility.Visible;
        _chartSection.Visibility = _points.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderChart();
    }

    private void RenderChart()
    {
        _chart.Children.Clear();
        if (_points.Count == 0 || _chart.ActualWidth <= 0)
        {
            return;
        }

        Brush replyBrush = GetThemeBrush("SignalSuccessColorBrush", Color.FromArgb(255, 29, 171, 131));
        Brush timeoutBrush = GetThemeBrush("SignalWarningColorBrush", Color.FromArgb(255, 245, 166, 35));
        Brush mutedBrush = GetThemeBrush("TextWeakColorBrush", Color.FromArgb(255, 120, 120, 130));
        double? measuredMaximum = null;
        foreach (ServerHealthGraphPoint point in _points)
        {
            if (point.Observations.Count == 0 && point.LatencyMilliseconds is double legacyRtt)
            {
                measuredMaximum = Math.Max(measuredMaximum ?? 0, legacyRtt);
            }
            foreach (ServerHealthObservation observation in point.Observations.Where(ServerHealthProbeMeasurement.IsValidReply))
            {
                measuredMaximum = Math.Max(measuredMaximum ?? 0, observation.RoundTripTimeMilliseconds!.Value);
            }
        }

        double maximumLatency = Math.Max(1, measuredMaximum ?? 0);
        _chartTitle.Text = measuredMaximum is null
            ? "No RTT samples • Oldest to newest"
            : $"Echo RTT (0–{Math.Ceiling(maximumLatency):0} ms) • Oldest to newest";

        Line separator = new()
        {
            X2 = _chart.ActualWidth,
            Stroke = mutedBrush,
            Opacity = 0.35,
            IsHitTestVisible = false,
        };
        Canvas.SetTop(separator, PlotHeight);
        _chart.Children.Add(separator);

        double columnWidth = _chart.ActualWidth / _points.Count;
        for (int index = 0; index < _points.Count; index++)
        {
            ServerHealthGraphPoint point = _points[index];
            double columnStart = index * columnWidth;
            Border column = new()
            {
                Width = columnWidth,
                Height = _chart.Height,
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = point.IsLatest ? new Thickness(1) : new Thickness(0),
                BorderBrush = mutedBrush,
                CornerRadius = new CornerRadius(3),
            };
            string description = FormatChartColumn(point);
            ToolTipService.SetToolTip(column, description);
            AutomationProperties.SetName(column, description.Replace('\n', ' '));
            Canvas.SetLeft(column, columnStart);
            _chart.Children.Add(column);

            double step = columnWidth / (point.Observations.Count + 1);
            double markerSize = Math.Min(7, Math.Max(1, step - 1));
            if (point.Observations.Count == 0)
            {
                AddMarker(columnStart + step / 2, point.LatencyMilliseconds, ServerHealthObservationOutcome.Unavailable,
                    markerSize, maximumLatency, mutedBrush, timeoutBrush, mutedBrush);
                continue;
            }

            for (int sample = 0; sample < point.Observations.Count; sample++)
            {
                ServerHealthObservation observation = point.Observations[sample];
                AddMarker(
                    columnStart + step * (sample + 1),
                    ServerHealthProbeMeasurement.IsValidReply(observation) ? observation.RoundTripTimeMilliseconds : null,
                    observation.Outcome,
                    markerSize,
                    maximumLatency,
                    point.IsInRollingWindow ? replyBrush : mutedBrush,
                    timeoutBrush,
                    mutedBrush);
            }
        }
    }

    private void AddMarker(
        double x,
        double? latency,
        ServerHealthObservationOutcome outcome,
        double size,
        double maximumLatency,
        Brush replyBrush,
        Brush timeoutBrush,
        Brush mutedBrush)
    {
        double y = PlotHeight + OutcomeRowHeight / 2;
        Shape marker;
        if (latency is double rtt)
        {
            y = PlotHeight - TopInset - rtt / maximumLatency * (PlotHeight - 2 * TopInset);
            marker = new Ellipse { Width = size, Height = size, Fill = replyBrush };
        }
        else if (outcome == ServerHealthObservationOutcome.TimedOut)
        {
            marker = new Polygon
            {
                Width = size,
                Height = size,
                Fill = timeoutBrush,
                Points = new PointCollection { new(size / 2, 0), new(size, size / 2), new(size / 2, size), new(0, size / 2) },
            };
        }
        else
        {
            marker = new Rectangle { Width = size, Height = 2, Fill = mutedBrush };
        }

        marker.IsHitTestVisible = false;
        Canvas.SetLeft(marker, x - marker.Width / 2);
        Canvas.SetTop(marker, y - marker.Height / 2);
        _chart.Children.Add(marker);
    }

    private static string FormatCheck(ServerHealthGraphPoint point)
    {
        ServerHealthProbeMeasurement measurement = point.Measurement;
        if (point.MissedSamples is int missed)
        {
            return ServerHealthPresentation.FormatLossCount(missed, point.TotalSamples) +
                (point.LatencyMilliseconds is double latency
                    ? $" • {ServerHealthPresentation.FormatLatency(latency)} avg RTT"
                    : " • No ICMP reply");
        }

        if (measurement.IsNoReply)
        {
            return "No ICMP reply";
        }

        if (point.LatencyMilliseconds is double estimate)
        {
            string source = measurement.IsDiscovery || point.Observations.Count == 0
                ? "RTT estimate"
                : "Incomplete check";
            return $"{source} • {ServerHealthPresentation.FormatLatency(estimate)}";
        }

        return measurement.UsedPhysicalRoute ? "No recorded echo outcomes" : "Route unavailable";
    }

    private static string FormatChartColumn(ServerHealthGraphPoint point)
    {
        string label = point.IsLatest ? "Latest check" : "Check";
        string summary = $"{label} • {point.CheckedAt.ToLocalTime():G}\n{FormatCheck(point)}";
        if (point.IsInRollingWindow)
        {
            summary += "\nIncluded in recent measurements";
        }

        if (point.Observations.Count > 0)
        {
            summary += "\nEchoes  " + string.Join(" • ", point.Observations.Select(observation =>
                observation.Outcome switch
                {
                    ServerHealthObservationOutcome.Reply when ServerHealthProbeMeasurement.IsValidReply(observation) =>
                        ServerHealthPresentation.FormatLatency(observation.RoundTripTimeMilliseconds),
                    ServerHealthObservationOutcome.TimedOut => "Timeout",
                    ServerHealthObservationOutcome.Unreachable => "Unreachable",
                    _ => "Unavailable",
                }));
        }

        return summary;
    }

    private static Brush GetThemeBrush(string resourceKey, Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallbackColor);
    }
}
