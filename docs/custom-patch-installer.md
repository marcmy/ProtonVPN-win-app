# Proton VPN custom patch installer

This tooling replaces the manual copy-over step for patch artifacts built from the fork.

## What the installer does

`Install-ProtonVPNPatch.ps1`:

1. Requests elevation when it is not already running as administrator.
2. Selects an installed Proton VPN version folder under `C:\Program Files\Proton\VPN`.
3. Stops Proton VPN processes and services that could lock files.
4. Copies the complete official version folder to a timestamped sibling folder, for example:

   ```text
   C:\Program Files\Proton\VPN\v5.1.5
   C:\Program Files\Proton\VPN\v5.1.5-backup-20260729-204416
   ```

5. Overlays the custom patch files without deleting untouched official files.
6. Automatically restores the backup if the overlay operation fails.
7. Restarts services that were running before installation.
8. Leaves the Proton VPN client closed by default unless `-RestartClient` is supplied.
9. After a successful install, keeps the newest three backups for that Proton VPN version and removes older matching backup folders.

Pass `-RestartClient` to relaunch the client when it was running before installation. Relaunching restores the client window, but the VPN connection itself comes back disconnected.

Backups are intentionally retained after a successful installation. A different parent folder can be supplied with `-BackupRoot`.

Backup retention can be changed per run:

```powershell
.\scripts\Install-ProtonVPNPatch.ps1 `
    -PatchPath .\protonvpn-client-patch-5.1.5-both.zip `
    -BackupRetentionCount 5
```

Set `-BackupRetentionCount 0` to disable automatic cleanup and keep every backup.

## Version safety

Every workflow-built patch now contains `patch-manifest.json`. The manifest records:

- manifest schema version
- exact Proton VPN release version
- build mode (`client`, `service`, or `both`)
- source commit and branch
- workflow run ID and build time
- path, size, and SHA-256 hash for every payload file

Before requesting elevation or stopping any service, the installer validates the
manifest schema, target version, safe and unique relative paths, exact payload file
set, file sizes, and SHA-256 hashes. `-ValidateOnly` performs the same verification
without changing the installed application:

```powershell
.\scripts\Install-ProtonVPNPatch.ps1 `
    -PatchPath .\protonvpn-client-patch-5.1.5-both `
    -TargetVersion 5.1.5 `
    -ValidateOnly
```

The self-extracting installer builder requires this manifest and bakes its `targetVersion` into the launcher. An installer built for `5.1.5` therefore passes:

```text
-TargetVersion 5.1.5
```

The install stops if `C:\Program Files\Proton\VPN\v5.1.5` is not present. It will not silently apply 5.1.5 binaries to a newer `v5.1.6` folder.

`Install-ProtonVPNPatch.ps1` requires a valid manifest. If `-TargetVersion` is
omitted, the selected installed folder must still exactly match the manifest's
target version.

## Install from an existing patch ZIP

```powershell
.\scripts\Install-ProtonVPNPatch.ps1 `
    -PatchPath .\protonvpn-client-patch-5.1.5-both.zip `
    -TargetVersion 5.1.5
```

The script also accepts an already-extracted patch directory.

Use `-WhatIf` to resolve the target and payload without stopping services, creating a backup, or copying files.

## Build a single self-extracting EXE

`New-ProtonVPNPatchSfx.ps1` uses the Windows-built-in IExpress tool. It packages three files into one EXE:

- `payload.zip`
- `Install-ProtonVPNPatch.ps1`
- `Install-ProtonVPNPatch.cmd`

The supplied patch ZIP or directory must contain exactly one `patch-manifest.json`.

```powershell
.\scripts\New-ProtonVPNPatchSfx.ps1 `
    -PatchPath .\protonvpn-client-patch-5.1.5-both.zip `
    -OutputPath .\ProtonVPN-Custom-Patch-5.1.5.exe
```

Double-clicking the resulting EXE extracts the payload to a temporary directory, launches the installer, triggers a normal UAC elevation prompt, relaunches Proton VPN when it was open before patching, and leaves the final result visible until Enter is pressed. The relaunched client starts disconnected, and the installer process chain exits afterward.

## GitHub Actions artifacts

`Windows fast patch build` produces two separate downloads:

```text
protonvpn-client-patch-5.1.5-both.zip
├─ patch-manifest.json
└─ raw patch files

protonvpn-custom-patch-installer-5.1.5-both.zip
└─ ProtonVPN-Custom-Patch-5.1.5.exe
```

`both` is the default and recommended build mode. It stages the client files plus every first-party service runtime assembly declared by `ProtonVPNService.deps.json`; packaging stops if a required assembly is missing or if client and service builds produce different files for the same install path.

GitHub Actions always wraps an artifact in a ZIP. A future GitHub Release can publish `ProtonVPN-Custom-Patch-5.1.5.exe` directly as a release asset.

Manual build workflows also publish GitHub build-provenance attestations. After
installing the GitHub CLI, verify a downloaded artifact before use:

```powershell
gh attestation verify .\ProtonVPN-Custom-Patch-5.1.5.exe `
    --repo marcmy/ProtonVPN-win-app
```

The installer EXE itself is not Authenticode-signed. The attestation proves which
GitHub Actions workflow and repository produced the exact bytes; it does not make
this fork an official Proton release.

## Official updates

An official Proton update installs a new release and can replace files previously
overlaid by this fork. Keep automatic security updates enabled, and only reapply a
fork patch whose manifest targets the exact installed version folder. If Proton has
released a new version but this fork has not yet ported it, use the official build
until the matching fork patch is available.

## Future Proton VPN releases

When Proton publishes source for a new version such as 5.1.6:

1. Sync the real 5.1.6 upstream source into the selected base branch.
2. Run `Future version patch automation` with `source_patch_branch=marc/proton` and `target_version=5.1.6`.
3. The workflow computes the complete fork delta from the common upstream base and ports it onto the new release. This includes NAT-PMP and app port forwarding, split tunneling, server list/search/health, updater behavior, installer tooling, and any other maintained fork changes.
4. The workflow stamps the fork-aware version, runs the fork regression suite, builds the client and service, creates the manifest, and produces `ProtonVPN-Custom-Patch-5.1.6.exe`.
5. Resolve any genuine conflicts where both Proton and the fork changed the same code, then test the installer against an official `v5.1.6` installation before publishing it.

Changing only the assembly version is not a substitute for syncing Proton's actual new source. Each release installer must be built from that release's real codebase.
