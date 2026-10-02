# Recursive folder rules

## Fork implementation (2026-10-02)

Both standard/exclude and inverse/include modes have independent persistent
folder lists with Add, Browse, enable/disable and Remove controls. No executables
are imported into the UI's app list. Existing executable wildcard rules are
retained without migration or automatic deletion.

The service receives active folder paths via settings IPC and owns discovery;
the UI does not need to remain open. Initial scans on connect/Apply are followed
by coalesced filesystem notifications (250 ms) and a 15-second reconciliation
fallback. Every update rescans the tree, covering directory renames/deletions,
missed notifications and recreated roots. A generation guard discards scans of
obsolete settings. The effective app set is the case-insensitive union of folder,
explicit and wildcard rules, so overlapping owners are retained.

Folder updates rebuild redirect and IPv4/IPv6 authorization filters using the
existing split-tunnel state lock and mode-specific policy. Include-mode connecting
blocks and OpenVPN IPv6 blocks receive the same effective set. Folder-only changes
do not reset IP/domain routes, DNS protection or domain polling. This follows the
existing filter-rebuild lifecycle, not a claim of atomic cross-engine WFP updates.

Safety limits: 20 saved folders per mode, 10,000 filesystem entries per root,
10,000 combined unique folder executables, 32 directory levels. Drive roots,
Windows/system paths, broad profile/program roots, UNC/device paths and broad
pattern anchors are rejected. Linked roots/ancestors are rejected and linked
descendants are skipped. Failed/over-limit scans return no partial app set for
the affected root and emit a service-log warning. The UI checks a folder's scan
before accepting it and displays limits/discovery limitations. Failed root scans
are retried during reconciliation; they do not delete the saved folder rule.

This is asynchronous app discovery, not a native directory predicate. A program
launched immediately after creation may connect before discovery; existing
sockets can need a natural reconnect. In inverse mode an undiscovered app has
the usual non-included behavior until its filter is installed. Do not describe
this feature as guaranteed first-packet privacy protection. Scripts/documents
are covered only when the networking executable itself is under the chosen root.
Source/build/CI tests do not replace a live test of installed WFP behavior.

Folder patterns support `*` and `?` within individual directory-name components
in both modes. For example, `C:\Apps\EA\*\Tools` matches one level between EA
and Tools, then recursively discovers executables beneath every matched Tools
folder. `**` and traversal components after the first wildcard are rejected.
The fixed prefix must itself pass the specific-root safety checks; `C:\*` and
patterns anchored at a broad profile/program root are rejected. Expansion and
recursive scans share one 10,000-entry budget per rule, not a fresh budget for
every match. A failure returns no partial coverage for that pattern.

The saved entry stays a pattern. A valid pattern with no matches can be added
as long as its fixed prefix exists and is accessible. The service watches that
prefix recursively, so future matching folders are discovered without Apply;
periodic reconciliation recovers missed events and recreated prefixes. Wildcard
matches skip linked directories, and matched roots are validated again before
scanning. An unmatched intermediate literal directory simply yields no matches.

## Intended behavior

Save one folder rule, e.g. `C:\Tools\Downloads`, not a one-time import of
executables into the app list. It covers networking processes whose executable
is inside that folder or its descendants, including later installations and
versioned updates. Keep folder rules distinct from explicit executable rules.

This is about the executable owning the connection, not the location of a
download, script, DLL, or document. A script in the folder executed by Python
outside it would not automatically exclude Python. A helper outside the folder
would need its own rule. Existing DNS protection must remain unchanged.

## Upstream proposal

[ProtonVPN/win-app PR #116](https://github.com/ProtonVPN/win-app/pull/116), reviewed
at `4c5a92556a50f08a98e562f1926817c59d069599` on 2026-10-02, persists separate
folder settings and uses recursive scans plus `FileSystemWatcher` to maintain
per-executable WFP filters. It does not require users to manage each executable,
but it is not a native directory predicate and new executables are discovered
asynchronously (including a 500 ms debounce).

Do not cherry-pick the proposal wholesale into this fork:

- `FolderWatcherService.OnFileRenamed` handles executable names, not a renamed
  directory tree; `OnFileDeleted` does not remove a deleted directory's children.
- `OnWatcherError` restarts events but does not reconcile changes lost during a
  buffer overflow. Recursive scans return nothing for the entire tree if an
  inaccessible descendant throws.
- `GetWatchedFolderPath` uses plain string-prefix matching without a directory
  boundary, and overlapping rules lack explicit per-rule ownership accounting.
- `VpnController` forwards changes directly to `ISplitTunnelClient`, bypassing
  `SplitTunnel`'s state lock and `IAppFilter` updates. Redirect callouts alone are
  not the complete IPv4/IPv6 firewall policy, especially with kill switch enabled.
- The watcher lives in the UI process; automatic discovery stops when the UI
  exits. Its asynchronous handlers and initial scan/watch handoff need ordered
  state changes and reconciliation.
- There are no regression-test files in this PR's diff. Large roots and reparse
  points are not bounded by a containment policy.

## Design rationale

Keep folder rules in the settings/UI, with a service-owned effective-rule layer.
Explicit paths, wildcard rules, and folder rules must contribute to the same
case-insensitive set with ownership accounting: removing one folder cannot
remove an overlapping folder's or an explicit app's filter. Apply IPv4/IPv6
authorization and redirect rules together through the existing synchronized
split-tunnel lifecycle. Reconcile on connect, Apply, folder-tree rename/delete,
watcher error, and periodically while connected; do not depend on the UI staying
open. Bounds or failed scans must be visible, not silently treated as success.

Canonicalize fully qualified paths, require a separator boundary, reject drive
roots and protected OS roots, and define an explicit junction/symlink policy.
Do not follow links out of the selected root or traverse cycles. Broad writable
folders deserve a warning: any program placed there gains the exclusion.

The enforcement guarantee is explicitly asynchronous. Scan/watch maintenance
can provide one self-updating folder rule but cannot guarantee exclusion of the
first connection from an executable launched before its filter is installed.
Strict first-connection coverage requires native-layer investigation and live
tests, not simply passing a directory or wildcard to the existing app-ID API.
Microsoft documents that the misleadingly named
[`FWP_MATCH_PREFIX`](https://learn.microsoft.com/en-us/windows/win32/api/fwptypes/ne-fwptypes-fwp_match_type)
actually matches a suffix, so it is not a drop-in directory-prefix fix.

Required regression coverage includes nested new apps, rename/move/delete of
whole trees, overflow recovery, overlapping folder/explicit rules, links and
case/boundary collisions, concurrent Apply/disconnect, UI exit, and both modes
with IPv4/IPv6 and kill switch. Live validation must include launch immediately
after creation, not just a build or a later successful download.
