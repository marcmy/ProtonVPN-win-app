# Recursive folder rules

## Fork implementation (2026-10-02)

Both standard/exclude and inverse/include modes have independent persistent
folder lists in a wide, host-constrained dialog directly underneath the apps selector, with
Add, Browse, enable/disable and Remove controls. No executables
are imported into the UI's app list. Existing executable wildcard rules are
retained without migration or automatic deletion.

Only the folder list scrolls. The short hint, Add/Browse controls, validation and
service status, and Close footer stay fixed. Long paths use ellipsis with a full
path tooltip, keeping removal accessible. The top X and footer Close use the
same hide command; closing the dialog cancels pending add-folder validation.

The hover panel has the same mode-specific folder count and a Manage folders
row between apps and IP addresses. It closes the flyout, navigates through the
normal settings/upsell/profile workflow and requests the same dialog once the
settings page is loaded and ready. It does not create a second independent
editor or silently apply unsaved settings. Folder changes in either mode update
the hover summary through the existing settings-change notifications.

Add/Browse asks the service to discover the new folder before accepting the rule.
It reports progress and supports cancellation without applying unsaved settings.
The service retains a bounded, short-lived preparation snapshot (ten minutes),
so Apply can immediately install those discovered apps without waiting for a
second scan. Expanded executable paths are never supplied by the UI or persisted
as individual app rules. Apply retains snapshots for unchanged folders and drops
removed owners immediately; unrelated settings changes do not rescan healthy rules.
If a preparation expired or was evicted, discovery falls back to the normal
asynchronous path. Both client and service must be updated for Add-time preparation.

The service receives active folder paths via settings IPC and owns discovery;
the UI does not need to remain open. Discovery runs on a
dedicated Windows background-priority worker in 128-entry batches, with cancellation on rule replacement
or disconnect. Relevant filesystem notifications are coalesced (250 ms), with a
five-minute watcher-health check that retries only failed or unwatched rules.
Healthy watched rules do not receive scheduled recursive scans. Watcher errors
trigger recovery of their affected rules, after recreating the watcher to close
the scan/watch handoff gap. A changed anchor scans only its associated rules;
cached complete snapshots for other rules remain in the effective union.
Ordinary non-executable file creation/rename/deletion does not trigger scans;
executable names, populated directory creation/moves and deletion/rename of
known executable ancestors still do. Triggered updates rescan affected trees,
covering directory renames/deletions, reported notification failures and recreated roots. A generation guard discards scans of
obsolete settings. The effective app set is the case-insensitive union of folder,
explicit and wildcard rules, so overlapping owners are retained.

Background batches pause for at least 10 ms, or four times the preceding batch's
work time, targeting at most 20% worker duty rather than running continuously.
The background mode lowers only the dedicated scan worker's resource scheduling
priority, never the VPN/filter or shared thread-pool threads. There is no scan
parallelism within one monitor. Add-time discovery uses the same paced worker,
so adding a library does not introduce an unbounded foreground scan. All existing executable, directory, no-link and timeout
safeguards remain active. Pacing can delay discovery; it is not a promise of
zero disk/CPU impact or a guarantee of game frame-time stability.

Folder updates rebuild redirect and IPv4/IPv6 authorization filters using the
existing split-tunnel state lock and mode-specific policy. Include-mode connecting
blocks and OpenVPN IPv6 blocks receive the same effective set. Folder-only changes
do not reset IP/domain routes, DNS protection or domain polling. This follows the
existing filter-rebuild lifecycle, not a claim of atomic cross-engine WFP updates.

There is no saved-folder-count limit in either mode. Safety limits are separate:
10,000 combined unique folder executables, 100,000 visited directories per rule,
32 directory levels and a five-minute scan-time budget per rule. Ordinary
non-executable files are not capped at 10,000. Pattern expansion shares these
budgets with recursive discovery. Watchers are shared by distinct fixed anchors;
up to 64 anchors have dedicated native watchers and additional rules still use
periodic reconciliation, with a service-log notice. Drive roots,
Windows/system paths, broad profile/program roots, UNC/device paths and broad
pattern anchors are rejected. Linked roots/ancestors are rejected and linked
descendants are skipped. Failed/over-limit scans return no partial app set for
the affected root and emit a service-log warning. The service checks a folder's scan
before accepting it, reports progress and offers cancellation. The dialog also
polls service discovery status while the settings page is open: scanning,
discovery complete, inactive, unavailable or failure. It explicitly distinguishes
unsaved edits from the currently applied service rules. Failed root scans
are retried during reconciliation; they do not delete the saved folder rule.

This is asynchronous app discovery, not a native directory predicate. A program
launched immediately after creation may connect before discovery; existing
sockets can need a natural reconnect. In inverse mode an undiscovered app has
the usual non-included behavior until its filter is installed. Do not describe
this feature as guaranteed first-packet privacy protection. Scripts/documents
are covered only when the networking executable itself is under the chosen root.
Source/build/CI tests do not replace a live test of installed WFP behavior.

The last complete snapshot remains effective during a rescan of unchanged rules;
batch results do not cause repeated partial WFP rebuilds. Changed rule lists
discard the old snapshot rather than preserving rules the user removed. Initial
folder discovery on a new connection is asynchronous too: wait for service
discovery to complete before assuming the entire library has its filters.
Explicit app rules still apply immediately. Discovery status does not claim that
native WFP calls or a live connection test succeeded.

Folder patterns support `*` and `?` within individual directory-name components
in both modes. For example, `C:\Apps\EA\*\Tools` matches one level between EA
and Tools, then recursively discovers executables beneath every matched Tools
folder. `**` and traversal components after the first wildcard are rejected.
The fixed prefix must itself pass the specific-root safety checks; `C:\*` and
patterns anchored at a broad profile/program root are rejected. Expansion and
recursive scans share executable, directory and time budgets across all matches.
A failure returns no partial coverage for that pattern.

The saved entry stays a pattern. A valid pattern with no matches can be added
as long as its fixed prefix exists and is accessible. The service watches that
prefix recursively, so future matching folders are discovered without Apply;
targeted recovery handles watcher failures and recreated prefixes. Wildcard
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
watcher error, and retry only failed/unwatched rules periodically while connected; do not depend on the UI staying
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
