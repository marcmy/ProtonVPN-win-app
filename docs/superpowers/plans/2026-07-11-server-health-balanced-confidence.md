# Server Health Balanced Confidence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Change shared server-health grading from the newest three completed checks to the newest six completed checks for both the connected-server panel and candidate-server rows.

**Architecture:** Keep `ServerHealthCalculator` as the single source of truth through an internal `ScoreMeasurementCount` constant. Reuse that count in aggregation, confidence presentation, and history-graph score-driver marking so the UI cannot drift from the grading policy. Preserve recorded order when selecting score drivers, then sort graph points chronologically for display.

**Tech Stack:** C# 12, .NET 8, MSTest 3.11.1, GitHub Actions on `windows-latest`.

## Global Constraints

- Use exactly the newest **6 completed measurements** for grading.
- Apply the same six-check policy to both current-server and candidate-server displays.
- Continue grading from all available measurements while fewer than six exist.
- Show partial confidence as `Based on X of 6 checks`.
- Show full confidence as `Based on 6 checks`.
- Mark the same newest six recorded measurements as score drivers in the history graph.
- Keep the current-server refresh cadence at 30 seconds.
- Keep the candidate-server refresh cadence at 60 seconds.
- Keep each completed check at four ping samples.
- Keep score weights, grade thresholds, retry semantics, direct-route probing, and the ten-minute retention period unchanged.
- Do not add settings, persistence, schema changes, or separate current/candidate grading windows.

---

## File Map

- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs`
  - Owns the shared six-measurement policy and aggregate calculations.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs`
  - Formats confidence text from the shared count.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGraphSeries.cs`
  - Marks score-driving measurements by recorded order and sorts points for display.
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs`
  - Verifies aggregation, outage displacement, newest-load behavior, and confidence text.
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthHistoryStoreTest.cs`
  - Verifies retained graph history while only the newest six checks drive scoring.
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthGraphSeriesTest.cs`
  - Verifies chronological graph order and recorded-order score-driver selection.

### Task 1: Define the six-check behavior with failing tests

**Files:**
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs`
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthHistoryStoreTest.cs`
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthGraphSeriesTest.cs`

**Interfaces:**
- Consumes: `ServerHealthCalculator.Aggregate`, `ServerHealthPresentation.FromSnapshot`, `ServerHealthGraphSeries.Create`, and `ServerHealthHistoryStore.ProbeAsync`.
- Produces: regression coverage for a shared six-check policy.

- [ ] **Step 1: Replace three-check calculator expectations with six-check expectations**

Cover these cases in `ServerHealthCalculatorTest.cs`:

```csharp
Assert.AreEqual(6, result.MeasurementCount);
Assert.AreEqual(24, result.TotalSamples);
```

Use seven retained measurements to prove the oldest one is excluded, six clean measurements to prove Excellent remains possible, and one outage followed by six clean measurements to prove the outage remains until the sixth newer check displaces it.

- [ ] **Step 2: Expand confidence-copy coverage from one-through-three to one-through-six**

Use these exact data rows:

```csharp
[DataRow(1, "Based on 1 of 6 checks")]
[DataRow(2, "Based on 2 of 6 checks")]
[DataRow(3, "Based on 3 of 6 checks")]
[DataRow(4, "Based on 4 of 6 checks")]
[DataRow(5, "Based on 5 of 6 checks")]
[DataRow(6, "Based on 6 checks")]
```

- [ ] **Step 3: Update the retained-history regression test**

Replace the four-batch/three-score test with one outage followed by six successful checks:

```csharp
[TestMethod]
public async Task SevenRecordedBatches_RetainGraphHistoryButScoreNewestSix()
{
    FakeServerHealthClock clock = new();
    QueueServerHealthSource source = Source();
    source.Enqueue(Failure(clock, "first"));
    source.Enqueue(Failure(clock, "retry"));
    for (int i = 0; i < 6; i++)
    {
        source.Enqueue(Success(clock, 30 + i, 4));
    }
    using ServerHealthHistoryStore store = new(clock);

    ServerHealthSnapshot result = await store.ProbeAsync(source, CancellationToken.None);
    for (int i = 0; i < 6; i++)
    {
        result = await store.ProbeAsync(source, CancellationToken.None);
    }

    Assert.AreEqual(7, result.Measurements.Count);
    Assert.AreEqual(6, result.Aggregate!.MeasurementCount);
    Assert.AreEqual(0d, result.Aggregate.PacketLossPercent, 0.001);
}
```

- [ ] **Step 4: Update the graph-series regression test**

Use seven measurements with the newest recorded measurement carrying a backward timestamp. After chronological sorting, expect the oldest recorded measurement—not the earliest timestamp—to be the only non-driver:

```csharp
CollectionAssert.AreEqual(
    new[] { -30, 0, 30, 60, 90, 120, 150 },
    result.Select(point => (int)(point.CheckedAt - DateTimeOffset.UnixEpoch).TotalSeconds).ToArray());
CollectionAssert.AreEqual(
    new[] { true, true, true, true, false, true, true },
    result.Select(point => point.IsScoreDriver).ToArray());
```

- [ ] **Step 5: Run the server-health test project and verify the new expectations fail**

Run:

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release -p:Platform=x64
```

Expected: FAIL in the confidence-copy and graph score-driver tests because production still formats and marks only three checks.

- [ ] **Step 6: Commit the regression tests**

```bash
git add \
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs \
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthHistoryStoreTest.cs \
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthGraphSeriesTest.cs
git commit -m "test: define balanced server health confidence"
```

### Task 2: Implement one shared six-check policy

**Files:**
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs`
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs`
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGraphSeries.cs`

**Interfaces:**
- Produces: `ServerHealthCalculator.ScoreMeasurementCount` as an internal constant shared within the UI assembly.
- Preserves: all existing public method and record signatures.

- [ ] **Step 1: Make the calculator own the shared count**

Use:

```csharp
internal const int ScoreMeasurementCount = 6;
```

and select measurements with:

```csharp
ServerHealthProbeMeasurement[] measurements = retained
    .TakeLast(ScoreMeasurementCount)
    .ToArray();
```

Do not change scoring formulas, thresholds, load selection, or loss caps.

- [ ] **Step 2: Format confidence text from the shared count**

Use:

```csharp
int requiredMeasurements = ServerHealthCalculator.ScoreMeasurementCount;
string confidence = aggregate.MeasurementCount >= requiredMeasurements
    ? $"Based on {requiredMeasurements} checks"
    : $"Based on {aggregate.MeasurementCount} of {requiredMeasurements} checks";
```

- [ ] **Step 3: Mark graph score drivers before chronological sorting**

Use recorded indexes to determine score drivers, then sort for display:

```csharp
int scoreStart = Math.Max(
    0,
    snapshot.Measurements.Count - ServerHealthCalculator.ScoreMeasurementCount);
return snapshot.Measurements
    .Select((measurement, index) => (
        Measurement: measurement,
        IsScoreDriver: index >= scoreStart))
    .OrderBy(item => item.Measurement.CheckedAt)
    .Select(item => new ServerHealthGraphPoint(
        item.Measurement.CheckedAt,
        item.Measurement.AverageLatencyMilliseconds,
        item.Measurement.PacketLossPercent,
        item.Measurement.ServerLoad,
        item.Measurement.SuccessfulSamples,
        item.Measurement.TotalSamples,
        item.Measurement.WasRetried,
        item.Measurement.IsConfirmedOutage,
        item.IsScoreDriver,
        item.Measurement.Error))
    .ToArray();
```

- [ ] **Step 4: Run the complete server-health test project**

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release -p:Platform=x64
```

Expected: PASS with zero failed tests and no MSTest analyzer annotations.

- [ ] **Step 5: Commit the implementation**

```bash
git add \
  src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs \
  src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs \
  src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthGraphSeries.cs
git commit -m "feat: use balanced server health confidence"
```

### Task 3: Validate scope and application builds

**Files:**
- Review the six implementation/test files above.
- Review: `docs/superpowers/specs/2026-07-11-server-health-balanced-confidence-design.md`
- Review: `docs/superpowers/plans/2026-07-11-server-health-balanced-confidence.md`

- [ ] **Step 1: Inspect the final branch diff**

```bash
git diff marc/proton...HEAD -- \
  src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth \
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth \
  docs/superpowers/specs/2026-07-11-server-health-balanced-confidence-design.md \
  docs/superpowers/plans/2026-07-11-server-health-balanced-confidence.md
```

Expected: no cadence, routing, retention, score-weight, grade-threshold, project-file, or workflow changes.

- [ ] **Step 2: Run the existing pull-request validation workflow**

The `Windows fast patch build` workflow job named `Validate client and service changes` must complete these steps successfully:

```text
Run server health tests
Restore client project
Restore service project
Build client project
Build service project
```

Expected final job conclusion: `success` with no new annotations.

- [ ] **Step 3: Record the validated immutable head SHA**

After CI succeeds, read the PR head SHA from GitHub and post it with:

```text
Server-health tests: passed
Client build: passed
Service build: passed
Annotations: none
```
