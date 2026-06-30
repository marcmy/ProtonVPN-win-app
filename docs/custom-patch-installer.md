# Proton VPN custom patch installer

This tooling replaces the manual copy-over step for patch artifacts built from the fork.

## What the installer does

`Install-ProtonVPNPatch.ps1`:

1. Requests elevation when it is not already running as administrator.
2. Selects an installed Proton VPN version folder under `C:\Program Files\Proton\VPN`.
3. Stops Proton VPN processes and services that could lock files.
4. Copies the complete official version folder to a timestamped sibling folder, for example:

   ```text
   C:\Program Files\Proton\VPN\v4.4.1
   C:\Program Files\Proton\VPN\v4.4.1-backup-20260629-204416
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
    -PatchPath .\protonvpn-client-patch-4.4.1-both.zip `
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

The self-extracting installer builder requires this manifest and bakes its `targetVersion` into the launcher. An installer built for `4.4.1` therefore passes:

```text
-TargetVersion 4.4.1
```

The install stops if `C:\Program Files\Proton\VPN\v4.4.1` is not present. It will not silently apply 4.4.1 binaries to a newer `v4.4.2` folder.

Running `Install-ProtonVPNPatch.ps1` directly without a manifest or `-TargetVersion` retains the legacy behavior of selecting the newest installed version folder. For version-safe manual use, always pass `-TargetVersion`.

## Install from an existing patch ZIP

```powershell
.\scripts\Install-ProtonVPNPatch.ps1 `
    -PatchPath .\protonvpn-client-patch-4.4.1-both.zip `
    -TargetVersion 4.4.1
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
    -PatchPath .\protonvpn-client-patch-4.4.1-both.zip `
    -OutputPath .\ProtonVPN-Custom-Patch-4.4.1.exe
```

Double-clicking the resulting EXE extracts the payload to a temporary directory, launches the installer, triggers a normal UAC elevation prompt, relaunches Proton VPN when it was open before patching, and leaves the final result visible until Enter is pressed. The relaunched client starts disconnected, and the installer process chain exits afterward.

## GitHub Actions artifacts

`Windows fast patch build` produces two separate downloads:

```text
protonvpn-client-patch-4.4.1-both.zip
├─ patch-manifest.json
└─ raw patch files

protonvpn-custom-patch-installer-4.4.1-both.zip
└─ ProtonVPN-Custom-Patch-4.4.1.exe
```

GitHub Actions always wraps an artifact in a ZIP. A future GitHub Release can publish `ProtonVPN-Custom-Patch-4.4.1.exe` directly as a release asset.

## Future Proton VPN releases

When Proton releases a new version such as 4.4.2:

1. Sync the real 4.4.2 upstream source into the selected base branch.
2. Run `Future version patch automation` with `target_version=4.4.2`.
3. The workflow ports the custom patch, stamps the version, builds the client and service, creates the manifest, and produces `ProtonVPN-Custom-Patch-4.4.2.exe`.
4. Test that installer against an official `v4.4.2` installation before publishing it.

Changing only the assembly version is not a substitute for syncing Proton's actual new source. Each release installer must be built from that release's real codebase.
