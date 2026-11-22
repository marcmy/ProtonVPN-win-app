# Server ping and loss telemetry

Browse all servers opens immediately from the catalogue. All, Secure Core, P2P and Tor each retain their full eligible discovery scope. Search, sorting and the maximum-ping filter work within that scope. Selecting an eligible row opens details and allows an explicit connection or server switch.

## Discovery and fixed checks

- Discovery admits up to 32 endpoint checks. A valid first reply is available immediately as a ping estimate, and one fallback follows an actual first timeout by 25 ms. New endpoints and visible or selected rows get priority. Discovery covers the full feature tab even when the results are filtered.
- Detailed checks send four echoes about 100 ms apart, each with its own 500 ms deadline. A successful reply can arrive while a different echo is pending. The service bounds native echoes at 32 and keeps capacity for discovery by limiting fixed bursts to four at a time. Passive refinement waits for the discovery pass to finish.
- The active list, visible rows, selected row and connected card share endpoint observations for the same physical route, with separate quick and fixed work. Closing a list or changing feature tabs releases its interests. A shared check with other subscribers can still finish. Cancellation keeps resources owned until the native sends have settled.
- Discovery refreshes the active catalogue after five minutes and backs off failures for ten minutes. Visible rows and the connected card refine about once a minute. A selected row can request refinement after discovery ends. Probe failures surface immediately and create no synthetic outage or retry sample.

## What the card and list show

- Ping starts with a single valid RTT. After three successful fixed-check replies, it uses the median of applicable replies. Details include RTT variation when enough replies are available.
- The rolling window uses up to six qualified fixed checks in the last ten minutes. A check is qualified when all four attempts are individually classified as replies or actual deadline misses. Discovery, old IPC results, network errors, unverified routes and cancellation contribute zero packets to that denominator.
- Details show the number of missed echoes out of the number sent. A percentage appears after at least three appropriately spaced checks when the endpoint has replied on the current route. The card and its History flyout use the same percentage and count. History labels the most recent check separately and distinguishes replies, timeouts and unavailable attempts.
- These measurements are round-trip ICMP to the physical entry endpoint. Server load comes from the catalogue, and it is a separate signal. A server with no observable ICMP replies may still accept VPN traffic. These probes do not measure traffic inside the tunnel or change connection selection.

## Storage and routing

The app keeps the last usable RTT per endpoint in `%LOCALAPPDATA%\ProtonVPN\ServerHealth\ping-cache-v1.json`. Writes are atomic and debounced. The cache is capped at 65,536 entries and 30 days. Restored values are historical, with no current loss estimate. A network change starts a clean live window, and late results cannot repopulate it. A verified route change detected by the service similarly resets endpoint history.

The client chooses candidate addresses from its catalogue and the current-server address from the selected physical server. The service verifies the effective IPv4 route before and after a probe, keeps destination-specific route and firewall leases, and removes only resources it owns. The current service does not have its own catalogue authority. New typed observations are optional in IPC so a mixed-version client can still read the legacy latency field.

The nominal fixed-check send window is 800 ms before route setup or queueing. Whole-tab discovery still depends on the catalogue, endpoint replies, timeouts and route setup. Compare the installed baseline and candidate on the same machine before claiming a live timing improvement.
