using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

internal sealed class FakeServerHealthClock : IServerHealthClock
{
    private TaskCompletionSource? _pendingDelay;

    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
    public List<TimeSpan> Delays { get; } = [];
    public bool BlockDelays { get; set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        UtcNow += delay;
        if (!BlockDelays)
        {
            return Task.CompletedTask;
        }

        _pendingDelay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => _pendingDelay.TrySetCanceled(cancellationToken));
        return _pendingDelay.Task;
    }

    public void CompleteDelay() => _pendingDelay?.TrySetResult();
    public void Advance(TimeSpan amount) => UtcNow += amount;
}

internal sealed class QueueServerHealthSource : IServerHealthSource
{
    private readonly Queue<Func<CancellationToken, Task<ServerHealthProbeMeasurement>>> _results = [];

    public required string HealthServerId { get; init; }
    public required string? HealthProbeAddress { get; init; }
    public double HealthServerLoad { get; set; }
    public int ProbeCount { get; private set; }

    public void Enqueue(ServerHealthProbeMeasurement measurement) =>
        _results.Enqueue(_ => Task.FromResult(measurement));

    public void Enqueue(Func<CancellationToken, Task<ServerHealthProbeMeasurement>> factory) =>
        _results.Enqueue(factory);

    public Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken cancellationToken)
    {
        ProbeCount++;
        return _results.Dequeue()(cancellationToken);
    }
}
