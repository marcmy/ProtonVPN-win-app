using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ProtonVPN.Client.Common.UI.ServerHealth;
using Windows.Foundation;
using Windows.UI;

namespace ProtonVPN.Client.Common.UI.Controls;

public sealed class ServerHealthHistoryDetailsControl : Border
{
    public static readonly DependencyProperty SnapshotProperty =
        DependencyProperty.Register(
            nameof(Snapshot),
            typeof(ServerHealthSnapshot),
            typeof(ServerHealthHistoryDetailsControl),
            new PropertyMetadata(null, OnSnapshotChanged));

    private readonly Grid _layout = new() { RowSpacing = 8 };
    private readonly TextBlock _summary = new();
    private readonly TextBlock _latest = new();
    private readonly Canvas _chart = new() { Width = 320, Height = 120 };

    public ServerHealthSnapshot? Snapshot
    {
        get => (ServerHealthSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public ServerHealthHistoryDetailsControl()
    {
        Width = 344;
        Padding = new Thickness(12);
        Child = _layout;
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(_summary, 0);
        Grid.SetRow(_chart, 1);
        Grid.SetRow(_latest, 2);
        _layout.Children.Add(_summary);
        _layout.Children.Add(_chart);
        _layout.Children.Add(_latest);
        Render();
    }

    private static void OnSnapshotChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((ServerHealthHistoryDetailsControl)dependencyObject).Render();

    private void Render()
    {
        _chart.Children.Clear();
        if (Snapshot is not ServerHealthSnapshot snapshot)
        {
            _summary.Text = "Server health: Checking…";
            _latest.Text = "Waiting for the first completed check.";
            return;
        }

        ServerHealthPresentation presentation = ServerHealthPresentation.FromSnapshot(snapshot);
        _summary.Text =
            $"{presentation.GradeText} • {presentation.LatencyText} • " +
            $"{presentation.PacketLossText} loss • {presentation.ConfidenceText}";
        _latest.Text = FormatLatest(snapshot, presentation);
        IReadOnlyList<ServerHealthGraphPoint> points = ServerHealthGraphSeries.Create(snapshot);
        if (points.Count == 0)
        {
            return;
        }

        DateTimeOffset start = points[0].CheckedAt;
        DateTimeOffset end = points[^1].CheckedAt;
        double seconds = Math.Max(1, (end - start).TotalSeconds);
        double maximumLatency = Math.Max(
            1,
            points.Where(point => point.LatencyMilliseconds is not null)
                .Select(point => point.LatencyMilliseconds!.Value)
                .DefaultIfEmpty(1)
                .Max());
        Brush latencyBrush = GetThemeBrush(
            "SignalSuccessColorBrush",
            Color.FromArgb(255, 29, 171, 131));
        Polyline loadLine = new()
        {
            Stroke = GetThemeBrush(
                "TextWeakColorBrush",
                Color.FromArgb(255, 120, 120, 130)),
            StrokeThickness = 1,
            Opacity = 0.45,
        };
        _chart.Children.Add(loadLine);
        Polyline? latencySegment = null;

        foreach (ServerHealthGraphPoint point in points)
        {
            double x = (point.CheckedAt - start).TotalSeconds / seconds * _chart.Width;
            if (point.LatencyMilliseconds is double latency)
            {
                if (latencySegment is null)
                {
                    latencySegment = new()
                    {
                        Stroke = latencyBrush,
                        StrokeThickness = 2,
                    };
                    _chart.Children.Add(latencySegment);
                }

                double y = _chart.Height - latency / maximumLatency * (_chart.Height - 12);
                latencySegment.Points.Add(new Point(x, y));
            }
            else
            {
                latencySegment = null;
            }

            loadLine.Points.Add(new Point(
                x,
                _chart.Height - point.ServerLoad * (_chart.Height - 12)));
            Ellipse marker = new()
            {
                Width = point.IsScoreDriver ? 8 : 6,
                Height = point.IsScoreDriver ? 8 : 6,
                Fill = GetThemeBrush(
                    point.PacketLossPercent > 0
                        ? "SignalWarningColorBrush"
                        : "SignalSuccessColorBrush",
                    point.PacketLossPercent > 0
                        ? Color.FromArgb(255, 245, 166, 35)
                        : Color.FromArgb(255, 29, 171, 131)),
            };
            Canvas.SetLeft(marker, Math.Clamp(x - marker.Width / 2, 0, _chart.Width - marker.Width));
            Canvas.SetTop(
                marker,
                point.IsConfirmedOutage
                    ? 0
                    : Math.Clamp(
                        _chart.Height - point.PacketLossPercent / 100 * _chart.Height - marker.Height / 2,
                        0,
                        _chart.Height - marker.Height));
            ToolTipService.SetToolTip(marker, FormatPoint(point));
            _chart.Children.Add(marker);
        }
    }

    private static string FormatLatest(
        ServerHealthSnapshot snapshot,
        ServerHealthPresentation presentation)
    {
        ServerHealthProbeMeasurement? latest = snapshot.LatestMeasurement;
        if (latest is null)
        {
            return snapshot.IsRechecking
                ? $"Rechecking after failure: {snapshot.PendingError}"
                : "Waiting for the first completed check.";
        }

        string prefix = snapshot.IsRechecking
            ? $"Rechecking after failure: {snapshot.PendingError}\n"
            : string.Empty;
        string latency = latest.AverageLatencyMilliseconds is null
            ? "—"
            : $"{latest.AverageLatencyMilliseconds.Value:0} ms";
        return prefix +
            $"Latest: {latency}, {latest.PacketLossPercent:0.#}% loss " +
            $"({latest.SuccessfulSamples}/{latest.TotalSamples} replies), " +
            $"{latest.CheckedAt.ToLocalTime():T}" +
            (latest.WasRetried ? " • retry" : string.Empty) +
            $"\nRoute: {presentation.RouteText}";
    }

    private static string FormatPoint(ServerHealthGraphPoint point)
    {
        string latency = point.LatencyMilliseconds is null
            ? "—"
            : $"{point.LatencyMilliseconds.Value:0} ms";
        return
            $"{point.CheckedAt.ToLocalTime():T}\n" +
            $"Latency: {latency}\n" +
            $"Loss: {point.PacketLossPercent:0.#}% " +
            $"({point.SuccessfulSamples}/{point.TotalSamples})\n" +
            $"Load: {point.ServerLoad:P0}\n" +
            (point.IsConfirmedOutage ? "Confirmed outage\n" : string.Empty) +
            (point.WasRetried ? "Retried check" : "Normal check");
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
