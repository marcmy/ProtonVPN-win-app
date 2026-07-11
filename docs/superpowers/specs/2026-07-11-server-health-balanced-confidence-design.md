# Server Health Balanced Confidence Design

## Goal

Change server-health grading from the current three-check rolling window to a six-check rolling window for both the connected-server panel and candidate-server rows.

This implements the approved **Balanced** confidence policy:

- current server reaches full confidence after roughly 3 minutes at the existing 30-second refresh cadence;
- candidate rows reach full confidence after roughly 6 minutes at the existing 60-second refresh cadence.

## Scope

The change is intentionally limited to the shared server-health aggregation, confidence presentation, history-graph score-driver markers, and their tests.

In scope:

- use the newest six completed measurements when calculating latency, packet loss, score, and grade;
- continue grading from the available history while fewer than six measurements exist;
- update confidence text to use six as the target;
- keep a confirmed outage in the rolling score until six newer measurements replace it;
- mark the same newest six recorded measurements as score drivers in the history graph;
- update regression tests for the six-check window.

Out of scope:

- changing probe frequency;
- changing the four-ping sample size within each check;
- changing score weights or grade thresholds;
- changing the ten-minute history retention period;
- adding a user-facing setting for the confidence window;
- giving current and candidate servers different window sizes.

## Architecture

`ServerHealthCalculator` remains the single source of truth for the grading window through a shared `ScoreMeasurementCount` constant set to six. Aggregation selects the newest measurements by recorded order.

Because both connected-server and candidate-server displays consume snapshots built through the same calculator, both automatically adopt the six-check policy without separate UI-specific grading logic.

`ServerHealthPresentation` uses the shared count when formatting confidence copy:

- `Based on 1 of 6 checks`
- `Based on 2 of 6 checks`
- …
- `Based on 6 checks`

`ServerHealthGraphSeries` also uses the shared count. It identifies score-driving measurements by recorded order before sorting points chronologically for display, so a backward clock adjustment cannot make the graph highlight measurements that did not determine the grade.

The grade itself remains visible before six checks are collected; the confidence label communicates that the result is still settling.

## Data Flow

1. A server-health probe records one completed measurement.
2. `ServerHealthHistoryStore` retains the measurement under the existing ten-minute policy.
3. `ServerHealthCalculator.Aggregate` selects up to the newest six retained measurements by recorded order.
4. Latency and packet-loss calculations aggregate those selected measurements exactly as they do today.
5. The newest selected measurement still supplies the current server load.
6. `ServerHealthPresentation` formats the aggregate and reports progress toward six checks.
7. `ServerHealthGraphSeries` marks the same recorded measurements as score drivers, then orders all retained points by timestamp for display.

No persistence or schema changes are required.

## Failure and Recovery Behavior

Confirmed outages keep their existing semantics. A no-reply result remains represented in the rolling aggregate until six newer completed measurements push it out of the window.

This makes grades less twitchy and prevents one brief clean period from immediately erasing a recent outage. Cancellation, retry, direct-route probing, and transient checking/rechecking states remain unchanged.

## Testing

Update the calculator, store, graph-series, and presentation tests to prove:

- aggregation uses the newest six measurements rather than the newest three;
- measurements older than the newest six do not affect latency, packet loss, or grade;
- the history store retains older graph history while scoring only the newest six;
- the newest measurement still supplies server load;
- recorded order remains authoritative if timestamps move backward;
- graph score-driver markers match the newest six recorded measurements even after chronological sorting;
- a confirmed outage is not fully displaced until six newer measurements exist;
- six clean, fast measurements can produce an Excellent grade;
- confidence text is correct for counts 1 through 6;
- existing single-measurement and partial-history behavior remains valid.

Run the server-health unit tests, then the existing pull-request validation workflow that builds both the client and service.

## Acceptance Criteria

- Both current-server and candidate-server grades use the newest six completed checks.
- Partial history still produces a grade and displays `Based on X of 6 checks`.
- Full confidence displays `Based on 6 checks`.
- History-graph score-driver markers identify the same newest six recorded checks used by grading.
- No refresh cadence, scoring threshold, score weight, sample count, retention, or routing behavior changes.
- Updated unit tests pass.
- Client and service validation builds pass.
