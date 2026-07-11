# Server Health History Design

**Status:** Approved  
**Date:** 2026-07-11  
**Branch:** `feature/server-health-history`  
**Base:** `marc/proton`

## Problem

The current server-health UI grades each server from only its latest four-ping probe. This makes the result overly volatile and occasionally misleading:

- One missed reply immediately appears as 25% packet loss and can push an otherwise stable server down to Fair.
- A server with recurring loss can appear Excellent whenever its most recent four replies happen to succeed.
- Search rows and the connected-server panel maintain separate snapshots, so the same endpoint can present inconsistent health.
- Scrolling a row out of view discards the useful evidence already collected.

The health display should describe recent behavior, not merely the luck of the latest four packets.

## Goals

1. Base the grade, displayed latency, and displayed packet loss on multiple recent checks.
2. React quickly enough to be useful while smoothing isolated misses.
3. Preserve evidence of intermittent loss until it naturally rolls out of the scoring window.
4. Share one recent history between search rows and the connected-server panel.
5. Preserve history while navigating or virtualizing rows during the app session.
6. Confirm a completely failed check with a fast retry before treating it as an outage.
7. Expose recent history in a hover graph without generating additional probes.
8. Keep the privileged service stateless beyond performing each requested probe.

## Non-goals

- Persisting health history across application restarts.
- Changing the direct physical-adapter routing mechanism used by probes.
- Increasing the normal four-ping sample count.
- Probing every server continuously when it is not visible or connected.
- Replacing Proton's server-load source with independently measured load.

## Chosen approach

Use a shared client-side rolling-history store keyed by server identity and normalized probe endpoint.

The privileged service continues to execute individual four-ping checks and return the result. The client owns aggregation, expiry, retry coordination, confidence state, graph data, and presentation.

This keeps historical and presentation-oriented state out of the privileged service while allowing both UI surfaces to consume the same result.

## History identity

A `ServerHealthHistoryKey` identifies a physical server endpoint using:

- the stable server identity available to both the search result and active connection; and
- the normalized probe address used by the service.

The composite key prevents two different physical servers from being mixed merely because an address is reused, while still allowing the connected panel and a search row for the same server to share history.

If a call site cannot provide the stable identity, the history adapter may fall back to the normalized probe address, but the normal search and connected paths should use the full composite key.

## Stored measurement

Each recorded measurement represents one completed health batch and contains at least:

- completion timestamp;
- average latency of successful replies, when any succeeded;
- successful reply count;
- total attempted reply count;
- packet-loss percentage derived from those counts;
- server load observed for that check;
- route/probe metadata already returned by the probe service;
- whether the measurement followed a retry;
- whether it represents a confirmed complete outage;
- the latest diagnostic/error text suitable for a tooltip.

The existing per-batch average latency and successful-reply count are sufficient to calculate an exact reply-weighted average across batches:

`pooled latency = sum(batch average latency × batch successful replies) / sum(successful replies)`

Individual RTT values do not need to cross IPC for this design.

## Retention and scoring windows

Two windows serve different purposes:

- **History retention:** keep recorded measurements for up to 10 minutes.
- **Health score:** use only the newest three recorded measurements.

A history entry expires when it has received no new recorded measurement for 10 minutes. Expiry removes both its graph data and score state. The next check starts a fresh provisional history.

Row unloading, scrolling, closing search, or navigating elsewhere does not clear history. Closing the application does.

## Probe cadence

Existing normal cadence remains unchanged:

- Connected server: every 30 seconds.
- Visible candidate server row: every 60 seconds.

No extra periodic traffic is introduced for the graph or aggregation.

## Shared probe coordination

The history store also coordinates requests per history key:

- At most one probe operation may be in flight for an endpoint.
- Requests arriving while a probe is running await or reuse that operation rather than sending another probe.
- A connected panel and visible search row requesting the same endpoint therefore share the same result.
- Existing UI timers may continue to request on their own cadence; the coordinator handles overlap.
- Cancellation caused by view disposal or application shutdown is not recorded as server health.

## Complete-failure retry

A complete failure is a finished health attempt with no usable reply evidence, including a returned zero-reply result or a terminal probe error. Cancellation and deliberate shutdown are excluded.

Behavior:

1. Do not immediately add the failed attempt to history.
2. Keep the previous aggregate visible, if one exists, and expose a `Rechecking` state.
3. Retry exactly once after 5 seconds.
4. If the retry succeeds or partially succeeds, record only that retry result as the batch.
5. If the retry also fails completely, record one synthetic four-attempt measurement with zero successful replies and therefore 100% packet loss.
6. Represent the original failure and retry as one confirmed-outage graph point, not two separate health samples.

A partial result, such as two replies out of four, is valid evidence and is recorded immediately. Partial loss does not trigger the fast retry.

The retry is part of the same logical batch. A concurrently scheduled normal request is coalesced rather than creating a second probe.

## Aggregation

For the newest one to three recorded measurements:

- **Attempted replies:** sum each measurement's total attempts.
- **Successful replies:** sum each measurement's successes.
- **Packet loss:** `(attempted - successful) / attempted × 100`.
- **Latency:** reply-weighted average of each batch's average latency.
- **Load:** newest available server-load value.

With three normal batches, the score reflects 12 attempted replies. One isolated miss is therefore 1/12, or approximately 8.3% loss, rather than 1/4 or 25%.

A confirmed outage contributes four missed attempts and no invented latency. It affects reliability while leaving latency calculated only from actual replies.

## Grade calculation

Retain the existing score components and grade thresholds, but feed them the pooled values:

- latency score from pooled latency;
- reliability score from pooled packet loss;
- load score from the latest known load;
- weighted score: 45% latency, 45% reliability, 10% load;
- existing packet-loss caps applied to the pooled loss percentage;
- existing Excellent, Good, Fair, and Poor thresholds.

This deliberately changes the evidence window, not the established meaning or weighting of the grades.

A confirmed outage does not vanish after one clean check. It remains in the newest-three window until two additional recorded batches push it out naturally.

## Provisional confidence

A grade is shown immediately after the first recorded batch.

Until three measurements exist, the UI identifies the result as provisional:

- `Based on 1 of 3 checks`
- `Based on 2 of 3 checks`
- `Based on 3 checks`

The compact meter remains readable; confidence detail belongs in the health tooltip/flyout rather than expanding every search row.

## Displayed values

The visible grade, latency, and packet-loss values all use the same newest-three aggregate. They must not mix a historical grade with latest-only numbers.

The hover detail also shows the most recent batch separately so a user can distinguish the recent trend from a fresh hiccup.

Recommended detail fields:

- aggregate grade, latency, loss, load, and confidence count;
- latest batch timestamp;
- latest batch average latency and successful/attempted replies;
- whether a retry occurred;
- confirmed-outage state or diagnostic text;
- route information already supplied by the service.

## History graph

Hovering either a server-row meter or the connected-server health panel opens the same history flyout for that endpoint.

The graph uses only already-recorded measurements and can display up to the retained 10 minutes:

- latency as the primary line, with gaps where no replies succeeded;
- packet-loss events as markers or ticks;
- load as a visually secondary line;
- confirmed complete failures as clear 100%-loss events;
- the newest three measurements identified as the points currently driving the grade.

Point details include:

- timestamp;
- average latency when available;
- successful and attempted replies;
- packet loss;
- load;
- retry/confirmed-outage status.

The original complete failure and its five-second retry appear as one combined event.

The search row and connected panel must render graph data from the shared store rather than maintaining independent chart histories.

## State updates

Consumers subscribe to immutable history snapshots or aggregate-change notifications. A snapshot contains:

- retained graph measurements;
- newest-three aggregate;
- confidence count;
- current checking/rechecking state;
- latest diagnostic metadata;
- expiry information where useful for scheduling.

Updates should be marshalled to the UI thread by the presentation layer rather than making the store dependent on a specific view.

## Memory and cleanup

The store is session-scoped and bounded:

- prune measurements older than 10 minutes whenever history is read or updated;
- remove inactive keys after 10 minutes without a recorded result;
- release subscriptions when controls unload;
- keep the underlying history entry independent of control lifetime.

This permits row virtualization without allowing unbounded per-server state.

## Error presentation

During the first failed attempt:

- preserve the last valid aggregate;
- indicate that a retry is in progress;
- do not lower the grade yet.

After two complete failures:

- add the confirmed 100%-loss batch;
- update the aggregate and graph normally;
- retain the latest diagnostic information in the tooltip.

When no prior history exists, the initial failure/retry sequence shows `Checking` or `Rechecking` rather than inventing a grade before confirmation.

## Testing strategy

Use a fake clock and fake probe executor so timing, expiry, and retry behavior are deterministic.

### Aggregation tests

- One missed reply across three four-attempt batches produces 1/12 loss, not 1/4.
- Three clean batches can still earn Excellent under the existing thresholds.
- Latency is weighted by successful replies rather than by batch count.
- A zero-reply confirmed outage contributes four misses and no latency value.
- The newest load value is used.
- Only the newest three recorded measurements affect the grade.
- A bad batch remains represented until it rolls out after two later batches.

### Confidence tests

- One recorded batch produces `Based on 1 of 3 checks`.
- Two recorded batches produce `Based on 2 of 3 checks`.
- Three or more retained batches produce `Based on 3 checks`.

### Retry tests

- A complete first failure schedules one retry after five seconds.
- No failure measurement is recorded before the retry resolves.
- A successful retry records one successful batch marked as retried.
- A partially successful retry records its actual counts.
- Two complete failures record exactly one four-attempt, 100%-loss batch.
- Partial loss on the initial check does not retry.
- Cancellation and shutdown do not create synthetic loss.

### Coordination tests

- Simultaneous connected-panel and search-row requests issue only one service probe.
- Both consumers receive the resulting shared snapshot.
- A request arriving during the five-second retry sequence is coalesced.
- Different server identities or endpoints do not share history.

### Lifetime tests

- Row unload/reload preserves session history.
- Leaving and returning to search preserves history.
- Measurements older than 10 minutes are pruned.
- A key inactive for 10 minutes expires and restarts provisionally.
- Application-session disposal clears all history and subscriptions.

### Graph tests

- Graph points mirror retained measurements in timestamp order.
- The newest three score-driving points are identified correctly.
- A confirmed outage appears as one combined event.
- A no-reply point does not fabricate latency.
- Connected and search views receive identical graph history for the same key.

### Regression tests

- Existing direct-adapter route and WFP behavior remains unchanged.
- Existing server-row and connected-panel refresh cadences remain unchanged.
- The graph causes no additional periodic probe calls.
- Existing grade thresholds and component weights remain unchanged.

## Implementation boundaries

Likely implementation areas include:

- a new shared history/coordinator service in the client/common health layer;
- immutable measurement, aggregate, snapshot, and key models;
- adapting `ServerHealthControl` to consume shared snapshots;
- adapting `ConnectionStatusHeaderViewModel` and its XAML to consume the same snapshots;
- a reusable health-history flyout/graph presentation;
- dependency registration and lifecycle management;
- focused unit tests plus existing health-probe regression suites.

Exact class placement and naming should follow the current solution's dependency-injection and UI conventions after inspecting the latest `marc/proton` source.

## Risks and mitigations

### Misclassifying local probe failures as server outages

The required double-failure rule reduces one-off route, IPC, or service hiccups. Diagnostic metadata remains visible so a confirmed result can still be understood. Cancellation and shutdown are explicitly excluded.

### Duplicate traffic from two UI surfaces

Per-key in-flight coalescing ensures the connected panel and search row cannot independently probe the same endpoint at the same moment.

### Stale history

A 10-minute inactivity expiry prevents old good or bad behavior from representing a server long after it stopped being observed.

### UI clutter

Rows keep their compact meter. Confidence, latest-batch detail, and the graph live in the hover flyout.

### Graph complexity

The graph consumes the same immutable snapshots as scoring and adds no probe behavior. Scoring should be implemented and tested independently from rendering.

## Acceptance criteria

The change is complete when:

1. Both health surfaces use one session-wide history for the same server endpoint.
2. Grade, latency, and loss are pooled from the newest three recorded batches.
3. Provisional grades appear immediately with 1-of-3 or 2-of-3 confidence.
4. A complete failure retries once after five seconds and becomes one 100%-loss batch only after the retry also fails.
5. Partial loss is recorded without retrying.
6. History survives row/view lifetime and expires after 10 minutes of inactivity.
7. Hovering either health surface shows the shared 10-minute graph and latest-batch detail.
8. Concurrent requests for the same endpoint do not duplicate probes.
9. No additional periodic probe traffic is generated for history or graphing.
10. Automated tests cover aggregation, retries, sharing, expiry, graph data, and regressions.