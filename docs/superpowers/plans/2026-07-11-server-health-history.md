# Server Health History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace latest-probe server health with a shared, session-wide three-check history, confirmed-outage retry behavior, and a reusable ten-minute hover graph for both candidate rows and the connected-server panel.

**Architecture:** Put scoring, retained measurements, retry/coalescing, presentation, and graph projection in `ProtonVPN.Client.Common.UI` so both current health surfaces consume the same immutable snapshots. A session singleton owns one testable `ServerHealthHistoryStore` keyed by stable server ID plus normalized probe address; the privileged service remains stateless and unchanged.

**Tech Stack:** C# 12, .NET 8, WinUI 3, CommunityToolkit.Mvvm, MSTest, NSubstitute, existing Proton VPN IPC/service probe.

## Global Constraints

- Keep each normal probe at exactly 4 ICMP samples.
- Keep connected-server refresh at 30 seconds and visible candidate-row refresh at 60 seconds.
- Score from only the newest 3 recorded batches by pooling successful and attempted replies.
- Retain graph measurements for 10 minutes; expire a key after 10 minutes without a recorded measurement.
- Show a grade immediately and identify provisional state as `Based on 1 of 3 checks` or `Based on 2 of 3 checks`.
- On complete failure, retry exactly once after 5 seconds. Only a second complete failure records one synthetic 4-attempt, 0-reply, 100%-loss batch.
- Record partial loss immediately and never trigger the fast retry for partial loss.
- Preserve existing latency buckets, 45% latency / 45% reliability / 10% load weights, packet-loss caps, and grade thresholds.
- Use reply-weighted pooled latency, pooled packet loss, and the newest known load for both grade and displayed numbers.
- Share history between candidate rows and the connected panel for the same stable server ID and normalized probe address.
- Preserve history across row unload, scrolling, and navigation during the app session; do not persist across process restarts.
- Generate no additional periodic probes for aggregation or graphing.
- Do not modify direct physical-adapter routing, WFP rules, IPC entities, service timeout, or service sample loop.
- View cancellation and app shutdown must not create synthetic packet loss.
- A confirmed outage has no fabricated latency. When the score window has no successful replies, display latency as `—` and grade Poor.
- Consumers marshal store notifications to their UI thread.

---

## File Map

### Create

- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/IServerHealthSource.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/IServerHealthClock.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGrade.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistoryKey.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthProbeMeasurement.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthAggregate.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthSnapshot.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistoryStore.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistorySession.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGraphSeries.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthHistoryDetailsControl.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj`
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthHistoryStoreTest.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthGraphSeriesTest.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthTestDoubles.cs`

### Modify

- `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs`
- `src/Client/Common/ProtonVPN.Client.Common.UI/Controls/Custom/ServerConnectionRowButton.cs`
- `src/Client/ProtonVPN.Client/Models/Connections/ServerLocationItemBase.cs`
- `src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderViewModel.cs`
- `src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderView.xaml`

---

### Task 1: Shared model, calculator, and presentation

**Files:**
- Create all model/calculator/presentation files listed above.
- Create the test project and `ServerHealthCalculatorTest.cs`.
- Modify `ServerHealthControl.cs` to remove its in-file contract/measurement declarations.
- Modify `ServerLocationItemBase.cs` to implement the expanded source contract.

**Interfaces:**
- Produces `IServerHealthSource`, `ServerHealthHistoryKey`, `ServerHealthProbeMeasurement`, `ServerHealthAggregate`, `ServerHealthSnapshot`, `ServerHealthCalculator`, and `ServerHealthPresentation`.

- [ ] **Step 1: Create the test project**

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
    <Compile Include="..\..\..\GlobalAssemblyInfo.cs" Link="Properties\GlobalAssemblyInfo.cs" />
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

- [ ] **Step 2: Write the failing calculator/presentation tests**

Create `ServerHealthCalculatorTest.cs`:

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
    public void Aggregate_UsesNewestThreeBatchesAndNewestLoad()
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
    public void Aggregate_ConfirmedOutageWithoutReplies_IsPoorAndHasNoLatency()
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
    public void Aggregate_ConfirmedOutage_RemainsUntilThreeNewerBatchesReplaceIt()
    {
        ServerHealthProbeMeasurement outage = Measurement(null, 0, 4, 0.10, 0, true);
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
    public void FromSnapshot_FormatsConfidence(int count, string expected)
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
    public void HistoryKey_NormalizesIdentityAndAddress()
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
        bool outage = false) =>
        new(
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
```

- [ ] **Step 3: Run the tests to verify failure**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64 `
  --filter FullyQualifiedName~ServerHealthCalculatorTest
```

Expected: compilation fails because the new server-health types do not exist.

- [ ] **Step 4: Add the source contract and immutable models**

`IServerHealthSource.cs`:

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

`IServerHealthClock.cs`:

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
    public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}
```

`ServerHealthGrade.cs`:

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

`ServerHealthHistoryKey.cs`:

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

`ServerHealthProbeMeasurement.cs`:

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

`ServerHealthAggregate.cs`:

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

`ServerHealthSnapshot.cs`:

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
            aggregate,
            measurements.Count == 0 ? null : measurements[^1],
            false,
            false,
            null);

    public int ConfidenceCount => Math.Min(Measurements.Count, 3);
}
```

- [ ] **Step 5: Implement aggregation and shared formatting**

`ServerHealthCalculator.cs`:

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

`ServerHealthPresentation.cs`:

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

        ServerHealthAggregate a = snapshot.Aggregate;
        string confidence = a.MeasurementCount >= 3
            ? "Based on 3 checks"
            : $"Based on {a.MeasurementCount} of 3 checks";
        return new(
            a.Grade.ToString(),
            a.Grade switch
            {
                ServerHealthGrade.Excellent => 4,
                ServerHealthGrade.Good => 3,
                ServerHealthGrade.Fair => 2,
                _ => 1,
            },
            a.AverageLatencyMilliseconds is null ? "—" : $"{a.AverageLatencyMilliseconds.Value:0} ms",
            $"{a.PacketLossPercent:0.#}%",
            $"{a.ServerLoad:P0}",
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

- [ ] **Step 6: Move the old contract and adapt the candidate model**

Remove the old interface/record from `ServerHealthControl.cs`; import `ProtonVPN.Client.Common.UI.ServerHealth` there, in `ServerConnectionRowButton.cs`, and in `ServerLocationItemBase.cs`.

Add to `ServerLocationItemBase`:

```csharp
public string HealthServerId => Server.Id;
public double HealthServerLoad => Load;
```

Map success/failure exactly:

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

```csharp
private ServerHealthProbeMeasurement CreateUnavailableMeasurement(string error) =>
    new(null, 0, 4, DateTimeOffset.UtcNow, false, error, HealthServerLoad);
```

- [ ] **Step 7: Run tests**

Run Step 3. Expected: all calculator/presentation tests pass.

- [ ] **Step 8: Commit**

```powershell
git add src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth `
  src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs `
  src/Client/ProtonVPN.Client/Models/Connections/ServerLocationItemBase.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests
git commit -m "feat: add pooled server health model"
```

---

### Task 2: History, expiry, retry, and coalescing

**Files:**
- Create `ServerHealthHistoryStore.cs`, `ServerHealthHistorySession.cs`, `ServerHealthTestDoubles.cs`, and `ServerHealthHistoryStoreTest.cs`.

**Interfaces:**
- Produces `GetSnapshot`, `ProbeAsync`, `SnapshotChanged`, and `ServerHealthHistorySession.Current`.

- [ ] **Step 1: Add deterministic doubles**

`ServerHealthTestDoubles.cs`:

```csharp
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

internal sealed class FakeServerHealthClock : IServerHealthClock
{
    private TaskCompletionSource? _pendingDelay;
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
    public List<TimeSpan> Delays { get; } = [];
    public bool BlockDelays { get; set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Delays.Add(delay);
        UtcNow += delay;
        if (!BlockDelays) return Task.CompletedTask;
        _pendingDelay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => _pendingDelay.TrySetCanceled(token));
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

    public Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken token)
    {
        ProbeCount++;
        return _results.Dequeue()(token);
    }
}
```

- [ ] **Step 2: Write the complete failing store suite**

`ServerHealthHistoryStoreTest.cs`:

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthHistoryStoreTest
{
    [TestMethod]
    public async Task FirstFailure_RetriesAfterFiveSecondsAndRecordsSuccessfulRetry()
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
    public async Task TwoFailures_RecordOneConfirmedOutage()
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

    [TestMethod]
    public async Task ConcurrentSameKeyRequests_UseOneProbe()
    {
        FakeServerHealthClock clock = new();
        TaskCompletionSource<ServerHealthProbeMeasurement> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource source = Source();
        source.Enqueue(_ => completion.Task);
        using ServerHealthHistoryStore store = new(clock);

        Task<ServerHealthSnapshot> first = store.ProbeAsync(source, CancellationToken.None);
        Task<ServerHealthSnapshot> second = store.ProbeAsync(source, CancellationToken.None);
        completion.SetResult(Success(clock, 40, 4));
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, source.ProbeCount);
        Assert.AreSame(first.Result, second.Result);
    }

    [TestMethod]
    public async Task RequestDuringRetryDelay_JoinsLogicalBatch()
    {
        FakeServerHealthClock clock = new() { BlockDelays = true };
        QueueServerHealthSource source = Source();
        source.Enqueue(Failure(clock, "first"));
        source.Enqueue(Failure(clock, "second"));
        using ServerHealthHistoryStore store = new(clock);

        Task<ServerHealthSnapshot> first = store.ProbeAsync(source, CancellationToken.None);
        Assert.IsTrue(SpinWait.SpinUntil(() => clock.Delays.Count == 1, TimeSpan.FromSeconds(1)));
        Task<ServerHealthSnapshot> second = store.ProbeAsync(source, CancellationToken.None);
        clock.CompleteDelay();
        await Task.WhenAll(first, second);

        Assert.AreEqual(2, source.ProbeCount);
        Assert.AreSame(first.Result, second.Result);
        Assert.AreEqual(1, first.Result.Measurements.Count);
        Assert.IsTrue(first.Result.Measurements[0].IsConfirmedOutage);
    }

    [TestMethod]
    public async Task FirstFailure_PreservesPreviousAggregateWhileRechecking()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Success(clock, 40, 4));
        source.Enqueue(Failure(clock, "first"));
        source.Enqueue(Failure(clock, "second"));
        using ServerHealthHistoryStore store = new(clock);
        ServerHealthSnapshot previous = await store.ProbeAsync(source, CancellationToken.None);
        List<ServerHealthSnapshot> observed = [];
        store.SnapshotChanged += (_, args) => observed.Add(args.Snapshot);

        await store.ProbeAsync(source, CancellationToken.None);

        ServerHealthSnapshot rechecking = observed.Single(s => s.IsRechecking);
        Assert.AreEqual(previous.Aggregate, rechecking.Aggregate);
        Assert.AreEqual(1, rechecking.Measurements.Count);
        Assert.AreEqual("first", rechecking.PendingError);
    }

    [TestMethod]
    public async Task HistorySurvivesViewLifetimeAndExpiresAfterTenMinutes()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Success(clock, 40, 4));
        using ServerHealthHistoryStore store = new(clock);
        ServerHealthSnapshot recorded = await store.ProbeAsync(source, CancellationToken.None);

        Assert.AreEqual(1, store.GetSnapshot(recorded.Key).Measurements.Count);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.AreEqual(1, store.GetSnapshot(recorded.Key).Measurements.Count);
        clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromMilliseconds(1)));
        Assert.AreEqual(0, store.GetSnapshot(recorded.Key).Measurements.Count);
    }

    [TestMethod]
    public async Task FourRecordedBatches_RetainGraphHistoryButScoreNewestThree()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Failure(clock, "first"));
        source.Enqueue(Failure(clock, "retry"));
        source.Enqueue(Success(clock, 30, 4));
        source.Enqueue(Success(clock, 31, 4));
        source.Enqueue(Success(clock, 32, 4));
        using ServerHealthHistoryStore store = new(clock);

        await store.ProbeAsync(source, CancellationToken.None);
        await store.ProbeAsync(source, CancellationToken.None);
        await store.ProbeAsync(source, CancellationToken.None);
        ServerHealthSnapshot result = await store.ProbeAsync(source, CancellationToken.None);

        Assert.AreEqual(4, result.Measurements.Count);
        Assert.AreEqual(3, result.Aggregate!.MeasurementCount);
        Assert.AreEqual(0, result.Aggregate.PacketLossPercent);
    }

    [TestMethod]
    public async Task ConsumerCancellation_DoesNotCreateLoss()
    {
        FakeServerHealthClock clock = new();
        TaskCompletionSource<ServerHealthProbeMeasurement> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource source = Source();
        source.Enqueue(_ => completion.Task);
        using ServerHealthHistoryStore store = new(clock);
        using CancellationTokenSource cancellation = new();
        Task<ServerHealthSnapshot> pending = store.ProbeAsync(source, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => pending);
        TaskCompletionSource<ServerHealthSnapshot> recorded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.SnapshotChanged += (_, args) =>
        {
            if (args.Snapshot.Aggregate is not null) recorded.TrySetResult(args.Snapshot);
        };
        completion.SetResult(Success(clock, 40, 4));
        ServerHealthSnapshot result = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, source.ProbeCount);
        Assert.AreEqual(1, result.Measurements.Count);
        Assert.IsFalse(result.Measurements[0].IsConfirmedOutage);
    }

    [TestMethod]
    public async Task SecondConsumerWithSameKey_SeesExistingHistory()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Success(clock, 40, 4));
        using ServerHealthHistoryStore store = new(clock);
        ServerHealthSnapshot first = await store.ProbeAsync(source, CancellationToken.None);
        ServerHealthSnapshot second = store.GetSnapshot(ServerHealthHistoryKey.Create(
            source.HealthServerId,
            source.HealthProbeAddress!));

        CollectionAssert.AreEqual(first.Measurements.ToArray(), second.Measurements.ToArray());
        Assert.AreEqual(first.Aggregate, second.Aggregate);
    }

    private static QueueServerHealthSource Source() => new()
    {
        HealthServerId = "server-1",
        HealthProbeAddress = "10.0.0.1",
        HealthServerLoad = 0.25,
    };

    private static ServerHealthProbeMeasurement Success(
        FakeServerHealthClock clock,
        double latency,
        int successful) =>
        new(latency, successful, 4, clock.UtcNow, true, null, 0.25);

    private static ServerHealthProbeMeasurement Failure(
        FakeServerHealthClock clock,
        string error) =>
        new(null, 0, 4, clock.UtcNow, false, error, 0.25);
}
```

- [ ] **Step 3: Run the suite to verify failure**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64 `
  --filter FullyQualifiedName~ServerHealthHistoryStoreTest
```

Expected: compilation fails for missing `ServerHealthHistoryStore`.

- [ ] **Step 4: Implement the complete store**

`ServerHealthHistoryStore.cs`:

```csharp
using System.Collections.Concurrent;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed class ServerHealthSnapshotChangedEventArgs : EventArgs
{
    public ServerHealthSnapshot Snapshot { get; }
    public ServerHealthSnapshotChangedEventArgs(ServerHealthSnapshot snapshot) => Snapshot = snapshot;
}

public sealed class ServerHealthHistoryStore : IDisposable
{
    private static readonly TimeSpan _retention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);
    private readonly IServerHealthClock _clock;
    private readonly SemaphoreSlim _probeSlots;
    private readonly ConcurrentDictionary<ServerHealthHistoryKey, Entry> _entries = [];
    private readonly object _inFlightLock = new();
    private readonly Dictionary<ServerHealthHistoryKey, Task<ServerHealthSnapshot>> _inFlight = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    public event EventHandler<ServerHealthSnapshotChangedEventArgs>? SnapshotChanged;

    public ServerHealthHistoryStore(
        IServerHealthClock? clock = null,
        int maximumConcurrentProbes = 8)
    {
        _clock = clock ?? new SystemServerHealthClock();
        _probeSlots = new(maximumConcurrentProbes, maximumConcurrentProbes);
    }

    public ServerHealthSnapshot GetSnapshot(ServerHealthHistoryKey key)
    {
        if (!_entries.TryGetValue(key, out Entry? entry)) return ServerHealthSnapshot.Empty(key);
        lock (entry.SyncRoot)
        {
            Prune(entry);
            if (entry.Measurements.Count == 0 &&
                !entry.IsChecking &&
                _clock.UtcNow - entry.LastRecordedAt > _retention)
            {
                _entries.TryRemove(key, out _);
                return ServerHealthSnapshot.Empty(key);
            }
            return CreateSnapshot(key, entry);
        }
    }

    public async Task<ServerHealthSnapshot> ProbeAsync(
        IServerHealthSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.HealthProbeAddress))
        {
            throw new ArgumentException("A health probe address is required.", nameof(source));
        }

        ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(
            source.HealthServerId,
            source.HealthProbeAddress);
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
    }

    private async Task RunProbeAndReleaseAsync(
        ServerHealthHistoryKey key,
        IServerHealthSource source,
        TaskCompletionSource<ServerHealthSnapshot> completion)
    {
        try
        {
            completion.TrySetResult(
                await ProbeCoreAsync(key, source, _lifetimeCancellation.Token));
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_inFlightLock) _inFlight.Remove(key);
        }
    }

    private async Task<ServerHealthSnapshot> ProbeCoreAsync(
        ServerHealthHistoryKey key,
        IServerHealthSource source,
        CancellationToken token)
    {
        Entry entry = _entries.GetOrAdd(key, _ => new Entry(_clock.UtcNow));
        SetTransientState(key, entry, true, false, null);
        await _probeSlots.WaitAsync(token);
        try
        {
            ServerHealthProbeMeasurement first = await InvokeProbeAsync(source, token);
            if (!first.IsCompleteFailure)
            {
                return Record(key, entry, first with { ServerLoad = source.HealthServerLoad });
            }

            SetTransientState(key, entry, false, true, first.Error);
            await _clock.DelayAsync(_retryDelay, token);
            ServerHealthProbeMeasurement retry = await InvokeProbeAsync(source, token);
            if (!retry.IsCompleteFailure)
            {
                return Record(key, entry, retry with
                {
                    ServerLoad = source.HealthServerLoad,
                    WasRetried = true,
                });
            }

            return Record(key, entry, new(
                null,
                0,
                4,
                _clock.UtcNow,
                first.UsedPhysicalRoute || retry.UsedPhysicalRoute,
                retry.Error ?? first.Error,
                source.HealthServerLoad,
                WasRetried: true,
                IsConfirmedOutage: true));
        }
        catch (OperationCanceledException)
        {
            SetTransientState(key, entry, false, false, null);
            throw;
        }
        finally
        {
            _probeSlots.Release();
        }
    }

    private async Task<ServerHealthProbeMeasurement> InvokeProbeAsync(
        IServerHealthSource source,
        CancellationToken token)
    {
        try
        {
            return await source.ProbeHealthAsync(token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(
                null, 0, 4, _clock.UtcNow, false,
                exception.Message, source.HealthServerLoad);
        }
    }

    private ServerHealthSnapshot Record(
        ServerHealthHistoryKey key,
        Entry entry,
        ServerHealthProbeMeasurement measurement)
    {
        ServerHealthSnapshot snapshot;
        lock (entry.SyncRoot)
        {
            entry.Measurements.Add(measurement);
            entry.LastRecordedAt = measurement.CheckedAt;
            entry.IsChecking = false;
            entry.IsRechecking = false;
            entry.PendingError = null;
            Prune(entry);
            snapshot = CreateSnapshot(key, entry);
        }
        RaiseSnapshotChanged(snapshot);
        return snapshot;
    }

    private void SetTransientState(
        ServerHealthHistoryKey key,
        Entry entry,
        bool checking,
        bool rechecking,
        string? error)
    {
        ServerHealthSnapshot snapshot;
        lock (entry.SyncRoot)
        {
            Prune(entry);
            entry.IsChecking = checking;
            entry.IsRechecking = rechecking;
            entry.PendingError = error;
            snapshot = CreateSnapshot(key, entry);
        }
        RaiseSnapshotChanged(snapshot);
    }

    private void Prune(Entry entry)
    {
        DateTimeOffset cutoff = _clock.UtcNow - _retention;
        entry.Measurements.RemoveAll(m => m.CheckedAt < cutoff);
    }

    private static ServerHealthSnapshot CreateSnapshot(
        ServerHealthHistoryKey key,
        Entry entry)
    {
        ServerHealthProbeMeasurement[] measurements = entry.Measurements
            .OrderBy(m => m.CheckedAt)
            .ToArray();
        ServerHealthAggregate? aggregate = measurements.Length == 0
            ? null
            : ServerHealthCalculator.Aggregate(measurements);
        return new(
            key,
            measurements,
            aggregate,
            measurements.Length == 0 ? null : measurements[^1],
            entry.IsChecking,
            entry.IsRechecking,
            entry.PendingError);
    }

    private void RaiseSnapshotChanged(ServerHealthSnapshot snapshot) =>
        SnapshotChanged?.Invoke(this, new(snapshot));

    public void Dispose()
    {
        _lifetimeCancellation.Cancel();
        _entries.Clear();
        lock (_inFlightLock) _inFlight.Clear();
        _lifetimeCancellation.Dispose();
        _probeSlots.Dispose();
    }

    private sealed class Entry
    {
        public object SyncRoot { get; } = new();
        public List<ServerHealthProbeMeasurement> Measurements { get; } = [];
        public DateTimeOffset LastRecordedAt { get; set; }
        public bool IsChecking { get; set; }
        public bool IsRechecking { get; set; }
        public string? PendingError { get; set; }
        public Entry(DateTimeOffset createdAt) => LastRecordedAt = createdAt;
    }
}
```

- [ ] **Step 5: Add the session owner**

`ServerHealthHistorySession.cs`:

```csharp
namespace ProtonVPN.Client.Common.UI.ServerHealth;

public static class ServerHealthHistorySession
{
    public static ServerHealthHistoryStore Current { get; } = new();
}
```

- [ ] **Step 6: Run all health tests**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64
```

Expected: calculator and store tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistoryStore.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthHistorySession.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth
git commit -m "feat: retain shared server health history"
```

---

### Task 3: Candidate-row integration

**Files:**
- Modify `ServerHealthControl.cs` and `ServerConnectionRowButton.cs`.

- [ ] **Step 1: Replace control-owned cache state**

Delete `_cacheLifetime`, static `_probeSlots`, `_probeCache`, `_probesInProgress`, `ProbeCacheEntry`, and `GetMeasurementAsync`. Add:

```csharp
private readonly ServerHealthHistoryStore _historyStore = ServerHealthHistorySession.Current;
private ServerHealthSnapshot? _snapshot;
private ServerHealthHistoryKey? _historyKey;
```

Subscribe while loaded and unsubscribe while unloaded without deleting history:

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

- [ ] **Step 2: Restore history before a new probe**

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

Call `RestoreSnapshot()` from `RestartProbeLoop()` instead of clearing the latest measurement.

- [ ] **Step 3: Keep the existing 60-second loop but delegate the logical batch**

```csharp
private async Task RunProbeLoopAsync(CancellationToken token)
{
    try
    {
        while (!token.IsCancellationRequested)
        {
            IServerHealthSource? source = ProbeSource;
            if (source is null || string.IsNullOrWhiteSpace(source.HealthProbeAddress)) return;
            ApplySnapshot(await _historyStore.ProbeAsync(source, token));
            await Task.Delay(_refreshInterval, token);
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch
    {
        if (!token.IsCancellationRequested)
        {
            SetUnavailableState("The direct health check could not be completed.");
        }
    }
}
```

- [ ] **Step 4: Apply pooled presentation and cross-surface notifications**

```csharp
private void ApplySnapshot(ServerHealthSnapshot snapshot)
{
    _snapshot = snapshot;
    ServerHealthPresentation presentation = ServerHealthPresentation.FromSnapshot(snapshot);
    if (snapshot.Aggregate is null)
    {
        SetBars(0, GetThemeBrush("TextWeakColorBrush", Color.FromArgb(255, 120, 120, 130)));
        return;
    }

    (string key, Color fallback) = snapshot.Aggregate.Grade switch
    {
        ServerHealthGrade.Fair => ("SignalWarningColorBrush", Color.FromArgb(255, 245, 166, 35)),
        ServerHealthGrade.Poor => ("SignalDangerColorBrush", Color.FromArgb(255, 220, 65, 80)),
        _ => ("SignalSuccessColorBrush", Color.FromArgb(255, 29, 171, 131)),
    };
    SetBars(presentation.ActiveBarCount, GetThemeBrush(key, fallback));
}

private void OnSnapshotChanged(object? sender, ServerHealthSnapshotChangedEventArgs e)
{
    if (_historyKey is not ServerHealthHistoryKey key || e.Snapshot.Key != key) return;
    DispatcherQueue.TryEnqueue(() =>
    {
        if (_isLoaded && _historyKey == e.Snapshot.Key) ApplySnapshot(e.Snapshot);
    });
}
```

During recheck, `snapshot.Aggregate` remains the previous aggregate, so bars stay stable while tooltip text says rechecking.

- [ ] **Step 5: Pass source identity/load from the row button**

```csharp
IServerHealthSource? source = DataContext as IServerHealthSource;
bool canProbe = !IsUnderMaintenance &&
                !string.IsNullOrWhiteSpace(source?.HealthProbeAddress);
_serverHealthControl.ServerLoad = source?.HealthServerLoad ?? ServerLoad;
_serverHealthControl.Visibility = canProbe ? Visibility.Visible : Visibility.Collapsed;
_serverHealthControl.ProbeSource = canProbe ? source : null;
```

Stop rescoring from the `ServerLoad` setter; recorded measurement load owns the aggregate.

- [ ] **Step 6: Build and commit**

```powershell
dotnet build src/Client/Common/ProtonVPN.Client.Common.UI/ProtonVPN.Client.Common.UI.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64

git add src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI/Controls/Custom/ServerConnectionRowButton.cs
git commit -m "feat: use shared history for server row health"
```

Expected: build succeeds and removed static-cache names no longer exist.

---

### Task 4: Connected-panel integration

**Files:**
- Modify `ConnectionStatusHeaderViewModel.cs` and `ConnectionStatusHeaderView.xaml`.

- [ ] **Step 1: Add shared fields/state and lifecycle subscription**

```csharp
private readonly ServerHealthHistoryStore _healthHistoryStore =
    ServerHealthHistorySession.Current;
private ServerHealthHistoryKey? _currentHealthKey;

[ObservableProperty]
private ServerHealthSnapshot? _currentServerHealthSnapshot;
```

In `OnActivated`, subscribe after `base.OnActivated()`; in `OnDeactivated`, unsubscribe before returning:

```csharp
_healthHistoryStore.SnapshotChanged += OnHealthSnapshotChanged;
```

```csharp
_healthHistoryStore.SnapshotChanged -= OnHealthSnapshotChanged;
```

- [ ] **Step 2: Add the exact current-server source adapter**

```csharp
private sealed class CurrentServerHealthSource : IServerHealthSource
{
    private readonly IVpnServiceCaller _vpnServiceCaller;
    public string HealthServerId { get; }
    public string? HealthProbeAddress { get; }
    public double HealthServerLoad { get; }

    public CurrentServerHealthSource(
        IVpnServiceCaller vpnServiceCaller,
        string serverId,
        string probeAddress,
        double serverLoad)
    {
        _vpnServiceCaller = vpnServiceCaller;
        HealthServerId = serverId;
        HealthProbeAddress = probeAddress;
        HealthServerLoad = serverLoad;
    }

    public async Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Result<ServerHealthProbeResultIpcEntity> result =
            await _vpnServiceCaller.ProbeServerHealthAsync(
                new ServerHealthProbeRequestIpcEntity { Address = HealthProbeAddress! });
        token.ThrowIfCancellationRequested();
        if (!result.Success)
        {
            return new(
                null, 0, HEALTH_PROBE_SAMPLE_COUNT, DateTimeOffset.UtcNow, false,
                string.IsNullOrWhiteSpace(result.Error)
                    ? "The VPN service did not complete the direct health check."
                    : result.Error,
                HealthServerLoad);
        }

        ServerHealthProbeResultIpcEntity response = result.Value;
        return new(
            response.AverageLatencyMilliseconds,
            response.SuccessfulSamples,
            response.TotalSamples,
            new DateTimeOffset(DateTime.SpecifyKind(response.CheckedAtUtc, DateTimeKind.Utc)),
            response.UsedPhysicalRoute,
            response.Error,
            HealthServerLoad);
    }
}
```

Factory:

```csharp
private IServerHealthSource? CreateCurrentServerHealthSource()
{
    ConnectionDetails? details = _connectionManager.CurrentConnectionDetails;
    string? address = GetProbeAddress(details);
    return details is null || string.IsNullOrWhiteSpace(address)
        ? null
        : new CurrentServerHealthSource(
            _vpnServiceCaller,
            details.ServerId,
            address,
            details.ServerLoad);
}
```

- [ ] **Step 3: Restore existing history and keep the 30-second cadence**

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

Delete `_lastHealthProbeAddress`, `ApplyHealthMeasurement`, and `CalculateHealthScore`; retain `HEALTH_REFRESH_TIMER_INTERVAL_IN_MS = 30000`.

- [ ] **Step 4: Apply notifications through the existing UI dispatcher**

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

`ResetHealthDisplay` clears `_currentHealthKey` and `CurrentServerHealthSnapshot`. Keep the existing compact panel dimensions; its footer now displays confidence.

- [ ] **Step 5: Build and commit**

```powershell
dotnet build src/Client/ProtonVPN.Client/ProtonVPN.Client.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64

git add src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderViewModel.cs `
  src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderView.xaml
git commit -m "feat: share health history with connected server"
```

Expected: client builds and latest-batch score code is gone.

---

### Task 5: Graph series and reusable hover details

**Files:**
- Create `ServerHealthGraphSeries.cs`, `ServerHealthHistoryDetailsControl.cs`, and `ServerHealthGraphSeriesTest.cs`.
- Modify both health surfaces to use the details control.

- [ ] **Step 1: Write failing graph projection tests**

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthGraphSeriesTest
{
    [TestMethod]
    public void Create_OrdersPointsAndMarksNewestThree()
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

        IReadOnlyList<ServerHealthGraphPoint> result = ServerHealthGraphSeries.Create(snapshot);

        CollectionAssert.AreEqual(
            new[] { 0, 30, 60, 90 },
            result.Select(p => (int)(p.CheckedAt - DateTimeOffset.UnixEpoch).TotalSeconds).ToArray());
        CollectionAssert.AreEqual(
            new[] { false, true, true, true },
            result.Select(p => p.IsScoreDriver).ToArray());
    }

    [TestMethod]
    public void Create_OutageHasLossButNoLatency()
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

    private static ServerHealthProbeMeasurement Measurement(
        int seconds,
        double? latency,
        int successful,
        bool outage = false) =>
        new(
            latency,
            successful,
            4,
            DateTimeOffset.UnixEpoch.AddSeconds(seconds),
            true,
            outage ? "Confirmed outage" : null,
            0.25,
            WasRetried: outage,
            IsConfirmedOutage: outage);
}
```

- [ ] **Step 2: Run to verify failure**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64 `
  --filter FullyQualifiedName~ServerHealthGraphSeriesTest
```

Expected: missing graph types.

- [ ] **Step 3: Implement graph projection**

```csharp
namespace ProtonVPN.Client.Common.UI.ServerHealth;

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

Run Step 2. Expected: pass.

- [ ] **Step 5: Implement the complete reusable hover control**

`ServerHealthHistoryDetailsControl.cs`:

```csharp
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

        ServerHealthPresentation p = ServerHealthPresentation.FromSnapshot(snapshot);
        _summary.Text =
            $"{p.GradeText} • {p.LatencyText} • {p.PacketLossText} loss • {p.ConfidenceText}";
        _latest.Text = FormatLatest(snapshot, p);
        IReadOnlyList<ServerHealthGraphPoint> points = ServerHealthGraphSeries.Create(snapshot);
        if (points.Count == 0) return;

        DateTimeOffset start = points[0].CheckedAt;
        DateTimeOffset end = points[^1].CheckedAt;
        double seconds = Math.Max(1, (end - start).TotalSeconds);
        double maximumLatency = Math.Max(
            1,
            points.Where(x => x.LatencyMilliseconds is not null)
                .Select(x => x.LatencyMilliseconds!.Value)
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
                    latencySegment = new() { Stroke = latencyBrush, StrokeThickness = 2 };
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

    private static Brush GetThemeBrush(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);
}
```

- [ ] **Step 6: Attach it to candidate rows**

In `ServerHealthControl`:

```csharp
private readonly ServerHealthHistoryDetailsControl _detailsControl = new();
```

Set once in the constructor:

```csharp
ToolTipService.SetToolTip(this, _detailsControl);
```

At the end of `ApplySnapshot`:

```csharp
_detailsControl.Snapshot = snapshot;
AutomationProperties.SetName(
    this,
    $"Server health {presentation.GradeText}; " +
    $"latency {presentation.LatencyText}; " +
    $"packet loss {presentation.PacketLossText}; " +
    presentation.ConfidenceText);
```

- [ ] **Step 7: Attach it to the connected panel**

Add namespace:

```xml
xmlns:commonControls="using:ProtonVPN.Client.Common.UI.Controls"
```

Inside `CurrentServerHealthPanel`, before its child grid:

```xml
<ToolTipService.ToolTip>
    <ToolTip Placement="Top">
        <commonControls:ServerHealthHistoryDetailsControl
            Snapshot="{x:Bind ViewModel.CurrentServerHealthSnapshot, Mode=OneWay}" />
    </ToolTip>
</ToolTipService.ToolTip>
```

Do not create a second graph collection or probe timer.

- [ ] **Step 8: Test, build, and commit**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64

dotnet build src/Client/ProtonVPN.Client/ProtonVPN.Client.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64

git add src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGraphSeries.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthHistoryDetailsControl.cs `
  src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs `
  src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderView.xaml `
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthGraphSeriesTest.cs
git commit -m "feat: show shared server health history graph"
```

Expected: tests and client build pass.

---

### Task 6: Full verification and pull request

**Files:** Modify implementation files only for concrete failures discovered below.

- [ ] **Step 1: Run the health suite twice**

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

Expected: both succeed.

- [ ] **Step 3: Confirm the privileged probe implementation and IPC contract are untouched**

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

Expected: workflow and artifacts succeed.

- [ ] **Step 5: Verify live shared behavior**

1. Connect to a server visible in search.
2. Confirm candidate row and connected panel show identical pooled grade, latency, and loss.
3. Confirm connected checks remain near 30 seconds and visible-row checks near 60 seconds.
4. Confirm confidence advances 1-of-3 → 2-of-3 → 3 checks.
5. Scroll off-screen/back and leave/return to search within ten minutes; history remains.
6. Hover both surfaces; graph and latest-batch details match.
7. Confirm one miss across three normal batches is about 8.3%, not 25%.
8. Confirm one clean batch does not immediately erase a recent bad batch.
9. Confirm graph trims at ten minutes and newest three markers are emphasized.
10. Confirm normal traffic remains tunneled and temporary candidate `/32` routes are removed.

- [ ] **Step 6: Verify confirmed outage safely**

Temporarily substitute the debug source address with documentation-only `192.0.2.1`, build without committing that change, and verify:

1. First total failure preserves the previous aggregate and shows `Rechecking…`.
2. Retry begins about five seconds later.
3. Second total failure creates one—not two—100%-loss graph point.
4. The point has no latency and is labeled retry/confirmed outage.
5. Restore the address, rebuild, and verify the outage rolls out only after three newer recorded checks.

The successful-retry path is covered deterministically by `FirstFailure_RetriesAfterFiveSecondsAndRecordsSuccessfulRetry`; do not add a production test hook.

- [ ] **Step 7: Inspect final diff**

```powershell
git diff --check marc/proton...HEAD

git grep -n "_probeCache\|_cacheLifetime\|CalculateHealthScore" -- `
  src/Client/Common/ProtonVPN.Client.Common.UI/Controls/ServerHealthControl.cs `
  src/Client/ProtonVPN.Client/UI/Main/Home/Status/ConnectionStatusHeaderViewModel.cs
```

Expected: first command has no output; second has no matches and exits 1.

- [ ] **Step 8: Commit only concrete verification fixes**

```powershell
git add src/Client/Common/ProtonVPN.Client.Common.UI `
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests `
  src/Client/ProtonVPN.Client/Models/Connections/ServerLocationItemBase.cs `
  src/Client/ProtonVPN.Client/UI/Main/Home/Status
git commit -m "fix: harden server health history behavior"
```

Skip the commit when no correction was needed.

- [ ] **Step 9: Open the pull request**

Base: `marc/proton`  
Head: `feature/server-health-history`

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
