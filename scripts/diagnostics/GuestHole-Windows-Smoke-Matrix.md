# Guest Hole -> disconnected Windows smoke matrix

This runbook validates the real Windows networking state after Guest Hole ends while the VPN remains disconnected and the kill switch remains enabled.

It exists because deterministic service/firewall characterization proves that the Guest Hole-safe settings snapshot is persisted and presented again to the kill-switch layer on a keep-enabled disconnect, but that does **not** by itself prove the effective Windows LAN, DNS, route, or WFP behavior.

The accompanying scripts are diagnostic-only:

- `Invoke-GuestHoleDiagnostic.ps1` starts, holds, reports, and releases the genuine Guest Hole path exposed only by this diagnostic branch.
- `Capture-GuestHoleWindowsState.ps1` records a timestamped state snapshot and performs read-only LAN/DNS probes.
- `Compare-GuestHoleWindowsState.ps1` compares the sanitized `summary.json` files from multiple snapshots.

The trigger script calls the real `IGuestHoleManager.ExecuteAsync` path inside the diagnostic client and holds its connected callback until release. It does not synthesize firewall, route, DNS, or network state. The capture/compare scripts remain read-only, and recovery actions remain deliberate manual steps.

## Install the diagnostic branch build

Do not run `src\bin\ProtonVPN.Client.exe` directly against the installed Proton VPN service for this smoke test. A loose development executable is not accepted by the installed service's process-authentication/version checks and will receive HTTP 401 responses instead of usable VPN service IPC.

First make sure the diagnostic changes are committed and pushed to `diagnostics/guest-hole-windows-smoke-matrix`. Then dispatch the fork's normal **FastPatch both** workflow explicitly against the fork repository:

```powershell
gh workflow run windows-client-fast-patch.yml `
    --repo marcmy/ProtonVPN-win-app `
    --ref diagnostics/guest-hole-windows-smoke-matrix `
    -f build_mode=both `
    -f upload_full_bin=false
```

`--repo` is intentional. This checkout also has the upstream Proton repository configured, and GitHub CLI may otherwise resolve the workflow lookup against `ProtonVPN/win-app`, where this fork-only workflow does not exist.

After the run finishes, find the newest run for this branch:

```powershell
$run = gh run list `
    --repo marcmy/ProtonVPN-win-app `
    --workflow windows-client-fast-patch.yml `
    --branch diagnostics/guest-hole-windows-smoke-matrix `
    --limit 1 `
    --json databaseId,status,conclusion `
    | ConvertFrom-Json

$run
```

Require `status = completed` and `conclusion = success`, then download the self-extracting installer artifact:

```powershell
gh run download $run.databaseId `
    --repo marcmy/ProtonVPN-win-app `
    --name protonvpn-custom-patch-installer-5.1.5-both `
    --dir .\guest-hole-fastpatch
```

Install the resulting diagnostic build through the generated FastPatch installer:

```powershell
Start-Process `
    .\guest-hole-fastpatch\ProtonVPN-Custom-Patch-5.1.5.exe `
    -Wait
```

Accept the normal UAC prompt. The installer overlays the matching client/service runtime into the installed `v5.1.5` tree and preserves a backup using the fork's normal FastPatch safety path. Launch Proton VPN normally after installation; the diagnostic named events are created only by this branch build.

## Preconditions

Run the matrix on the Windows machine that normally runs this fork.

Use an **elevated PowerShell** so WFP, firewall, service, and route observations are complete. The capture script is Windows PowerShell 5.1-compatible and also works from newer PowerShell.

Before starting, choose at least one LAN target that is known reachable during the normal connected baseline:

- preferably a stable router, NAS, or another machine;
- use its IP address, not only a hostname;
- record ICMP plus at least one TCP port that is known open on that target when practical.

Do not infer "LAN blocked" from an ICMP failure alone. A peer firewall can block ping. The same target/port must first succeed in the baseline for a later failure to be meaningful.

The capture script tries to locate the persisted service-settings JSON under Proton-related `%ProgramData%` directories. If `ServiceSettings.Path` is empty in `summary.json`, locate the actual service-settings file for this installation and pass it explicitly with `-ServiceSettingsPath` on subsequent captures.

The script only copies these selected service fields into its output; it does not copy the complete settings file:

- `KillSwitchMode`
- `IsLocalAreaNetworkAccessEnabled`
- `DnsBlockMode`
- `PortForwardingForApps`
- `IsIpv6Enabled`
- `Ipv6LeakProtection`
- coarse split-tunnel mode/enabled fields when present

## What each snapshot contains

Each capture creates a directory below `guest-hole-smoke-results` containing:

- full route table and `route print`;
- targeted route resolution for each LAN peer and `10.2.0.1`;
- adapter and IP-interface state;
- DNS client/server configuration;
- effective NRPT policy and NRPT rules;
- `Resolve-DnsName` and `nslookup` results;
- LAN ICMP/TCP probe results;
- Windows Firewall profiles;
- Proton-named Windows Firewall rules, if any;
- `netsh advfirewall` state;
- a WFP state XML from `netsh wfp show state` by default;
- Proton service/process state;
- selected persisted service settings;
- sanitized `summary.json` for automated comparison.

A WFP hash change is useful as a fast signal, but it is **not** by itself evidence of a defect. Inspect the actual WFP/filter state and the observable networking result.

## Capture command template

From the repository root:

```powershell
$common = @{
    LanTargets      = @('192.168.1.50') # replace with a known LAN peer
    LanTcpPorts     = @(445)             # replace with a port known open at baseline
    DnsNames        = @('protonvpn.com', 'example.com')
    UiLanAccessState = 'Enabled'
}

# Add this only if auto-discovery does not find the persisted service settings:
# $common.ServiceSettingsPath = 'C:\path\to\the\actual\service-settings.json'
```

Then invoke the capture for the current phase:

```powershell
.\scripts\diagnostics\Capture-GuestHoleWindowsState.ps1 `
    @common `
    -KillSwitchMode Soft `
    -Phase Baseline `
    -Note 'Normal VPN connected; LAN enabled; normal DNS'
```

Repeat with `-KillSwitchMode Hard` for the Hard-kill-switch matrix.

Do not use `-SkipWfp` for the authoritative matrix unless WFP capture is impossible on the test machine.

## Matrix discipline

Run **Soft** and **Hard** as separate matrices.

For recovery testing, use a **fresh Baseline -> Guest Hole -> KeepEnabledDisconnected cycle for each recovery arm**. Do not perform reconnect, settings reapply, service restart, client restart, and reboot one after another in a single chain: the first recovery action changes the state being investigated and contaminates the later result.

For this branch, use `Invoke-GuestHoleDiagnostic.ps1`; it is the deterministic diagnostic control surface for the genuine Guest Hole path. Do not substitute synthetic firewall/network manipulation.

### A. Baseline normal state

For the selected kill-switch mode:

1. Set the user's normal LAN access setting to **enabled**.
2. Use the normal DNS mode intended for the test.
3. Enable the selected Soft or Hard kill switch.
4. Establish an ordinary VPN connection.
5. Verify the chosen LAN ICMP/TCP target is reachable at baseline.
6. Record what the client UI says for LAN access.
7. Capture:

```powershell
.\scripts\diagnostics\Capture-GuestHoleWindowsState.ps1 @common `
    -KillSwitchMode Soft `
    -Phase Baseline `
    -Note 'Normal VPN connected'
```

The baseline is the control for route, LAN, DNS/NRPT, firewall/WFP, and service settings.

### B. Guest Hole active

1. With the diagnostic-branch client running, start and hold the genuine Guest Hole:

```powershell
.\scripts\diagnostics\Invoke-GuestHoleDiagnostic.ps1 Start
```

2. Confirm the script reports `Guest Hole is active and held open for capture.` You can also query:

```powershell
.\scripts\diagnostics\Invoke-GuestHoleDiagnostic.ps1 Status
```

3. Do not change normal user settings to manufacture the safe state.
4. Capture while Guest Hole is active:

```powershell
.\scripts\diagnostics\Capture-GuestHoleWindowsState.ps1 @common `
    -KillSwitchMode Soft `
    -Phase GuestHole `
    -Note 'Guest Hole active; deterministic diagnostic ExecuteAsync trigger'
```

Expected characterization to verify at the real Windows layer: Guest Hole's safe policy should prevent ordinary LAN access and should show the expected safe DNS/NRPT/firewall behavior.

### C. Guest Hole ended -> keep-enabled disconnected

This is the state under investigation.

1. Release the held callback. This returns `null` to `GuestHoleManager.ExecuteAsync`, which makes the real manager invoke its normal disconnect path:

```powershell
.\scripts\diagnostics\Invoke-GuestHoleDiagnostic.ps1 Release
```

2. Keep the VPN **disconnected**.
3. Keep the kill switch **enabled**.
4. Do **not** reconnect.
5. Do **not** toggle LAN/DNS/kill-switch settings before this capture.
6. Record what the client UI says for LAN access while disconnected.
7. Capture immediately:

```powershell
.\scripts\diagnostics\Capture-GuestHoleWindowsState.ps1 @common `
    -KillSwitchMode Soft `
    -Phase KeepEnabledDisconnected `
    -UiLanAccessState Enabled `
    -Note 'Guest Hole ended; VPN deliberately left disconnected; no settings touched'
```

The important questions are now empirical:

- Does the same LAN peer/port that succeeded at baseline still succeed?
- Which route is selected for that LAN target?
- What effective NRPT policy/rules remain?
- Do ordinary DNS queries resolve, and through what observable configuration?
- What WFP state remains after Guest Hole teardown?
- Do the selected persisted service settings still show Guest Hole-safe LAN/DNS values?
- If the UI still says LAN access is enabled while the service snapshot says disabled, does Windows behavior follow the UI preference or the Guest Hole-safe service/firewall snapshot?

Do not call the mismatch a bug until the observable result and intended disconnected kill-switch semantics are established.

### D1. Recovery by normal reconnect

Start from a fresh A -> B -> C cycle, then make an ordinary VPN connection and capture:

```powershell
.\scripts\diagnostics\Capture-GuestHoleWindowsState.ps1 @common `
    -KillSwitchMode Soft `
    -Phase RecoveryReconnect `
    -UiLanAccessState Enabled `
    -Note 'Ordinary VPN connection established after keep-enabled disconnected state'
```

Confirm whether normal LAN/DNS/firewall state returns exactly to the baseline semantics.

### D2. Recovery by settings reapply/change while disconnected

Start from a fresh A -> B -> C cycle. Stay disconnected. Change/reapply a relevant normal setting through the real client UI (for example, deliberately toggle LAN access and restore it to the intended value). Do not reconnect before capture.

```powershell
.\scripts\diagnostics\Capture-GuestHoleWindowsState.ps1 @common `
    -KillSwitchMode Soft `
    -Phase RecoverySettingsReapply `
    -UiLanAccessState Enabled `
    -Note 'Relevant normal setting reapplied while still disconnected'
```

This identifies whether a settings push alone restores normal LAN/DNS policy or whether a connection transition is required.

### D3. Recovery by service restart

Start from a fresh A -> B -> C cycle. While still disconnected, restart the actual Proton VPN service using the normal Windows/service-management path, leave the client/network settings otherwise untouched, then capture:

```powershell
.\scripts\diagnostics\Capture-GuestHoleWindowsState.ps1 @common `
    -KillSwitchMode Soft `
    -Phase RecoveryServiceRestart `
    -UiLanAccessState Enabled `
    -Note 'Service restarted while still disconnected; no normal reconnect'
```

Because the Guest Hole-safe settings snapshot is persisted at the service abstraction layer, this arm determines whether service startup rehydrates that snapshot into the same effective Windows policy or reconstructs something different.

### D4. Recovery by client restart

Start from a fresh A -> B -> C cycle. Restart only the client through the ordinary application path, remain disconnected, then capture:

```powershell
.\scripts\diagnostics\Capture-GuestHoleWindowsState.ps1 @common `
    -KillSwitchMode Soft `
    -Phase RecoveryClientRestart `
    -UiLanAccessState Enabled `
    -Note 'Client restarted while still disconnected; no normal reconnect'
```

This determines whether normal client settings are automatically resent during client startup and, if so, whether that is the restoration event.

### D5. Optional recovery/persistence across Windows reboot

Only run this after the earlier arms establish that persistence may matter. Start from a fresh A -> B -> C cycle, reboot Windows without first performing another recovery action, leave Proton VPN disconnected after boot if the product state permits it, and capture:

```powershell
.\scripts\diagnostics\Capture-GuestHoleWindowsState.ps1 @common `
    -KillSwitchMode Soft `
    -Phase RecoveryReboot `
    -UiLanAccessState Enabled `
    -Note 'Windows rebooted from persisted keep-enabled disconnected state'
```

## Comparing snapshots

Pass either snapshot directories or their `summary.json` files. Put the baseline first:

```powershell
.\scripts\diagnostics\Compare-GuestHoleWindowsState.ps1 @(
    '.\guest-hole-smoke-results\<baseline-directory>',
    '.\guest-hole-smoke-results\<guest-hole-directory>',
    '.\guest-hole-smoke-results\<keep-enabled-disconnected-directory>',
    '.\guest-hole-smoke-results\<recovery-directory>'
)
```

The comparison prints a compact table plus selected differences relative to the first snapshot. Use the raw captures for the authoritative route, NRPT, firewall, and WFP interpretation.

## Results table

Fill one row per kill-switch mode after the real-machine run:

| Mode | Baseline LAN | Guest Hole LAN | Keep-enabled disconnected LAN | Disconnected service LAN/DNS | UI LAN setting | NRPT after disconnect | WFP/firewall observation | Normal reconnect restores? | Settings-only restores? | Service restart effect | Client restart effect | Reboot effect | Verdict |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Soft | pending | pending | pending | pending | pending | pending | pending | pending | pending | pending | pending | optional | pending |
| Hard | pending | pending | pending | pending | pending | pending | pending | pending | pending | pending | pending | optional | pending |

## Decision rule

### Leave runtime behavior unchanged

Do not modify production code if the real Windows result is safe and consistent with intended kill-switch behavior, even if the persisted Guest Hole snapshot looks surprising at the service layer.

Document:

- the effective LAN behavior;
- the effective DNS/NRPT behavior;
- the relevant WFP/firewall state;
- any temporary client/service representation mismatch;
- the exact event that restores normal policy.

### Consider a targeted runtime correction

Only proceed to production code if the smoke matrix demonstrates a concrete undesirable result, such as:

- LAN remains unexpectedly blocked after Guest Hole despite intended disconnected semantics and the user's normal LAN setting;
- NRPT/DNS remains incorrectly persistent;
- the client says normal LAN access is enabled while Windows materially enforces a stale safe snapshot until an unrelated future event;
- kill-switch state becomes inconsistent or leaks traffic.

If that occurs, identify the smallest state-restoration point and add deterministic regression coverage before changing behavior. Preserve Guest Hole safety while active and preserve both Soft and Hard kill-switch semantics.

## Scope boundary

This matrix is not a Guest Hole redesign and is not a FastPatch/CI task. It must not weaken the kill switch merely to make LAN behavior more convenient.

The service-level snapshot is evidence about inputs to the Windows networking layer. The actual Windows route/NRPT/WFP/reachability result is the deciding evidence.
