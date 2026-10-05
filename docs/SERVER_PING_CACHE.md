# Server pings: immediate display, full active-tab coverage

Server browsing renders immediately from the catalogue and saved measurements.
Opening All, Secure Core, P2P or Tor starts a quick first pass across the entire
eligible catalogue for that feature, not a small selection. Selecting a ping
threshold or hovering a health badge does not start another pass.

Unmeasured servers stay visible while checks arrive. Under an active threshold,
measured matches sort before pending rows; measured results above the threshold
are removed. A no-reply check never invents a latency; without successful history
it cannot match a threshold. The label is simply "Ping". Progress shows how much of the tab
has been checked or already has a recent result.

## Refresh policy

- The first pass has at most 32 checks in flight. Each quick service check returns
  after the first successful ICMP reply. If the first packet gets no reply, it
  allows one fallback attempt after 25 ms; it does not wait for four samples or
  the normal five-second retry. Attempt and success counts remain accurate.
- All measures all eligible servers. Secure Core, P2P and Tor measure only their
  own feature catalogue, including during periodic refresh. Maintenance and
  excluded servers are skipped. Candidates are ordered by Proton's ascending
  fastest-server score, then load; unknown endpoints run before stale ones.
- Fresh values are reused on reopening or switching tabs. The full active tab is
  eligible for another quick pass after five minutes. Failed endpoints have a
  ten-minute cooldown. The scheduler checks for due work every 30 seconds.
- A newer tab cancels queued old-tab checks. Closing the list stops queued checks
  and periodic passes. Already-admitted shared probes may finish and save their
  result. There is no admission deadline or small total-server discovery cap.
- Loaded rows can refine their measurements once a minute, with up to eight
  ordinary four-sample checks in flight and at most 64 visible-row interests.
  That interest limit does not limit initial or periodic whole-tab coverage.
  Unloading a row releases its interest; old-tab interests cannot escape the
  active feature scope.
- List/filter updates are coalesced at 200 ms so a reply burst does not rebuild
  a large list separately for every response. Each server's cached health
  display still receives completed measurements.
- The connected-server panel retains ordinary four-sample checks once a minute
  while active, including the normal failure retry. Repeated load or connection
  statistics for an unchanged endpoint only restore the cached display.
- List and connection-panel requests share a 32-slot client store, deduplicated
  by logical server ID **and actual probe address**, with a one-minute minimum
  repeat interval. Different endpoints never inherit each other's measurements.
- Network-address changes mark measurements stale without launching a new scan.
  Refresh still follows the active scope and the same concurrency limits.

The total pass duration depends on catalogue size, replies, timeouts and routing
setup. The list does not wait for the pass to finish, and individual successful
first replies are usable as they arrive. This is bounded parallel coverage, not
a promise that every remote endpoint will respond instantly.

## Persistence and meaning

The newest completed measurement per endpoint is saved in
`%LOCALAPPDATA%\ProtonVPN\ServerHealth\ping-cache-v1.json`. Writes are debounced
and atomically replace the file. Retention is 30 days, with room for 65,536
endpoints and a 64 MiB read-size limit. Invalid or unreadable telemetry is ignored
and cannot prevent startup. Restored estimates keep their original timestamps
and are marked stale; they can differ after changing networks.

The live latency is a weighted rolling average of up to six completed checks.
A one-reply quick check is immediately useful; later multi-reply checks refine
it using their actual successful-sample counts. The ten-minute rolling graph is
separate: restoring a saved ping does not invent historical samples or
six-check confidence.

Health measures ICMP through the physical adapter, not RTT through the VPN
tunnel or to a game server. No reply does not prove the VPN server is offline.
The service still validates destinations against the catalogue, uses
destination-specific route/filter permits, serializes probes to the same IP,
and removes only routes and permits it owns. Service-wide concurrency is capped
at 32. Pings are telemetry and do not select VPN connections or change
split-tunneling rules.

## Regression coverage

Tests cover complete 1,000-server initial coverage, 32-slot parallel bounds,
feature isolation, tab replacement/closure, cache reuse, pending-row visibility,
first-reply filtering and weighted refinement, normal retry, failed-server
cooldown, visible-row limits, cache round-trips above 4,096 endpoints, endpoint
identity, retention, invalid input, network changes, shared cancellation and
coalesced UI updates. Service tests cover quick one-reply and fallback counts,
ordinary four-sample checks, route/permit ownership, cancellation and parallel
cleanup. IPC tests cover the optional quick-mode field and legacy requests.
Full client compilation validates the XAML bindings and the plain "Ping" label.

Installed-app responsiveness and resource use still require a live check.
