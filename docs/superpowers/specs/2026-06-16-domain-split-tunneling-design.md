# Domain Split Tunneling Design

Date: 2026-06-16
Branch: app-natpmp-mapping-phase1

## Goal

Add domain-based split tunneling for sites that block VPN traffic, without excluding an entire browser process and without permanently excluding broad CDN subnets.

The feature should let a user enter a domain such as `example.com` in the split tunneling address list. When an app resolves a matching domain through system DNS, the Proton VPN service should temporarily exclude the returned IP addresses from the VPN using the existing split-tunnel IP route/filter path.

## Non-Goals

- Do not inspect, decrypt, or modify HTTPS traffic.
- Do not infer domains from TLS SNI, HTTP headers, QUIC, or browser internals.
- Do not add a browser extension or browser-specific integration in the first version.
- Do not permanently exclude CDN ranges or whole provider subnets.
- Do not support full URLs such as `https://example.com/path` in the first version.

## DNS Requirement

This feature requires browsers and apps to use system DNS. Browser DNS-over-HTTPS or app-private DNS can bypass the service's DNS observation path, so those modes are unsupported for domain split tunneling in the first version.

The app may document this limitation in UI text or diagnostics, but the first implementation does not need to automatically reconfigure browsers.

## User-Facing Rule Semantics

Split tunneling address entries can be either:

- IP/CIDR entries, which keep the existing behavior.
- Domain entries, which become dynamic DNS-observed rules.

Domain matching is suffix-boundary aware:

- `example.com` matches `example.com`.
- `example.com` matches `www.example.com`.
- `example.com` matches `login.example.com`.
- `example.com` matches `api.foo.example.com`.
- `example.com` does not match `badexample.com`.

The UI/service may also accept `*.example.com` as an alias for `example.com`. Both forms should have the same effective behavior.

Domain rules are case-insensitive and should be normalized to lowercase without a trailing dot. Wildcards are only accepted in the leading `*.` form.

## Runtime Behavior

When split tunneling is active in standard exclude mode:

1. Exact IP/CIDR entries are applied as stable configured exclusions through the existing `_permittedRemoteAddress` and `_excludedIpRouteManager` behavior.
2. Domain entries are kept as active match rules.
3. The service observes system DNS answers.
4. If a DNS answer's queried hostname matches an active domain rule, each returned IP address becomes a temporary domain-derived exclusion.
5. Temporary exclusions are added to the same route/filter backend used by manual IP exclusions.
6. When a temporary exclusion expires, it is removed only if no active domain rule still owns that IP address.

When split tunneling is disabled, VPN disconnects, or a domain rule is removed, stale domain-derived exclusions must be removed promptly.

## Expiration Policy

Temporary domain-derived exclusions should use DNS TTL when available.

Effective lifetime:

- Base lifetime: DNS TTL.
- Minimum lifetime: 60 seconds.
- Grace period: 5 minutes after TTL expiry.
- Maximum lifetime: 1 hour.

When the same domain resolves again, matching IP ownership and expiry are refreshed.

If two or more domain rules resolve to the same IP, that IP remains excluded until every owning rule has expired or been removed.

This policy avoids permanent exclusions while reducing mid-session breakage for HTTPS tabs or long-lived browser connections.

## Architecture

### Domain Rule Parser

Add a service-side parser that classifies split tunneling address strings as:

- `NetworkAddress` for IP/CIDR entries.
- `DomainRule` for exact/suffix domain entries.
- invalid entries, which are ignored and preferably logged.

The client already accepts hostnames in split tunneling address entries. The service should remain authoritative because settings can arrive from persisted state or older clients.

### Domain DNS Observer

Add a service-side component, tentatively `SplitTunnelDnsObserver`, responsible for:

- tracking active `DomainRule` values,
- observing system DNS answers,
- matching answer hostnames against active rules,
- publishing temporary IP additions and removals.

The implementation should first investigate existing Proton DNS callout/NRPT plumbing. If no practical DNS-answer observation point exists, a conservative first implementation can periodically resolve active domain rules through system DNS and apply the same TTL/expiry model where available.

### Dynamic Exclusion Manager

Extend or wrap the existing split-tunnel IP exclusion path so configured IPs and domain-derived IPs are tracked separately:

- configured IP/CIDR exclusions persist while the setting is active,
- domain-derived exclusions expire dynamically,
- duplicate ownership is reference-counted or owner-tracked,
- removals do not tear down an IP still required by another active source.

This avoids mixing transient DNS-derived state into the user's persisted settings.

## Data Flow

1. User adds `example.com` to the split tunneling address list.
2. Client stores/sends `example.com` in the existing split tunneling IP/address setting path.
3. Service classifies `example.com` as a domain rule.
4. Browser resolves `login.example.com` through system DNS.
5. DNS observer sees `login.example.com -> 203.0.113.10` with TTL.
6. Domain rule matches the answer hostname.
7. Service adds `203.0.113.10/32` to the excluded route/filter set.
8. Browser opens `https://login.example.com` outside the VPN.
9. The temporary exclusion expires after TTL plus grace unless refreshed.

## Error Handling

- Invalid domain entries should not crash split tunneling; ignore and log them.
- DNS observation failures should leave configured IP/CIDR exclusions working.
- Failed route/filter additions should be logged and retried on the next matching DNS answer or periodic refresh.
- If DNS returns only IPv6 and the current split-tunnel route backend only supports IPv4 exclusions, IPv6 answers should be ignored or logged until IPv6 routing support is explicitly designed.

## Testing

Unit tests:

- domain parser accepts `example.com` and `*.example.com`,
- parser rejects URLs, embedded wildcards, slashes, and blank values,
- suffix-boundary matching allows subdomains and rejects `badexample.com`,
- expiration policy clamps low/high TTLs and adds grace,
- dynamic exclusion ownership keeps shared IPs until all owners expire.

Service-level tests:

- configured IP entries still produce stable exclusions,
- domain-derived IPs are added without persisting to settings,
- removing a domain rule removes only its derived IPs,
- disconnect/disable clears all domain-derived exclusions.

Manual validation:

- with browser system DNS enabled, add a VPN-blocking domain and confirm HTTPS traffic to that domain exits outside the VPN,
- confirm unrelated browser traffic remains protected by the VPN,
- enable browser DoH and confirm the limitation is observable/documented rather than silently promised.

## Open Implementation Questions

- Can existing DNS callout/NRPT code observe DNS answers reliably enough for TTL-based dynamic routing?
- If DNS answer observation is not available, what is the best first fallback: periodic system DNS resolution, event-driven cache polling, or a small local DNS proxy?
- Should `example.com` also trigger an immediate initial resolve for both `example.com` and `www.example.com`, or should all additions be DNS-observed only?
- What is the safest place to surface the system-DNS requirement in the UI without overloading the split tunneling page?
