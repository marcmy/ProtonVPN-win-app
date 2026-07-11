# Server Health History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace latest-probe server health with a shared, session-wide three-check history, confirmed-outage retry behavior, and a reusable ten-minute hover graph for both candidate rows and the connected-server panel.

**Architecture:** Put the scoring, retained measurements, retry/coalescing coordinator, and graph-series projection in the existing `ProtonVPN.Client.Common.UI` assembly so both current health surfaces consume exactly the same immutable snapshots. A session singleton owns one `ServerHealthHistoryStore`; UI controls subscribe by a composite server-ID/address key, while the privileged VPN service remains stateless and unchanged.

**Tech Stack:** C# 12, .NET 8, WinUI 3, CommunityToolkit.Mvvm, MSTest, NSubstitute, existing Proton VPN IPC/service probe.

## Global Constraints

- Keep normal probes at exactly 4 ICMP samples.
- Keep connected-server refresh at 30 seconds and visible candidate-row refresh at 60 seconds.
- Score from only the newest 3 recorded batches; pool attempted replies and successful replies across those batches.
- Retain graph history for 10 minutes and expire a key after 10 minutes without a recorded measurement.
- Show provisional results immediately as `Based on 1 of 3 checks` or `Based on 2 of 3 checks`.
- On a complete failure, retry exactly once after 5 seconds; record one synthetic 4-attempt/0-reply batch only when the retry also fails completely.
- Record partial loss immediately and do not retry it.
- Preserve the existing latency buckets, 45% latency / 45% reliability / 10% load weights, packet-loss caps, and grade thresholds.
- Use the newest known load value, but use reply-weighted pooled latency and pooled packet loss for the visible numbers.
- Share history between search rows and the connected panel when both resolve to the same stable server ID and normalized probe address.
- Preserve history across row unloading and navigation, but never persist it across application restarts.
- Generate no extra periodic probes for history or graphing.
- Do not change the privileged service’s direct physical-adapter route, WFP filters, IPC contract, timeout, or sample loop.
- Cancellation from a disposed view or application shutdown must not create synthetic packet loss.
- Confirmed outages have no invented latency; when no successful replies exist in the scoring window, display latency as `—` and grade the result Poor.
- All UI updates raised from the shared store must be marshalled by the consuming view/control to the UI thread.

---

## File Map

### New shared health-domain files

- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/IServerHealthSource.cs` — source identity, load, address, and probe callback contract.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/IServerHealthClock.cs` — injectable UTC clock and delay abstraction.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGrade.cs` — grade enum.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistoryKey.cs` — normalized composite identity.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthProbeMeasurement.cs` — one recorded logical batch.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthAggregate.cs` — newest-three pooled result.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthSnapshot.cs` — immutable retained history and transient checking state.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs` — pooled aggregation and unchanged grade formula.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs` — shared labels, bar count, confidence, and formatted values.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistoryStore.cs` — retention, expiry, retry, concurrency, and notifications.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistorySession.cs` — one application-session store.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGraphSeries.cs` — graph-ready points and newest-three markers.

### New reusable UI file

- `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthHistoryDetailsControl.cs` — tooltip content and ten-minute graph.

### New tests

- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj`
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthHistoryStoreTest.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthGraphSeriesTest.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthTestDoubles.cs`

### Existing files to modify

- `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/Custom/ServerConnectionRowButton.cs`
- `src/Client/ProtonVPN.Client/Models/Connections/ServerLocationItemBase.cs`
- `src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderViewModel.cs`
- `src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderView.xaml`

---

### Task 1: Establish the shared health model, pooled calculator, and presentation contract

**Files:**
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/IServerHealthSource.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/IServerHealthClock.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGrade.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistoryKey.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthProbeMeasurement.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthAggregate.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthSnapshot.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs`
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs:28-47` — remove the in-file interface and measurement record after moving them.
- Modify: `src/Client/ProtonVPN.Client/Models/Connections/ServerLocationItemBase.cs:45-130` — implement stable health identity/load properties and update measurement construction.

**Interfaces:**
- Produces:
  - `IServerHealthSource.HealthServerId`
  - `IServerHealthSource.HealthProbeAddress`
  - `IServerHealthSource.HealthServerLoad`
  - `IServerHealthSource.ProbeHealthAsync(CancellationToken)`
  - `ServerHealthHistoryKey.Create(string serverId, string probeAddress)`
  - `ServerHealthCalculator.Aggregate(IReadOnlyList<ServerHealthProbeMeasurement>)`
  - `ServerHealthPresentation.FromSnapshot(ServerHealthSnapshot)`

- [ ] **Step 1: Create the MSTest project**

Create `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <PreserveCompilationContext>true</PreserveCompilationContext>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWinUI>true</UseWinUI>
    <OutputPath>..\..\..\bin</OutputPath>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\..\..\GlobalAssemblyInfo.cs"
             Link="Properties\GlobalAssemblyInfo.cs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="MSTest.TestAdapter" />
    <PackageReference Include="MSTest.TestFramework" />
    <PackageReference Include="NSubstitute" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\ProtonVPN.Client.Common.UI\ProtonVPN.Client.Common.UI.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write failing pooled-calculation tests**

Create `ServerHealthCalculatorTest.cs` with tests covering:

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthCalculatorTest
{
    [TestMethod]
    public void Aggregate_OneMissAcrossThreeBatches_UsesOneOfTwelveLoss()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(40, 4, 4, 0.20, 0),
            Measurement(42, 3, 4, 0.25, 30),
            Measurement(41, 4, 4, 0.30, 60),
        ];

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

        Assert.AreEqual(11, result.SuccessfulSamples);
        Assert.AreEqual(12, result.TotalSamples);
        Assert.AreEqual(100d / 12d, result.PacketLossPercent, 0.001);
        Assert.AreEqual(3, result.MeasurementCount);
    }

    [TestMethod]
    public void Aggregate_WeightsLatencyBySuccessfulReplies()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(20, 4, 4, 0.20, 0),
            Measurement(100, 1, 4, 0.20, 30),
        ];

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

        Assert.AreEqual(36, result.AverageLatencyMilliseconds!.Value, 0.001);
    }

    [TestMethod]
    public void Aggregate_UsesOnlyNewestThreeBatchesAndNewestLoad()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(null, 0, 4, 0.10, 0, true),
            Measurement(30, 4, 4, 0.20, 30),
            Measurement(31, 4, 4, 0.30, 60),
            Measurement(32, 4, 4, 0.40, 90),
        ];

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

        Assert.AreEqual(12, result.SuccessfulSamples);
        Assert.AreEqual(12, result.TotalSamples);
        Assert.AreEqual(0, result.PacketLossPercent, 0.001);
        Assert.AreEqual(0.40, result.ServerLoad, 0.001);
    }

    [TestMethod]
    public void Aggregate_ConfirmedOutageWithoutReplies_IsPoorAndDoesNotInventLatency()
    {
        ServerHealthProbeMeasurement outage =
            Measurement(null, 0, 4, 0.20, 0, true);

        ServerHealthAggregate result = ServerHealthCalculator.Aggregate([outage]);

        Assert.IsNull(result.AverageLatencyMilliseconds);
        Assert.AreEqual(100, result.PacketLossPercent);
        Assert.AreEqual(ServerHealthGrade.Poor, result.Grade);
    }

    [TestMethod]
    public void Aggregate_ThreeCleanFastBatches_CanBeExcellent()
    {
        ServerHealthProbeMeasurement[] measurements =
        [
            Measurement(20, 4, 4, 0.10, 0),
            Measurement(22, 4, 4, 0.10, 30),
            Measurement(21, 4, 4, 0.10, 60),
        ];

        Assert.AreEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate(measurements).Grade);
    }

    [TestMethod]
    public void Aggregate_ConfirmedOutage_RemainsUntilThreeLaterBatchesReplaceIt()
    {
        ServerHealthProbeMeasurement outage =
            Measurement(null, 0, 4, 0.10, 0, true);
        ServerHealthProbeMeasurement first = Measurement(20, 4, 4, 0.10, 30);
        ServerHealthProbeMeasurement second = Measurement(20, 4, 4, 0.10, 60);
        ServerHealthProbeMeasurement third = Measurement(20, 4, 4, 0.10, 90);

        Assert.AreNotEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate([outage, first]).Grade);
        Assert.AreNotEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate([outage, first, second]).Grade);
        Assert.AreEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate([outage, first, second, third]).Grade);
    }

    [DataTestMethod]
    [DataRow(1, "Based on 1 of 3 checks")]
    [DataRow(2, "Based on 2 of 3 checks")]
    [DataRow(3, "Based on 3 checks")]
    public void FromSnapshot_FormatsConfidenceForAvailableBatchCount(
        int count,
        string expected)
    {
        ServerHealthProbeMeasurement[] measurements = Enumerable.Range(0, count)
            .Select(i => Measurement(40, 4, 4, 0.25, i * 30))
            .ToArray();
        ServerHealthSnapshot snapshot = ServerHealthSnapshot.CreateRecorded(
            ServerHealthHistoryKey.Create("server-1", "10.0.0.1"),
            measurements,
            ServerHealthCalculator.Aggregate(measurements));

        Assert.AreEqual(
            expected,
            ServerHealthPresentation.FromSnapshot(snapshot).ConfidenceText);
    }

    [TestMethod]
    public void HistoryKey_NormalizesServerIdentityAndProbeAddress()
    {
        ServerHealthHistoryKey left =
            ServerHealthHistoryKey.Create(" us-ny#79 ", " 10.0.0.1 ");
        ServerHealthHistoryKey right =
            ServerHealthHistoryKey.Create("US-NY#79", "10.0.0.1");

        Assert.AreEqual(left, right);
    }

    private static ServerHealthProbeMeasurement Measurement(
        double? latency,
        int successful,
        int total,
        double load,
        int seconds,
        bool outage = false)
    {
        return new(
            latency,
            successful,
            total,
            DateTimeOffset.UnixEpoch.AddSeconds(seconds),
            true,
            outage ? "No replies." : null,
            load,
            WasRetried: outage,
            IsConfirmedOutage: outage);
    }
}
```

- [ ] **Step 3: Run the tests and confirm the model does not exist yet**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64 `
  --filter FullyQualifiedName~ServerHealthCalculatorTest
```

Expected: build failure naming missing `ProtonVPN.Client.Common.UI.ServerHealth` types.

- [ ] **Step 4: Add the source contract and immutable models**

Create `IServerHealthSource.cs`:

```csharp
namespace ProtonVPN.Client.Common.UI.ServerHealth;

public interface IServerHealthSource
{
    string HealthServerId { get; }
    string? HealthProbeAddress { get; }
    double HealthServerLoad { get; }
    Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken cancellationToken);
}
```

Create `IServerHealthClock.cs`:

```csharp
namespace ProtonVPN.Client.Common.UI.ServerHealth;

public interface IServerHealthClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemServerHealthClock : IServerHealthClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
```

Create `ServerHealthGrade.cs`:

```csharp
namespace ProtonVPN.Client.Common.UI.ServerHealth;

public enum ServerHealthGrade
{
    Poor,
    Fair,
    Good,
    Excellent,
}
```

Create `ServerHealthHistoryKey.cs`:

```csharp
namespace ProtonVPN.Client.Common.UI.ServerHealth;

public readonly record struct ServerHealthHistoryKey(string ServerId, string ProbeAddress)
{
    public static ServerHealthHistoryKey Create(string serverId, string probeAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(probeAddress);
        return new(
            serverId.Trim().ToUpperInvariant(),
            probeAddress.Trim().ToUpperInvariant());
    }
}
```

Create `ServerHealthProbeMeasurement.cs`:

```csharp
namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthProbeMeasurement(
    double? AverageLatencyMilliseconds,
    int SuccessfulSamples,
    int TotalSamples,
    DateTimeOffset CheckedAt,
    bool UsedPhysicalRoute,
    string? Error,
    double ServerLoad,
    bool WasRetried = false,
    bool IsConfirmedOutage = false)
{
    public double PacketLossPercent => TotalSamples <= 0
        ? 100
        : (TotalSamples - SuccessfulSamples) * 100d / TotalSamples;

    public bool IsCompleteFailure =>
        SuccessfulSamples <= 0 || AverageLatencyMilliseconds is null;
}
```

Create `ServerHealthAggregate.cs`:

```csharp
namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthAggregate(
    double? AverageLatencyMilliseconds,
    double PacketLossPercent,
    int SuccessfulSamples,
    int TotalSamples,
    double ServerLoad,
    double Score,
    ServerHealthGrade Grade,
    int MeasurementCount);
```

Create `ServerHealthSnapshot.cs`:

```csharp
namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthSnapshot(
    ServerHealthHistoryKey Key,
    IReadOnlyList<ServerHealthProbeMeasurement> Measurements,
    ServerHealthAggregate? Aggregate,
    ServerHealthProbeMeasurement? LatestMeasurement,
    bool IsChecking,
    bool IsRechecking,
    string? PendingError)
{
    public static ServerHealthSnapshot Empty(ServerHealthHistoryKey key) =>
        new(key, [], null, null, false, false, null);

    public static ServerHealthSnapshot CreateRecorded(
        ServerHealthHistoryKey key,
        IReadOnlyList<ServerHealthProbeMeasurement> measurements,
        ServerHealthAggregate aggregate) =>
        new(
            key,
            measurements,
            measurements.Count == 0 ? null : aggregate,
            measurements.Count == 0 ? null : measurements[^1],
            false,
            false,
            null);

    public int ConfidenceCount => Math.Min(Measurements.Count, 3);
}
```

- [ ] **Step 5: Implement pooling and the unchanged score formula**

Create `ServerHealthCalculator.cs`:

```csharp
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

        if (loss >= 50) return Math.Min(score, 39);
        if (loss >= 25) return Math.Min(score, 64);
        return score;
    }
}
```

Create `ServerHealthPresentation.cs`:

```csharp
namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed record ServerHealthPresentation(
    string GradeText,
    int ActiveBarCount,
    string LatencyText,
    string PacketLossText,
    string LoadText,
    string ConfidenceText,
    string RouteText,
    string LastCheckedText)
{
    public static ServerHealthPresentation FromSnapshot(ServerHealthSnapshot snapshot)
    {
        if (snapshot.Aggregate is null)
        {
            string state = snapshot.IsRechecking ? "Rechecking…" : "Checking…";
            return new(
                state, 0, "—", "—", "—", "Waiting for first check",
                snapshot.PendingError ?? "Physical adapter (direct)", state);
        }

        ServerHealthAggregate aggregate = snapshot.Aggregate;
        string confidence = aggregate.MeasurementCount >= 3
            ? "Based on 3 checks"
            : $"Based on {aggregate.MeasurementCount} of 3 checks";
        return new(
            aggregate.Grade.ToString(),
            aggregate.Grade switch
            {
                ServerHealthGrade.Excellent => 4,
                ServerHealthGrade.Good => 3,
                ServerHealthGrade.Fair => 2,
                _ => 1,
            },
            aggregate.AverageLatencyMilliseconds is null
                ? "—"
                : $"{aggregate.AverageLatencyMilliseconds.Value:0} ms",
            $"{aggregate.PacketLossPercent:0.#}%",
            $"{aggregate.ServerLoad:P0}",
            confidence,
            snapshot.LatestMeasurement?.UsedPhysicalRoute == true
                ? "Physical adapter (direct)"
                : snapshot.LatestMeasurement?.Error ?? "Route unavailable",
            snapshot.LatestMeasurement is null
                ? "Waiting for first check"
                : $"Updated {snapshot.LatestMeasurement.CheckedAt.ToLocalTime():T}");
    }
}
```

- [ ] **Step 6: Move the old contract out of `ServerHealthControl` and adapt `ServerLocationItemBase`**

Remove the old in-file `IServerHealthSource` and `ServerHealthProbeMeasurement`; import the new namespace. Add:

```csharp
public string HealthServerId => Server.Id;
public double HealthServerLoad => Load;
```

Map successful IPC results with sample counts and the source load:

```csharp
return new ServerHealthProbeMeasurement(
    response.AverageLatencyMilliseconds,
    response.SuccessfulSamples,
    response.TotalSamples,
    new DateTimeOffset(DateTime.SpecifyKind(response.CheckedAtUtc, DateTimeKind.Utc)),
    response.UsedPhysicalRoute,
    response.Error,
    HealthServerLoad);
```

Map local/IPC failure to a complete but unconfirmed failed attempt:

```csharp
private ServerHealthProbeMeasurement CreateUnavailableMeasurement(string error) =>
    new(null, 0, 4, DateTimeOffset.UtcNow, false, error, HealthServerLoad);
```

- [ ] **Step 7: Run the focused tests**

Run the Step 3 command. Expected: all calculator and presentation tests pass.

- [ ] **Step 8: Commit**

```powershell
git add src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth `
  src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs `
  src/Client/ProtonVPN.Client/Models/Connections/ServerLocationItemBase.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests
git commit -m "feat: add pooled server health model"
```

---

### Task 2: Implement retained history, expiry, retry, and per-key probe coalescing

**Files:**
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistoryStore.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistorySession.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthTestDoubles.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthHistoryStoreTest.cs`

**Interfaces:**
- Consumes the Task 1 model/calculator.
- Produces `GetSnapshot`, `ProbeAsync`, `SnapshotChanged`, and `ServerHealthHistorySession.Current`.

- [ ] **Step 1: Add deterministic test doubles**

```csharp
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
        if (!BlockDelays) return Task.CompletedTask;
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
    public void Enqueue(Func<CancellationToken, Task<ServerHealthProbeMeasurement>> result) =>
        _results.Enqueue(result);
    public Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken token)
    {
        ProbeCount++;
        return _results.Dequeue()(token);
    }
}
```

- [ ] **Step 2: Write failing store tests**

Create tests with exact assertions for these cases:

```csharp
[TestMethod]
public async Task FirstCompleteFailure_RetriesAfterFiveSecondsAndRecordsSuccessfulRetry()
{
    FakeServerHealthClock clock = new();
    QueueServerHealthSource source = Source();
    source.Enqueue(Failure(clock, "first"));
    source.Enqueue(Success(clock, 40, 4));
    using ServerHealthHistoryStore store = new(clock);

    ServerHealthSnapshot result = await store.ProbeAsync(source, CancellationToken.None);

    CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(5) }, clock.Delays);
    Assert.AreEqual(2, source.ProbeCount);
    Assert.AreEqual(1, result.Measurements.Count);
    Assert.IsTrue(result.Measurements[0].WasRetried);
    Assert.IsFalse(result.Measurements[0].IsConfirmedOutage);
}

[TestMethod]
public async Task TwoCompleteFailures_RecordOneConfirmedOutage()
{
    FakeServerHealthClock clock = new();
    QueueServerHealthSource source = Source();
    source.Enqueue(Failure(clock, "first"));
    source.Enqueue(Failure(clock, "second"));
    using ServerHealthHistoryStore store = new(clock);

    ServerHealthSnapshot result = await store.ProbeAsync(source, CancellationToken.None);

    Assert.AreEqual(1, result.Measurements.Count);
    Assert.AreEqual(0, result.Measurements[0].SuccessfulSamples);
    Assert.AreEqual(4, result.Measurements[0].TotalSamples);
    Assert.IsTrue(result.Measurements[0].IsConfirmedOutage);
    Assert.AreEqual(100, result.Aggregate!.PacketLossPercent);
}

[TestMethod]
public async Task PartialLoss_DoesNotRetry()
{
    FakeServerHealthClock clock = new();
    QueueServerHealthSource source = Source();
    source.Enqueue(Success(clock, 40, 2));
    using ServerHealthHistoryStore store = new(clock);

    ServerHealthSnapshot result = await store.ProbeAsync(source, CancellationToken.None);

    Assert.AreEqual(1, source.ProbeCount);
    Assert.AreEqual(0, clock.Delays.Count);
    Assert.AreEqual(50, result.Aggregate!.PacketLossPercent);
}
```

Also implement tests named exactly:

```csharp
ProbeAsync_SameKeyConcurrentRequests_UseOneUnderlyingProbe
ProbeAsync_RequestDuringRetryDelay_JoinsTheSameLogicalBatch
ProbeAsync_FirstFailure_PreservesPreviousAggregateWhileRechecking
GetSnapshot_RowLifetimeDoesNotClearHistoryButTenMinutesOfInactivityDoes
ProbeAsync_FourBatches_RetainsGraphHistoryButScoresNewestThree
ProbeAsync_ConsumerCancellation_DoesNotCreateSyntheticLoss
GetSnapshot_ASecondConsumerWithTheSameCompositeKey_SeesExistingHistory
```

Each test uses the doubles above and asserts probe count, event state, retained count, aggregate count, and outage flags directly; the retry-delay test blocks `FakeServerHealthClock`, starts a second request, completes the delay, and asserts only two service probes occurred.

- [ ] **Step 3: Run store tests and confirm the coordinator is missing**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64 `
  --filter FullyQualifiedName~ServerHealthHistoryStoreTest
```

Expected: build failure for missing `ServerHealthHistoryStore`.

- [ ] **Step 4: Implement `ServerHealthHistoryStore`**

Use these exact constants and public surface:

```csharp
private static readonly TimeSpan _retention = TimeSpan.FromMinutes(10);
private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);
private readonly SemaphoreSlim _probeSlots = new(8, 8);
private readonly object _inFlightLock = new();
private readonly Dictionary<ServerHealthHistoryKey, Task<ServerHealthSnapshot>> _inFlight = [];
private readonly ConcurrentDictionary<ServerHealthHistoryKey, Entry> _entries = [];

public event EventHandler<ServerHealthSnapshotChangedEventArgs>? SnapshotChanged;
public ServerHealthSnapshot GetSnapshot(ServerHealthHistoryKey key);
public Task<ServerHealthSnapshot> ProbeAsync(
    IServerHealthSource source,
    CancellationToken cancellationToken);
```

Coalesce consumers without letting one view cancellation cancel the underlying operation:

```csharp
Task<ServerHealthSnapshot> pending;
lock (_inFlightLock)
{
    if (!_inFlight.TryGetValue(key, out pending!))
    {
        TaskCompletionSource<ServerHealthSnapshot> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion.Task;
        _inFlight.Add(key, pending);
        _ = RunProbeAndReleaseAsync(key, source, completion);
    }
}
return await pending.WaitAsync(cancellationToken);
```

`RunProbeAndReleaseAsync` removes the key in `finally`. `ProbeCoreAsync` must:

```csharp
ServerHealthProbeMeasurement first = await InvokeProbeAsync(source, lifetimeToken);
if (!first.IsCompleteFailure)
{
    return Record(key, entry, first with { ServerLoad = source.HealthServerLoad });
}

SetTransientState(key, entry, isChecking: false, isRechecking: true, first.Error);
await _clock.DelayAsync(_retryDelay, lifetimeToken);
ServerHealthProbeMeasurement retry = await InvokeProbeAsync(source, lifetimeToken);
if (!retry.IsCompleteFailure)
{
    return Record(key, entry, retry with
    {
        ServerLoad = source.HealthServerLoad,
        WasRetried = true,
    });
}

return Record(key, entry, new ServerHealthProbeMeasurement(
    null, 0, 4, _clock.UtcNow,
    first.UsedPhysicalRoute || retry.UsedPhysicalRoute,
    retry.Error ?? first.Error,
    source.HealthServerLoad,
    WasRetried: true,
    IsConfirmedOutage: true));
```

`Record` appends one measurement, updates `LastRecordedAt`, prunes `CheckedAt < UtcNow - 10 minutes`, computes `ServerHealthCalculator.Aggregate`, clears checking flags, and raises an immutable snapshot. `GetSnapshot` performs the same prune and removes an inactive empty key after ten minutes. Catch non-cancellation probe exceptions and convert them to an unconfirmed zero-reply attempt; propagate cancellation without recording anything.

- [ ] **Step 5: Add the session owner**

```csharp
public static class ServerHealthHistorySession
{
    public static ServerHealthHistoryStore Current { get; } = new();
}
```

- [ ] **Step 6: Run all shared-health tests**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64
```

Expected: all calculator and store tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistoryStore.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistorySession.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth
git commit -m "feat: retain shared server health history"
```

---

### Task 3: Make candidate-row meters consume shared snapshots

**Files:**
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs`
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/Custom/ServerConnectionRowButton.cs`

**Interfaces:** Consumes `ServerHealthHistorySession.Current`, `ProbeAsync`, `SnapshotChanged`, and `ServerHealthPresentation`.

- [ ] **Step 1: Run the second-consumer shared-key test from Task 2**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64 `
  --filter FullyQualifiedName~GetSnapshot_ASecondConsumer
```

Expected: pass before UI migration.

- [ ] **Step 2: Remove candidate-owned history/cache state**

Delete `_cacheLifetime`, `_probeSlots`, `_probeCache`, `_probesInProgress`, `ProbeCacheEntry`, and `GetMeasurementAsync`. Add:

```csharp
private readonly ServerHealthHistoryStore _historyStore = ServerHealthHistorySession.Current;
private ServerHealthSnapshot? _snapshot;
private ServerHealthHistoryKey? _historyKey;
```

Subscribe on `Loaded`, unsubscribe on `Unloaded`, but never clear the store entry:

```csharp
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
```

- [ ] **Step 3: Restore history before probing and retain the 60-second cadence**

```csharp
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
```

The loop remains 60 seconds and uses:

```csharp
ServerHealthSnapshot snapshot =
    await _historyStore.ProbeAsync(ProbeSource!, cancellationToken);
ApplySnapshot(snapshot);
await Task.Delay(_refreshInterval, cancellationToken);
```

- [ ] **Step 4: Apply pooled presentation and shared events**

`ApplySnapshot` sets 0–4 bars from `ServerHealthPresentation.ActiveBarCount`, uses the existing success/warning/danger theme brushes by `snapshot.Aggregate.Grade`, and sets pooled latency/loss/load/confidence in the tooltip. During `IsRechecking`, keep the previous aggregate and bars while indicating the pending retry.

```csharp
private void OnSnapshotChanged(object? sender, ServerHealthSnapshotChangedEventArgs e)
{
    if (_historyKey is not ServerHealthHistoryKey key || e.Snapshot.Key != key) return;
    DispatcherQueue.TryEnqueue(() =>
    {
        if (_isLoaded && _historyKey == e.Snapshot.Key)
        {
            ApplySnapshot(e.Snapshot);
        }
    });
}
```

- [ ] **Step 5: Pass source load consistently from `ServerConnectionRowButton`**

```csharp
IServerHealthSource? source = DataContext as IServerHealthSource;
bool canProbe = !IsUnderMaintenance &&
                !string.IsNullOrWhiteSpace(source?.HealthProbeAddress);
_serverHealthControl.ServerLoad = source?.HealthServerLoad ?? ServerLoad;
_serverHealthControl.Visibility = canProbe ? Visibility.Visible : Visibility.Collapsed;
_serverHealthControl.ProbeSource = canProbe ? source : null;
```

- [ ] **Step 6: Build Common UI**

```powershell
dotnet build src/Client/Common/ProtonVPN.Client.Common.UI/ProtonVPN.Client.Common.UI.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64
```

Expected: success with no references to the removed static probe cache.

- [ ] **Step 7: Commit**

```powershell
git add src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI/Controls/Custom/ServerConnectionRowButton.cs
git commit -m "feat: use shared history for server row health"
```

---

### Task 4: Move the connected-server panel onto the same store

**Files:**
- Modify: `src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderViewModel.cs`
- Modify: `src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderView.xaml`

- [ ] **Step 1: Add shared state and a current-server source adapter**

Add fields:

```csharp
private readonly ServerHealthHistoryStore _healthHistoryStore =
    ServerHealthHistorySession.Current;
private ServerHealthHistoryKey? _currentHealthKey;

[ObservableProperty]
private ServerHealthSnapshot? _currentServerHealthSnapshot;
```

Subscribe/unsubscribe `SnapshotChanged` with view-model activation. Add a nested `CurrentServerHealthSource : IServerHealthSource` that captures `ServerId`, selected probe address, `ServerLoad`, and calls the existing `_vpnServiceCaller.ProbeServerHealthAsync` without changing the IPC request/result types. Map service failure to an unconfirmed zero-reply measurement and successful IPC data to sample counts, timestamp, route flag, and load.

- [ ] **Step 2: Restore candidate history immediately on connection**

```csharp
private void RestartHealthMonitoring()
{
    IServerHealthSource? source = CreateCurrentServerHealthSource();
    if (source is null || string.IsNullOrWhiteSpace(source.HealthProbeAddress))
    {
        _currentHealthKey = null;
        CurrentServerHealthSnapshot = null;
        SetUnavailableHealth("No endpoint is available for the current server.");
        return;
    }

    _currentHealthKey = ServerHealthHistoryKey.Create(
        source.HealthServerId,
        source.HealthProbeAddress);
    ApplyHealthSnapshot(_healthHistoryStore.GetSnapshot(_currentHealthKey.Value));
    _ = RefreshCurrentServerHealthAsync();
}
```

`CreateCurrentServerHealthSource` must use the existing `GetProbeAddress(ConnectionDetails)` ordering and `ConnectionDetails.ServerId`/`ServerLoad`, guaranteeing the same composite key as the search model.

- [ ] **Step 3: Replace direct latest-batch scoring while retaining the 30-second timer**

```csharp
private async Task RefreshCurrentServerHealthAsync()
{
    if (_isHealthRefreshInProgress || !_connectionManager.IsConnected) return;
    IServerHealthSource? source = CreateCurrentServerHealthSource();
    if (source is null || string.IsNullOrWhiteSpace(source.HealthProbeAddress))
    {
        SetUnavailableHealth("No endpoint is available for the current server.");
        return;
    }

    ServerHealthHistoryKey requestedKey = ServerHealthHistoryKey.Create(
        source.HealthServerId,
        source.HealthProbeAddress);
    _currentHealthKey = requestedKey;
    _isHealthRefreshInProgress = true;
    try
    {
        ServerHealthSnapshot snapshot =
            await _healthHistoryStore.ProbeAsync(source, CancellationToken.None);
        if (_connectionManager.IsConnected && _currentHealthKey == requestedKey)
        {
            ApplyHealthSnapshot(snapshot);
        }
    }
    finally
    {
        _isHealthRefreshInProgress = false;
    }
}
```

Delete `ApplyHealthMeasurement`, `CalculateHealthScore`, and `_lastHealthProbeAddress`; keep `HEALTH_REFRESH_TIMER_INTERVAL_IN_MS = 30000`.

- [ ] **Step 4: Apply the same snapshot presentation on the UI thread**

```csharp
private void OnHealthSnapshotChanged(object? sender, ServerHealthSnapshotChangedEventArgs e)
{
    if (_currentHealthKey is not ServerHealthHistoryKey key || e.Snapshot.Key != key) return;
    ExecuteOnUIThread(() =>
    {
        if (_currentHealthKey == e.Snapshot.Key) ApplyHealthSnapshot(e.Snapshot);
    });
}

private void ApplyHealthSnapshot(ServerHealthSnapshot snapshot)
{
    CurrentServerHealthSnapshot = snapshot;
    ServerHealthPresentation p = ServerHealthPresentation.FromSnapshot(snapshot);
    HealthGrade = p.GradeText;
    HealthLatency = p.LatencyText;
    HealthPacketLoss = p.PacketLossText;
    HealthLoad = p.LoadText;
    HealthRoute = snapshot.IsRechecking
        ? $"Rechecking in progress — {p.RouteText}"
        : p.RouteText;
    HealthLastChecked = p.ConfidenceText;
    SetHealthState(snapshot.Aggregate?.Grade switch
    {
        ServerHealthGrade.Excellent => HealthState.Excellent,
        ServerHealthGrade.Good => HealthState.Good,
        ServerHealthGrade.Fair => HealthState.Fair,
        ServerHealthGrade.Poor => HealthState.Poor,
        _ => HealthState.Checking,
    });
}
```

`ResetHealthDisplay` also clears `CurrentServerHealthSnapshot` and `_currentHealthKey`. Keep the compact XAML dimensions; the existing footer now displays confidence.

- [ ] **Step 5: Build the full client**

```powershell
dotnet build src/Client/ProtonVPN.Client/ProtonVPN.Client.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64
```

Expected: success; both health surfaces now compile against one calculator/store.

- [ ] **Step 6: Commit**

```powershell
git add src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderViewModel.cs `
  src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderView.xaml
git commit -m "feat: share health history with connected server"
```

---

### Task 5: Add graph-ready history and reusable hover details

**Files:**
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGraphSeries.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthHistoryDetailsControl.cs`
- Create: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthGraphSeriesTest.cs`
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs`
- Modify: `src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderView.xaml`

- [ ] **Step 1: Write failing graph-series tests**

```csharp
[TestMethod]
public void Create_ReturnsRetainedMeasurementsInTimeOrderAndMarksNewestThree()
{
    ServerHealthProbeMeasurement[] measurements =
    [
        Measurement(90, 40, 4),
        Measurement(0, 50, 4),
        Measurement(60, null, 0, true),
        Measurement(30, 45, 3),
    ];
    ServerHealthSnapshot snapshot = ServerHealthSnapshot.CreateRecorded(
        ServerHealthHistoryKey.Create("server-1", "10.0.0.1"),
        measurements,
        ServerHealthCalculator.Aggregate(measurements));

    IReadOnlyList<ServerHealthGraphPoint> result =
        ServerHealthGraphSeries.Create(snapshot);

    CollectionAssert.AreEqual(
        new[] { 0, 30, 60, 90 },
        result.Select(p => (int)(p.CheckedAt - DateTimeOffset.UnixEpoch).TotalSeconds).ToArray());
    CollectionAssert.AreEqual(
        new[] { false, true, true, true },
        result.Select(p => p.IsScoreDriver).ToArray());
}

[TestMethod]
public void Create_ConfirmedOutageHasLossButNoLatency()
{
    ServerHealthProbeMeasurement outage = Measurement(0, null, 0, true);
    ServerHealthSnapshot snapshot = ServerHealthSnapshot.CreateRecorded(
        ServerHealthHistoryKey.Create("server-1", "10.0.0.1"),
        [outage],
        ServerHealthCalculator.Aggregate([outage]));

    ServerHealthGraphPoint result = ServerHealthGraphSeries.Create(snapshot).Single();

    Assert.IsNull(result.LatencyMilliseconds);
    Assert.AreEqual(100, result.PacketLossPercent);
    Assert.IsTrue(result.IsConfirmedOutage);
}
```

- [ ] **Step 2: Run graph tests and confirm projection types are missing**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64 `
  --filter FullyQualifiedName~ServerHealthGraphSeriesTest
```

Expected: build failure for missing graph types.

- [ ] **Step 3: Implement graph projection**

```csharp
public sealed record ServerHealthGraphPoint(
    DateTimeOffset CheckedAt,
    double? LatencyMilliseconds,
    double PacketLossPercent,
    double ServerLoad,
    int SuccessfulSamples,
    int TotalSamples,
    bool WasRetried,
    bool IsConfirmedOutage,
    bool IsScoreDriver,
    string? Error);

public static class ServerHealthGraphSeries
{
    public static IReadOnlyList<ServerHealthGraphPoint> Create(ServerHealthSnapshot snapshot)
    {
        ServerHealthProbeMeasurement[] ordered = snapshot.Measurements
            .OrderBy(m => m.CheckedAt)
            .ToArray();
        int scoreStart = Math.Max(0, ordered.Length - 3);
        return ordered.Select((m, index) => new ServerHealthGraphPoint(
            m.CheckedAt,
            m.AverageLatencyMilliseconds,
            m.PacketLossPercent,
            m.ServerLoad,
            m.SuccessfulSamples,
            m.TotalSamples,
            m.WasRetried,
            m.IsConfirmedOutage,
            index >= scoreStart,
            m.Error)).ToArray();
    }
}
```

- [ ] **Step 4: Run graph tests**

Run Step 2. Expected: both graph tests pass.

- [ ] **Step 5: Implement `ServerHealthHistoryDetailsControl`**

Create a `Border`-derived WinUI control with:

```csharp
public static readonly DependencyProperty SnapshotProperty =
    DependencyProperty.Register(
        nameof(Snapshot),
        typeof(ServerHealthSnapshot),
        typeof(ServerHealthHistoryDetailsControl),
        new PropertyMetadata(null, OnSnapshotChanged));

public ServerHealthSnapshot? Snapshot
{
    get => (ServerHealthSnapshot?)GetValue(SnapshotProperty);
    set => SetValue(SnapshotProperty, value);
}
```

Its constructor builds a 344-pixel-wide layout containing summary text, a 320×120 `Canvas`, and latest-batch text. Rendering must:

1. Use `ServerHealthPresentation` for pooled grade/latency/loss/confidence.
2. Use `ServerHealthGraphSeries` for all retained points.
3. Scale X from first to last timestamp.
4. Draw load as a one-pixel, 45%-opacity secondary polyline.
5. Draw latency as separate polyline segments so a no-reply point creates a visible gap rather than bridging the outage.
6. Draw one marker per point; use larger markers for `IsScoreDriver` and position confirmed 100%-loss outages at the top.
7. Attach a WinUI tooltip to each marker containing timestamp, latency or `—`, loss, reply count, load, retry status, and confirmed-outage status.
8. Show `Rechecking after failure: <diagnostic>` above the previous latest batch while a retry is pending.
9. Show route text and latest-batch time below the graph.
10. Use existing theme brushes (`SignalSuccessColorBrush`, `SignalWarningColorBrush`, `SignalDangerColorBrush`, `TextWeakColorBrush`) with existing fallback colors.

- [ ] **Step 6: Attach the same details control to candidate rows**

Keep one instance in `ServerHealthControl`:

```csharp
private readonly ServerHealthHistoryDetailsControl _detailsControl = new();
```

Set it once with `ToolTipService.SetToolTip(this, _detailsControl)` and update `_detailsControl.Snapshot` in `ApplySnapshot`. Preserve a plain-text `AutomationProperties.Name` containing the same pooled values and confidence.

- [ ] **Step 7: Attach it to the connected panel**

Add:

```xml
xmlns:commonControls="using:ProtonVPN.Client.Common.UI.Controls"
```

Inside `CurrentServerHealthPanel`:

```xml
<ToolTipService.ToolTip>
    <ToolTip Placement="Top">
        <commonControls:ServerHealthHistoryDetailsControl
            Snapshot="{x:Bind ViewModel.CurrentServerHealthSnapshot, Mode=OneWay}" />
    </ToolTip>
</ToolTipService.ToolTip>
```

Do not add another timer, probe callback, or graph collection to the view model.

- [ ] **Step 8: Run tests and build**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64

dotnet build src/Client/ProtonVPN.Client/ProtonVPN.Client.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64
```

Expected: all shared-health tests pass and client build succeeds.

- [ ] **Step 9: Commit**

```powershell
git add src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGraphSeries.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthHistoryDetailsControl.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs `
  src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderView.xaml `
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthGraphSeriesTest.cs
git commit -m "feat: show shared server health history graph"
```

---

### Task 6: Verify lifecycle, failure semantics, artifacts, and live behavior

**Files:** Modify only when a verification reveals a concrete defect in Tasks 1–5.

- [ ] **Step 1: Run the complete health suite twice**

```powershell
1..2 | ForEach-Object {
    dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
      --configuration Release --runtime win-x64 -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "Health test pass $_ failed." }
}
```

Expected: both passes green.

- [ ] **Step 2: Build service and client exactly as CI does**

```powershell
dotnet restore src/ProtonVPN.Service/ProtonVPN.Service.csproj --runtime win-x64
dotnet build src/ProtonVPN.Service/ProtonVPN.Service.csproj `
  --configuration Release --runtime win-x64 --no-restore -p:Platform=x64 -v:minimal

dotnet restore src/Client/ProtonVPN.Client/ProtonVPN.Client.csproj --runtime win-x64
dotnet build src/Client/ProtonVPN.Client/ProtonVPN.Client.csproj `
  --configuration Release --runtime win-x64 --no-restore -p:Platform=x64 -v:minimal
```

Expected: both builds succeed.

- [ ] **Step 3: Confirm direct-probe service and IPC files are untouched**

```powershell
git diff marc/proton...HEAD -- `
  src/ProtonVPN.Service/ServerHealth `
  src/ProcessCommunication/ProtonVPN.ProcessCommunication.Contracts/Entities/Vpn/ServerHealthProbeRequestIpcEntity.cs `
  src/ProcessCommunication/ProtonVPN.ProcessCommunication.Contracts/Entities/Vpn/ServerHealthProbeResultIpcEntity.cs
```

Expected: no output.

- [ ] **Step 4: Dispatch and watch the existing `both` patch workflow**

```powershell
gh workflow run windows-client-fast-patch.yml `
  --repo marcmy/ProtonVPN-win-app `
  --ref feature/server-health-history `
  -f build_mode=both -f upload_full_bin=false -f target_version=4.4.1

$runId = gh run list --repo marcmy/ProtonVPN-win-app `
  --workflow windows-client-fast-patch.yml `
  --branch feature/server-health-history --limit 1 `
  --json databaseId --jq '.[0].databaseId'
gh run watch $runId --repo marcmy/ProtonVPN-win-app --exit-status
```

Expected: workflow succeeds and publishes raw patch and installer artifacts.

- [ ] **Step 5: Verify live shared behavior**

Install the artifact and perform these observations:

1. Connect to a server also visible in search.
2. Confirm search row and connected panel show identical pooled grade, latency, and loss.
3. Confirm connected checks remain about 30 seconds and visible-row checks about 60 seconds.
4. Confirm confidence advances 1-of-3 → 2-of-3 → 3 checks.
5. Scroll the row off-screen/back and leave/return to search within ten minutes; history must remain.
6. Hover both surfaces; they must show the same graph and latest batch.
7. Confirm an isolated miss across three normal batches appears near 8.3% rather than 25%.
8. Confirm one clean batch does not instantly erase a recent bad batch.
9. Confirm graph history trims at ten minutes and newest three points are emphasized.
10. Confirm normal traffic remains tunneled and temporary candidate `/32` routes are removed after probes.

- [ ] **Step 6: Verify a live confirmed outage safely**

Temporarily substitute the current debug source’s probe address with the documentation-only unreachable address `192.0.2.1`, build without committing that substitution, and observe:

1. First complete failure preserves the previous aggregate and shows `Rechecking…`.
2. Retry starts about five seconds later.
3. Second complete failure creates exactly one 100%-loss graph point.
4. The point has no latency value and carries the retry/confirmed-outage labels.
5. Remove the substitution, rebuild, and verify the next successful batches roll the outage out only after three newer recorded checks.

The successful-retry path is already deterministically covered by `FirstCompleteFailure_RetriesAfterFiveSecondsAndRecordsSuccessfulRetry`; do not add a production test hook.

- [ ] **Step 7: Inspect final diff**

```powershell
git diff --check marc/proton...HEAD

git grep -n "_probeCache\|_cacheLifetime\|CalculateHealthScore" -- `
  src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs `
  src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderViewModel.cs
```

Expected: first command prints nothing; second returns no matches and exit code 1.

- [ ] **Step 8: Commit only concrete verification fixes**

```powershell
git add src/Client/Common/ProtonVPN.Client.Common.UI `
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests `
  src/Client/ProtonVPN.Client/Models/Connections/ServerLocationItemBase.cs `
  src/Client/ProtonVPN.Client/UI/Main/Home/Status
git commit -m "fix: harden server health history behavior"
```

Skip this commit when verification required no correction.

- [ ] **Step 9: Prepare the pull request**

Open from `feature/server-health-history` into `marc/proton`:

```markdown
## Summary

- pool server health across the newest three four-ping checks
- share session history between candidate rows and the connected-server panel
- retry complete failures once after five seconds before recording one confirmed outage
- retain ten minutes of graph history without adding probe traffic
- show provisional confidence and pooled latency/loss values
- add the same hover history graph to both health surfaces

## Validation

- shared health tests pass twice
- client and service Release win-x64 builds pass
- Windows fast patch `both` workflow passes
- live sharing, graph, expiry, and confirmed-outage behavior verified
```
