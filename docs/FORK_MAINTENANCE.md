# Fork maintenance rules

This document defines the behavior that must survive dependency upgrades and
future upstream ports. The source and focused regression tests remain the final
authority.

## Preserved feature invariants

### NAT-PMP and app port forwarding

- Preserve NAT-PMP discovery, lease renewal, and mapped-port publication.
- Preserve app-specific port-forwarding routes while connected and configured.
- Serialize route reconciliation, remove a route if state changes during
  creation, and retain failed deletions for a later retry.

### Split tunneling

- Preserve live service-side application of split-tunnel settings.
- Preserve the configured IP/CIDR path and standard include/exclude behavior.
- In standard exclude mode, accept `example.com` and `*.example.com` as one
  suffix-boundary rule; never match `badexample.com`.
- Observe only successful Windows DNS-cache A/AAAA records. Version 1 applies
  IPv4 exclusions and does not inspect HTTPS, TLS, QUIC, browser traffic, or
  private DNS.
- Clamp observed TTL to at least 60 seconds, add a five-minute grace period, cap
  the total lifetime at one hour, and keep shared IPs until every owning rule
  expires or is removed.
- Remove temporary domain-derived filters and routes when rules are removed,
  split tunneling is disabled, or the VPN disconnects.

### Server list and health

- Preserve the fork's server search/list presentation and server-health history.
- Keep probe permits and routes scoped to the probe lifetime and clean them up
  after success, failure, or cancellation.
- Serialize duplicate probes for one address, bound global concurrency, and
  suppress duplicate UI probes within the configured freshness interval.

### Patch and release tooling

- Patch payloads contain only required first-party `ProtonVPN*` assemblies and
  client resources, never a replacement official runtime.
- Reject conflicting files and unsafe output paths before deleting or staging.
- Require a versioned manifest with an exact file set, sizes, and SHA-256 hashes.
- Validate the payload before elevation, service shutdown, backup, or overlay.
- Build with read-only repository credentials; isolate any branch push in a
  separate least-privilege job; retain SHA-pinned third-party Actions.

## Dependency policy

Dependabot may open patch and minor updates. Major upgrades require a manual
compatibility review. In particular, the 5.1.5 installed runtime supplies the
strongly named `log4net` 3.2.0 assembly, so this fork must not build a patch
against an incompatible log4net identity merely because a newer package exists.

Validate dependency changes with the fast-patch workflow and against an official
installation of the target version. A successful compile alone is not sufficient
for an overlay patch.

## Upstream release procedure

1. Record the current upstream base and compare it with the entire maintained
   fork. This complete delta is the preservation checklist, including additions,
   edits, and deletions.
2. Sync the real new upstream release; changing only assembly-version text is not
   an upstream port.
3. Port the complete fork delta onto the new base. Where both sides changed the
   same behavior, take upstream's implementation only when it is demonstrably an
   improvement and retains every fork capability.
4. Resolve conflicts narrowly. Do not replace whole files when an isolated merge
   preserves both upstream and fork work.
5. Run patch-tooling tests, fork regressions, service/client tests, Release x64
   builds, CodeQL, dependency/SBOM scans, and patch payload validation.
6. Test the built overlay against an untouched official installation with the
   exact target version. Exercise connection/disconnection, NAT-PMP renewal,
   app-port routing, live split-tunnel changes, domain expiry/cleanup, server
   list/health behavior, and rollback.
7. Verify the GitHub build-provenance attestation before distributing an
   artifact.

Official application updates can overwrite the overlay. Keep automatic security
updates enabled and publish a matching fork patch for each supported upstream
release instead of applying old binaries to a new runtime.
