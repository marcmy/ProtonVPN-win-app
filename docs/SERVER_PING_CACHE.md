# Cached server pings

Server browsing and ping filtering read saved measurements immediately. Selecting
a threshold, hovering a health badge or loading a row does not start a probe.
Unknown pings are shown as unmeasured and excluded by an active threshold. Turning
the filter to All always exposes unmeasured servers.

## Refresh policy

- One shared client-side store deduplicates list and connection-panel requests by
  logical server ID **and actual probe address**. Different endpoints must not
  inherit each other's measurements.
- A single list scheduler wakes every 30 seconds and admits at most two due
  servers sequentially, with one second of pacing between them. Values become due
  after five minutes; complete failures get a ten-minute list-refresh cooldown.
- Loaded rows register interest and unload releases it. Opening a country or
  search result also offers its first eight non-maintenance servers for five
  minutes, allowing a cold-cache filter to gain results without scanning the
  catalogue. Offered servers can finish warming after closing that list, but the
  offer expires. Filter changes do not renew offers.
- Scheduler interest is bounded to 64 endpoints. There is no all-server sweep.
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
The service's catalogue validation, scoped route/filter permits, retry behavior
and cleanup are unchanged. Pings are telemetry only and do not select or change
VPN routes, split-tunneling rules or connection behavior.

## Regression coverage

Common UI tests cover cache-only filtering, restart/endpoint identity, timestamps,
retention, invalid input, network changes during an in-flight probe, cancellation,
deduplication, scheduler admission/pacing, unloaded views, interest limits and
failed-server cooldown. Full client compilation validates the XAML bindings.
Installed-app responsiveness and resource use still require a live check.
