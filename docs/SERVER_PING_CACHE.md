# Cached server pings

Server browsing and ping filtering read saved measurements immediately. Selecting
a threshold or hovering a health badge does not start a probe. Opening a list
starts a bounded first pass for unmeasured candidates, without delaying display.
Unknown pings are shown as unmeasured and excluded by an active threshold. Turning
the filter to All always exposes unmeasured servers.

## Refresh policy

- One shared client-side store deduplicates list and connection-panel requests by
  logical server ID **and actual probe address**. Different endpoints must not
  inherit each other's measurements.
- A single list scheduler wakes every 30 seconds and admits at most two due
  servers sequentially, with one second of pacing between them. Values become due
  after five minutes; complete failures get a ten-minute list-refresh cooldown.
- Opening a feature tab (including P2P), search result, all-server list or expanded
  server group requests an immediate first pass: at most 24 **uncached** endpoints,
  with 250 ms pacing and no new admissions after 20 seconds. Cached values appear
  immediately; each new result is published and filtered as it completes. A check
  already admitted can finish after the deadline or list closure.
- Candidates are ordered by Proton's existing ascending fastest-server score,
  then load, before the ping threshold is applied. Maintenance and excluded
  servers are skipped. Cached endpoints are skipped before the initial-pass cap,
  so subsequent openings can discover a different small selection.
- Only one list pass runs at a time; a newer list supersedes pending work from the
  previous one. Rapid list changes are capped at 48 initial admissions per minute.
  The pass does not do the normal five-second no-reply retry; an unanswered first
  check is not labelled a confirmed outage. Normal periodic checks retain retry
  and failure-cooldown behavior.
- A list owns refresh interest in its initial candidates plus at most eight
  previously measured candidates. Closing it releases that interest and stops
  queued first-pass checks. Loaded rows also register interest and unload releases
  it. Threshold changes only reapply measurements; they never restart discovery.
- Scheduler interest is bounded to 64 endpoints, with newer viewed rows replacing
  older interests if full. There is no all-server sweep.
  No eligible interests means no list probes, even though the cheap timer remains.
- The connected-server panel refreshes once a minute while active. Repeated load
  or connection-statistics messages for an unchanged endpoint only restore the
  cached display. It shares the store's one-probe concurrency limit and one-minute
  deduplication interval with the list scheduler.
- Network-address changes mark measurements stale; they do not trigger immediate
  scans. Background refresh remains within the same budget.

## Persistence and meaning

The newest completed measurement per endpoint is saved in
`%LOCALAPPDATA%\ProtonVPN\ServerHealth\ping-cache-v1.json`. Writes are debounced
and atomically replace the file. Retention is 30 days and at most 4,096 endpoints.
Unreadable, malformed, oversized or invalid telemetry is ignored, never allowed
to prevent startup. Restored values are marked saved/stale and keep their original
timestamps. They are estimates, especially after changing networks.

The ten-minute rolling health graph remains separate: restoring one saved ping
does not invent historical samples or six-check confidence. Server health still
uses ICMP through the physical adapter, not RTT through the VPN tunnel or to a
game server. No-reply measurements do not prove the VPN server is offline.
The service's catalogue validation, scoped route/filter permits, ICMP sampling
and cleanup are unchanged. Pings are telemetry only and do not select or change
VPN routes, split-tunneling rules or connection behavior.

## Regression coverage

Common UI tests cover cache-only filtering, restart/endpoint identity, timestamps,
retention, invalid input, network changes during an in-flight probe, cancellation,
deduplication, immediate bounded discovery, cached-candidate skipping, incremental
filter matches, deadline/pacing/global admission budgets, list replacement and
closure, interest limits and failed-server cooldown. Full client compilation
validates the XAML bindings and the plain "Ping" label.
Installed-app responsiveness and resource use still require a live check.
