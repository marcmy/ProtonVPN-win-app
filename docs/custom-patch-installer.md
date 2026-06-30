# Proton VPN custom patch installer

This tooling replaces the manual copy-over step for patch artifacts built from the fork.

## What it does

`Install-ProtonVPNPatch.ps1`:

1. Requests elevation when it is not already running as administrator.
2. Detects the newest installed Proton VPN version folder under `C:\Program Files\Proton\VPN` unless a version is specified explicitly.
3. Stops Proton VPN processes and services that could lock files.
4. Copies the complete official version folder to a timestamped sibling folder beside it, for example:

   ```text
   C:\Program Files\Proton\VPN\v4.4.1
   C:\Program Files\Proton\VPN\v4.4.1-backup-20260629-204416
   ```

5. Overlays the custom patch files without deleting untouched official files.
6. Automatically restores the backup if the overlay operation fails.
7. Restarts services and the client when they were running before installation.
8. After a successful install, keeps the newest three backups for that Proton VPN version and removes older matching backup folders.

Backups are intentionally retained after a successful installation. A different parent folder can still be supplied with `-BackupRoot`.

Backup retention can be changed per run:

```powershell
.\scripts\Install-ProtonVPNPatch.ps1 `
    -PatchPath .\protonvpn-client-patch-both.zip `
    -BackupRetentionCount 5
```

Set `-BackupRetentionCount 0` to disable automatic cleanup and keep every backup.

## Install from an existing patch ZIP

```powershell
.\scripts\Install-ProtonVPNPatch.ps1 `
    -PatchPath .\protonvpn-client-patch-both.zip
```

The script also accepts an already-extracted patch directory.

To target an explicit installed version:

```powershell
.\scripts\Install-ProtonVPNPatch.ps1 `
    -PatchPath .\protonvpn-client-patch-both.zip `
    -TargetVersion 4.4.1
```

Use `-WhatIf` to resolve the target and payload without stopping services, creating a backup, or copying files.

## Build a single self-extracting EXE

`New-ProtonVPNPatchSfx.ps1` uses the Windows-built-in IExpress tool. It packages three files into one EXE:

- `payload.zip`
- `Install-ProtonVPNPatch.ps1`
- `Install-ProtonVPNPatch.cmd`

Example:

```powershell
.\scripts\New-ProtonVPNPatchSfx.ps1 `
    -PatchDirectory .\artifacts\patch-both `
    -OutputPath .\artifacts\ProtonVPN-Custom-Patch.exe
```

Double-clicking the resulting EXE extracts the payload to a temporary directory, launches the installer, and triggers a normal UAC elevation prompt.

## Intended GitHub Actions integration

The final `Fast Patch Both` packaging step should pass its completed patch staging directory to:

```powershell
.\scripts\New-ProtonVPNPatchSfx.ps1 `
    -PatchDirectory $patchDirectory `
    -OutputPath $installerPath
```

The workflow can then upload both the traditional ZIP and the self-extracting EXE until the EXE path has been proven reliable on the installed client.
