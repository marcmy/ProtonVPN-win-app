# Server Health Balanced Confidence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Change shared server-health grading from the newest three completed checks to the newest six completed checks for both the connected-server panel and candidate-server rows.

**Architecture:** Keep `ServerHealthCalculator` as the single source of truth for the rolling grading window and keep `ServerHealthPresentation` responsible only for user-facing confidence copy. The existing `ServerHealthHistoryStore`, probe cadence, retry behavior, retention policy, score weights, grade thresholds, and routing behavior remain unchanged.

**Tech Stack:** C# 12, .NET 8, MSTest 3.11.1, GitHub Actions on `windows-latest`.

## Global Constraints

- Use exactly the newest **6 completed measurements** for grading.
- Apply the same six-check policy to both current-server and candidate-server displays.
- Continue grading from all available measurements while fewer than six exist.
- Show partial confidence as `Based on X of 6 checks`.
- Show full confidence as `Based on 6 checks`.
- Keep the current-server refresh cadence at 30 seconds.
- Keep the candidate-server refresh cadence at 60 seconds.
- Keep each completed check at four ping samples.
- Keep score weights, grade thresholds, retry semantics, direct-route probing, and the ten-minute retention period unchanged.
- Do not add settings, persistence, schema changes, or separate current/candidate grading windows.

---

## File Map

- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs`
  - Owns the rolling measurement-count policy and aggregate calculations.
- `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs`
  - Formats the confidence label shown by both server-health displays.
- `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs`
  - Verifies the rolling window, outage displacement, recorded-order semantics, newest-load behavior, partial-history grading, and confidence copy.

### Task 1: Expand the shared grading window to six measurements

**Files:**
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs`
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs:11-23`

**Interfaces:**
- Consumes: `ServerHealthCalculator.Aggregate(IReadOnlyList<ServerHealthProbeMeasurement>)`
- Produces: the same public method and `ServerHealthAggregate` shape, now calculated from up to six newest measurements.

- [ ] **Step 1: Replace three-check regression cases with six-check cases**

In `ServerHealthCalculatorTest.cs`, replace the existing three-check window tests with the following methods:

```csharp
[TestMethod]
public void Aggregate_OneMissAcrossSixBatches_UsesOneOfTwentyFourLoss()
{
    ServerHealthProbeMeasurement[] measurements =
    [
        Measurement(40, 4, 4, 0.20, 0),
        Measurement(42, 3, 4, 0.25, 30),
        Measurement(41, 4, 4, 0.30, 60),
        Measurement(39, 4, 4, 0.20, 90),
        Measurement(40, 4, 4, 0.20, 120),
        Measurement(41, 4, 4, 0.20, 150),
    ];

    ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

    Assert.AreEqual(23, result.SuccessfulSamples);
    Assert.AreEqual(24, result.TotalSamples);
    Assert.AreEqual(100d / 24d, result.PacketLossPercent, 0.001);
    Assert.AreEqual(6, result.MeasurementCount);
    Assert.IsTrue(result.Grade is ServerHealthGrade.Good or ServerHealthGrade.Excellent);
}

[TestMethod]
public void Aggregate_UsesNewestSixBatchesAndNewestLoad()
{
    ServerHealthProbeMeasurement[] measurements =
    [
        Measurement(null, 0, 4, 0.10, 0, true),
        Measurement(30, 4, 4, 0.20, 30),
        Measurement(31, 4, 4, 0.30, 60),
        Measurement(32, 4, 4, 0.40, 90),
        Measurement(33, 4, 4, 0.50, 120),
        Measurement(34, 4, 4, 0.60, 150),
        Measurement(35, 4, 4, 0.70, 180),
    ];

    ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

    Assert.AreEqual(24, result.SuccessfulSamples);
    Assert.AreEqual(24, result.TotalSamples);
    Assert.AreEqual(0d, result.PacketLossPercent, 0.001);
    Assert.AreEqual(0.70, result.ServerLoad, 0.001);
    Assert.AreEqual(6, result.MeasurementCount);
}

[TestMethod]
public void Aggregate_UsesRecordedOrderWhenTheClockMovesBackward()
{
    ServerHealthProbeMeasurement[] measurements =
    [
        Measurement(null, 0, 4, 0.10, 0, true),
        Measurement(30, 4, 4, 0.20, 30),
        Measurement(31, 4, 4, 0.30, 60),
        Measurement(32, 4, 4, 0.40, 90),
        Measurement(33, 4, 4, 0.50, 120),
        Measurement(34, 4, 4, 0.60, 150),
        Measurement(35, 4, 4, 0.70, -30),
    ];

    ServerHealthAggregate result = ServerHealthCalculator.Aggregate(measurements);

    Assert.AreEqual(24, result.SuccessfulSamples);
    Assert.AreEqual(24, result.TotalSamples);
    Assert.AreEqual(0d, result.PacketLossPercent, 0.001);
    Assert.AreEqual(0.70, result.ServerLoad, 0.001);
    Assert.AreEqual(6, result.MeasurementCount);
}

[TestMethod]
public void Aggregate_SixCleanFastBatches_CanBeExcellent()
{
    ServerHealthProbeMeasurement[] measurements =
    [
        Measurement(20, 4, 4, 0.10, 0),
        Measurement(22, 4, 4, 0.10, 30),
        Measurement(21, 4, 4, 0.10, 60),
        Measurement(20, 4, 4, 0.10, 90),
        Measurement(22, 4, 4, 0.10, 120),
        Measurement(21, 4, 4, 0.10, 150),
    ];

    Assert.AreEqual(
        ServerHealthGrade.Excellent,
        ServerHealthCalculator.Aggregate(measurements).Grade);
}

[TestMethod]
public void Aggregate_ConfirmedOutage_RemainsUntilSixNewerBatchesReplaceIt()
{
    ServerHealthProbeMeasurement outage = Measurement(null, 0, 4, 0.10, 0, true);
    ServerHealthProbeMeasurement[] recovery = Enumerable.Range(1, 6)
        .Select(index => Measurement(20, 4, 4, 0.10, index * 30))
        .ToArray();

    for (int count = 1; count < 6; count++)
    {
        Assert.AreNotEqual(
            ServerHealthGrade.Excellent,
            ServerHealthCalculator.Aggregate([outage, .. recovery.Take(count)]).Grade,
            $"The outage should still affect the grade after only {count} newer checks.");
    }

    Assert.AreEqual(
        ServerHealthGrade.Excellent,
        ServerHealthCalculator.Aggregate([outage, .. recovery]).Grade);
}
```

Keep these existing tests unchanged because they verify required partial-history behavior:

```csharp
Aggregate_WeightsLatencyBySuccessfulReplies
Aggregate_ConfirmedOutageWithoutReplies_IsPoorAndHasNoLatency
```

- [ ] **Step 2: Run the calculator tests and verify the new expectations fail**

Run:

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release -p:Platform=x64 `
  --filter "FullyQualifiedName~ServerHealthCalculatorTest"
```

Expected: FAIL. At minimum, `Aggregate_UsesNewestSixBatchesAndNewestLoad` must fail because the implementation still selects only three measurements, and the outage-recovery test must show the outage disappearing after three newer checks instead of six.

- [ ] **Step 3: Change the calculator window from three to six**

In `ServerHealthCalculator.cs`, replace:

```csharp
private const int SCORE_MEASUREMENT_COUNT = 3;
```

with:

```csharp
private const int SCORE_MEASUREMENT_COUNT = 6;
```

Do not change the aggregation formulas, newest-load selection, grade thresholds, or loss caps.

- [ ] **Step 4: Run the calculator tests and verify they pass**

Run:

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release -p:Platform=x64 `
  --filter "FullyQualifiedName~ServerHealthCalculatorTest"
```

Expected: PASS with zero failed tests.

- [ ] **Step 5: Commit the calculator behavior change**

```bash
git add \
  src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs \
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs
git commit -m "feat: use six checks for server health grading"
```

### Task 2: Update confidence presentation for the six-check target

**Files:**
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs:128-145`
- Modify: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs:31-34`

**Interfaces:**
- Consumes: `ServerHealthPresentation.FromSnapshot(ServerHealthSnapshot)` and `ServerHealthAggregate.MeasurementCount`
- Produces: unchanged `ServerHealthPresentation` record shape with six-check confidence copy.

- [ ] **Step 1: Expand the confidence test matrix from one-through-three to one-through-six**

Replace the existing `FromSnapshot_FormatsConfidence` data rows with:

```csharp
[TestMethod]
[DataRow(1, "Based on 1 of 6 checks")]
[DataRow(2, "Based on 2 of 6 checks")]
[DataRow(3, "Based on 3 of 6 checks")]
[DataRow(4, "Based on 4 of 6 checks")]
[DataRow(5, "Based on 5 of 6 checks")]
[DataRow(6, "Based on 6 checks")]
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
```

- [ ] **Step 2: Run only the confidence test and verify it fails**

Run:

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release -p:Platform=x64 `
  --filter "FullyQualifiedName~FromSnapshot_FormatsConfidence"
```

Expected: FAIL because production still formats against a target of three checks.

- [ ] **Step 3: Change the presentation target from three to six**

In `ServerHealthPresentation.cs`, replace:

```csharp
string confidence = aggregate.MeasurementCount >= 3
    ? "Based on 3 checks"
    : $"Based on {aggregate.MeasurementCount} of 3 checks";
```

with:

```csharp
string confidence = aggregate.MeasurementCount >= 6
    ? "Based on 6 checks"
    : $"Based on {aggregate.MeasurementCount} of 6 checks";
```

Do not change the waiting state, grade text, active-bar mapping, latency, packet-loss, load, route, or last-checked formatting.

- [ ] **Step 4: Run the confidence test and verify it passes**

Run:

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release -p:Platform=x64 `
  --filter "FullyQualifiedName~FromSnapshot_FormatsConfidence"
```

Expected: PASS for all six data rows.

- [ ] **Step 5: Run the complete server-health test project**

Run:

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release -p:Platform=x64
```

Expected: PASS with zero failed tests and no MSTest analyzer annotations.

- [ ] **Step 6: Commit the presentation update**

```bash
git add \
  src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs \
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs
git commit -m "feat: update server health confidence copy"
```

### Task 3: Review the branch and validate both application builds

**Files:**
- Review only: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs`
- Review only: `src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs`
- Review only: `src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs`
- Review only: `docs/superpowers/specs/2026-07-11-server-health-balanced-confidence-design.md`

**Interfaces:**
- Consumes: the two implementation commits from Tasks 1 and 2.
- Produces: a reviewable pull request whose existing validation workflow proves tests, client build, and service build.

- [ ] **Step 1: Inspect the final diff for scope compliance**

Run:

```bash
git diff marc/proton...HEAD -- \
  src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthCalculator.cs \
  src/Client/Common/ProtonVPN.Client.Common.UI/ServerHealth/ServerHealthPresentation.cs \
  src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ServerHealth/ServerHealthCalculatorTest.cs \
  docs/superpowers/specs/2026-07-11-server-health-balanced-confidence-design.md \
  docs/superpowers/plans/2026-07-11-server-health-balanced-confidence.md
```

Expected: only the shared measurement-count constant, confidence copy, six-check tests, and approved documentation differ. There must be no cadence, routing, retention, score-weight, grade-threshold, project-file, or workflow changes.

- [ ] **Step 2: Re-run the full unit-test project from the final branch head**

Run:

```powershell
dotnet test src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj `
  --configuration Release -p:Platform=x64
```

Expected: PASS with zero failed tests.

- [ ] **Step 3: Open the pull request against `marc/proton`**

Run:

```bash
gh pr create \
  --base marc/proton \
  --head feature/server-health-balanced-confidence \
  --title "feat: use balanced server health confidence" \
  --body "## Summary

- grade server health from the newest six completed checks instead of three
- apply the shared six-check window to current and candidate servers
- update confidence text to `Based on X of 6 checks`
- keep outage history until six newer checks replace it
- preserve refresh cadences, score thresholds, routing, retries, and retention

## Validation

- server-health unit tests
- client build
- service build"
```

Expected: a new open pull request targeting `marc/proton`.

- [ ] **Step 4: Verify the existing PR validation workflow**

Wait for the existing `Windows fast patch build` workflow job named `Validate client and service changes`.

Expected successful steps:

```text
Run server health tests
Restore client project
Restore service project
Build client project
Build service project
```

Expected final job conclusion: `success` with no new annotations.

- [ ] **Step 5: Record the verified head SHA and validation result in the PR summary**

Run:

```bash
HEAD_SHA=$(git rev-parse HEAD)
gh pr comment --body "Verified head: $HEAD_SHA
Server-health tests: passed
Client build: passed
Service build: passed
Annotations: none"
```

Expected: the pull request contains the immutable validated branch SHA and the four successful validation results.
