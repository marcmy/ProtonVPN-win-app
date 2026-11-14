# Shared firewall filter ownership

Folder split-tunnel rules and direct server-health probes share the service's
dynamic WFP sublayer. SplitTunnel's state lock serializes its own policy changes;
it does not serialize independent probes or other firewall consumers.

Sublayer therefore has no managed collection of created filter IDs. WFP is the
authoritative inventory used by enumeration and sublayer cleanup. Each app rule
or temporary probe lease owns the IDs it receives and removes those IDs during
teardown. Native creation returns its ID directly, without a second shared
bookkeeping operation that could fail after the native filter has been created.

For an app path with multiple layer filters, a creation failure removes the
filters already created for that path and its registration. Invalid arguments
retain the existing logged/skip behavior; other failures still propagate. A
subsequent Apply can retry the path rather than skipping a partial installation.

## Regression tests

ProtonVPN.NetworkFilter.Tests compiles the production Sublayer, AppFilter, and
ServerHealthPermitManager sources against an inert native/session boundary. It
does not reference P/Invoke or open a real WFP session. This keeps concurrency and
failure tests safe on CI without adding a test-only abstraction to production.

Coverage includes a serial app-policy worker alongside 32 parallel permit
workers (8,192 probe leases), native ID/policy forwarding, all 18 filter creators,
lease ownership/idempotent teardown, and rollback/retry after partial app-filter
creation. A deterministic guard also rejects reintroducing an unowned managed
filter-ID collection. The tests run in the normal Core regression lane, supported
broad suite, and fork coverage report.

These tests validate the managed callers and their ownership contract. They do
not emulate WFP classification, native DLL implementation, or real traffic.
Installed VPN-on validation still needs folder Apply and disconnect while
active-tab probes are running, in both include and exclude modes.
