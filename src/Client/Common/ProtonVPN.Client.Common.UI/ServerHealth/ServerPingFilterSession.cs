using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerPingFilterOption(string Label, int? MaxLatencyMilliseconds)
{
    public override string ToString() => Label;
}

public sealed class ServerPingFilterSession : INotifyPropertyChanged
{
    private const int MAX_CONCURRENT_FILTER_PROBES = 4;

    public static ServerPingFilterSession Current { get; } = new();

    private readonly ServerHealthHistoryStore _historyStore = ServerHealthHistorySession.Current;
    private readonly SemaphoreSlim _filterProbeSlots =
        new(MAX_CONCURRENT_FILTER_PROBES, MAX_CONCURRENT_FILTER_PROBES);

    private ServerPingFilterOption _selectedOption;

    public IReadOnlyList<ServerPingFilterOption> Options { get; } =
    [
        new("All", null),
        new("≤ 150 ms", 150),
        new("≤ 100 ms", 100),
        new("≤ 75 ms", 75),
        new("≤ 50 ms", 50),
        new("≤ 25 ms", 25),
    ];

    public ServerPingFilterOption SelectedOption
    {
        get => _selectedOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_selectedOption == value)
            {
                return;
            }

            _selectedOption = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(MaxLatencyMilliseconds));
        }
    }

    public bool IsActive => SelectedOption.MaxLatencyMilliseconds is not null;

    public int? MaxLatencyMilliseconds => SelectedOption.MaxLatencyMilliseconds;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ServerPingFilterSession()
    {
        _selectedOption = Options[0];
    }

    public double? GetAverageLatencyMilliseconds(IServerHealthSource source)
    {
        string? probeAddress = source.HealthProbeAddress;
        if (string.IsNullOrWhiteSpace(probeAddress))
        {
            return null;
        }

        ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(source.HealthServerId, probeAddress);
        return _historyStore.GetSnapshot(key).Aggregate?.AverageLatencyMilliseconds;
    }

    public bool Matches(IServerHealthSource source)
    {
        int? maximumLatency = MaxLatencyMilliseconds;
        if (maximumLatency is null)
        {
            return true;
        }

        double? latency = GetAverageLatencyMilliseconds(source);
        return latency is not null && latency <= maximumLatency.Value;
    }

    public async Task<ServerHealthSnapshot> ProbeAsync(
        IServerHealthSource source,
        CancellationToken cancellationToken)
    {
        // Do not submit an entire country/search result to the shared health store at once.
        // Consumer cancellation does not abort already-started shared probes, so a bounded
        // admission window prevents stale filter work from building a large queue.
        await _filterProbeSlots.WaitAsync(cancellationToken);
        try
        {
            return await _historyStore.ProbeAsync(source, cancellationToken);
        }
        finally
        {
            _filterProbeSlots.Release();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new(propertyName));
    }
}
