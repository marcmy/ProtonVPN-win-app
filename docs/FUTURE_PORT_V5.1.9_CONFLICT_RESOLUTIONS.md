# Proton VPN 5.1.9 early-source port

Status: reconstructed source port implemented; local validation below. Not
installed or accepted for production use. Public-source reconciliation is pending.

## Provenance and scope

- Fork baseline: `cacc64e847d58f27e5e2e488d3b9c7a9af97a108` (`marc/proton`).
- Official public source baseline: `v5.1.8`, `d2a4f8bc92a0fd296943a7cdd15f4f870c8a87f9`.
- Target: official Windows x64 **5.1.9 EarlyAccess**, released 2026-10-19.
- Installer SHA-256: `8454eba06a37c0a636ec8dcfe6a7a823a68a78ee8fe2eeda2d651cd5cd6c3d70`.
- Binary comparison: [archaeology run 39334105184](https://github.com/marcmy/ProtonVPN-win-app/actions/runs/39334105184), comparing official 5.1.8 and 5.1.9 with ILSpy.
- No official 5.1.9 source tag was available when work began. Reconstructed behavior is not represented as original upstream source.

Preserve the fork's ping refresh, app/folder patterns, include/exclude parity,
service-owned folder preparation, domain rules, live Apply, and patch safety.
Do not import the separate unfinished native routing/driver prototype. The
historical-backport cleanup and fail-closed source-reconciliation path remain
unchanged. Resolve substantive overlaps individually; do not prefer upstream
merely because it is newer.

## Review ledger

| Area | Official 5.1.9 intent | Fork-aware resolution | Verification |
| --- | --- | --- | --- |
| Exclusion reconnect lifecycle | Retain redirects/authorization while reconnecting; avoid rebuilding identical redirects. | Preserve explicit apps, discovered folder snapshots, resolved domains, and existing authorization across a reconnect. Refresh redirects only when paths or physical IPv4/IPv6 identity change. | Reconnect/connecting/physical-address regressions; focused suite 32/32 before the additional non-local-path case. |
| Applied routes | Remove routes belonging to the configuration actually installed. | Fork already tracks an effective routing config, including resolved domains; retain this stronger behavior. | Existing live-Apply/domain route regressions retained. |
| Service lifecycle | Pass reconnect state into split-tunnel disconnect handling. Other reviewed lifecycle differences are compiler/build-path metadata. | Forward reconnect context rather than treating every transient disconnect as a final disconnect. | Service suite 170/170, including existing live-Apply, folder, and domain regressions. |
| App authorization replacement | Replace exclusions without a remove-all gap. | Transactionally create the new WFP app policy and remove the old one; restore managed ownership on failure. Keep include-mode behavior. | Replacement, duplicate paths, rollback/retry, and unsupported non-local-path tests; inert native-boundary regressions retained. |
| Remote-address authorization | Upstream adds a replacement API. | Retain the fork's existing transactional, empty-set-capable reconciliation instead of substituting the simpler upstream implementation. | Existing address/domain route coverage retained. |
| VPN config files | Share protected config-file writing between OpenVPN and WireGuard. | Retain SYSTEM-only read/write, administrator delete-only access, and a protected DACL; extend the same writer to OpenVPN. | Descriptor regression does not create files, read real keys, or require elevation. |
| Process arguments | Use separate argv tokens instead of building quoted command strings. | Tokenize OpenVPN options, use ArgumentList, parse release-supplied installer arguments with Windows rules, and keep legacy APIs for fork compatibility. Do not reinterpret legacy cmd.exe command strings. | OpenVPN argument tests plus quote/empty-argument/parser regressions; updater and opt-out integration tests. |
| Server-cache identity | Track country/IP separately from coordinate-dependent scores; retain complete server data and the binary status snapshot. | Apply 5.1.9 cache behavior without changing the fork's ping sampler, cache, filtering, tab scope, or cadence. Keep the readonly/init-only DeviceLocation model. | Five cache regressions: IP invalidation, hidden servers, failed binary loads, NotModified freshness, and local coordinate recomputation. |
| Server refresh sequencing | Refresh overdue loads before choosing a server; continue in the background after a short deadline. | Serialize refreshes, recheck freshness under the semaphore, wait at most the configured one second before connecting. Refresh loads independently after a NotModified logical-list response. | Four updater regressions plus pre-selection connection ordering. |
| Sleep/resume recovery | Repeat service state and refresh plan/certificate/servers before reconnecting, with a five-second refresh deadline. | Port the ordered asynchronous path, retaining fork connection/Guest Hole behavior and exception reporting. | Resume-order regression; connection suite 71/71. |
| Issue-report forms | API length constraints, touched-state errors, reset after success, packaged fallback form. | Retain legacy optional-field compatibility, reject invalid constraints, retry API access after fallback, and notify dependent UI properties when limits change. Use the official embedded fallback JSON and length hint. | Four model tests and four provider cases; feedback suite 41/41. |
| SSO and activation diagnostics | Versioned SSO bootstrap endpoint and caller-aware activation logs. | Change only the endpoint to auth/v4/sso; preserve the fork's pinned HTTP bootstrap. Retain existing window lifecycle and use lambdas where optional caller parameters replace method groups. | Full client compilation; integration suite 56/56 before final package validation. |
| Guest Hole release configuration | Official 5.1.9 rotates release-injected build constants. | Do not copy vendor build constants into source. Preserve the matching official Builds.Variables assembly and the fork's runtime constant lookup, rather than inlining public-source placeholders. | Existing Guest Hole regressions and FastPatch preservation checks retained. |
| Optional connected-server checker | The class is absent from the shipped 5.1.9 assembly. | Retain the existing feature-flagged checker rather than infer that an absent/trimmed type authorizes deleting fork behavior. Preserve physical-server status from logical-list responses. | Existing connection and mapper tests retained. |
| Native deployment | Official OpenVPN is 2.6.23; the signed callout driver is unchanged from 5.1.8. | Require an official 5.1.9 installation underneath FastPatch. Do not package a locally rebuilt callout driver, change Windows protections, or claim the separate Apex issue is fixed. | Verified official callout SHA-256: ba788ffdc1bd95dd4aa0eccb1b43a5e512eae09fb2d8d623e7266bde65d79e00. |

The binary comparison excludes generated WinRT exposure tables, regenerated regex
identifiers, assembly versions, and compiler-injected logging/caller paths. Those
are regenerated by the normal source build, not copied into maintained C#.
The original fork ping and folder-scanning implementations are not replaced with
decompiled upstream files. The legacy RequiredIf attribute is retained for source
compatibility, but the report fields now use their explicit constraints.

REA execution-free metadata established the service artifact identity (SHA-256
920b9bf2f1bae2368ceed332575eb210915fe680f7e2bada36427aada97114da;
evidence ev_1665f115dbdc52b2f8da06b98e0c4d98990bf672f15ac709ff04b0c6548c5bfc).
Method reconstruction uses pinned ILSpy 11.1.0.9782. Static reconstruction does
not establish runtime acceptance.

## Local validation

- Full client and service Release/x64 builds: zero warnings and errors.
- Core regression matrix: **908 passed** across 10 isolated test projects.
- UI/localization: **114 passed, 1 skipped** across two projects.
- Integration: **56 passed**; final stamped-source rerun recorded with the build logs.
- Public-source watcher selection/ancestry: **10 passed**.
- Patch tooling: passed, including known-backport subtraction, preservation of
  later same-file fork edits, and fail-closed semantic conflicts after promotion.
- Complete FastPatch's secure-staging regression requires an administrator token
  on Windows and was not run successfully in this non-elevated desktop session.
  Its default CI requirement is unchanged; do not treat this as a local pass.
- The final local package is built from isolated client outputs and the matching
  service outputs, excluding test DLLs and the release-private Builds.Variables
  assembly. Package creation and CI are separate from installing it.

## Acceptance boundary

Source tests, build/package validation, official binary identity, and installed
network/game acceptance are separate gates. Reconnect fixes alone do not prove
the previously observed dual-stack/Apex routing issue is resolved. No live
driver, network setting, private key, or installed application is changed by
this source port.
