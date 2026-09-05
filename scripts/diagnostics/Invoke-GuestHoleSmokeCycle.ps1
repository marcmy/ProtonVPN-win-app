# Copyright (c) 2026 Proton AG
#
# Runs the Guest Hole-active and keep-enabled-disconnected smoke phases as one
# local operation. This is intentionally self-contained because genuine Guest
# Hole can remove ordinary Internet access (including a remote control/chat
# channel) while it is held open for capture.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Soft', 'Hard')]
    [string]$KillSwitchMode,

    [string[]]$LanTargets = @(),

    [int[]]$LanTcpPorts = @(),

    [string[]]$DnsNames = @('protonvpn.com', 'example.com'),

    [string]$OutputRoot = (Join-Path (Get-Location) 'guest-hole-smoke-results'),

    [string]$ServiceSettingsPath,

    [ValidateSet('Enabled', 'Disabled', 'Unknown')]
    [string]$UiLanAccessState = 'Unknown',

    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$diagnosticScript = Join-Path $PSScriptRoot 'Invoke-GuestHoleDiagnostic.ps1'
$captureScript = Join-Path $PSScriptRoot 'Capture-GuestHoleWindowsState.ps1'

function Test-IsAdministrator
{
    try
    {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch
    {
        return $false
    }
}

function Get-DiagnosticStatus
{
    return ((& $diagnosticScript Status) | Select-Object -Last 1).Trim()
}

function Invoke-StateCapture
{
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('GuestHole', 'KeepEnabledDisconnected')]
        [string]$Phase,

        [Parameter(Mandatory = $true)]
        [string]$Note
    )

    $arguments = @{
        KillSwitchMode = $KillSwitchMode
        Phase = $Phase
        LanTargets = $LanTargets
        LanTcpPorts = $LanTcpPorts
        DnsNames = $DnsNames
        OutputRoot = $OutputRoot
        UiLanAccessState = $UiLanAccessState
        Note = $Note
    }

    if (-not [string]::IsNullOrWhiteSpace($ServiceSettingsPath))
    {
        $arguments.ServiceSettingsPath = $ServiceSettingsPath
    }

    & $captureScript @arguments
}

if (-not (Test-IsAdministrator))
{
    throw 'Invoke-GuestHoleSmokeCycle.ps1 must be run from an elevated PowerShell. No Guest Hole action was started.'
}

if (-not (Test-Path -LiteralPath $diagnosticScript -PathType Leaf) -or
    -not (Test-Path -LiteralPath $captureScript -PathType Leaf))
{
    throw 'Required Guest Hole diagnostic scripts were not found beside this script.'
}

$releaseCompleted = $false
$guestHoleConfirmedActive = $false

Write-Host 'Starting the genuine Guest Hole smoke cycle.'
Write-Host 'Ordinary Internet access may disappear while Guest Hole is active.'
Write-Host 'Do not reconnect, press Disconnect, or change Proton VPN settings.'
Write-Host 'This local PowerShell process will capture Guest Hole, release it, and capture the disconnected state automatically.'
Write-Host ''

try
{
    & $diagnosticScript Start -TimeoutSeconds $TimeoutSeconds

    $status = Get-DiagnosticStatus
    if ($status -ne 'Active')
    {
        throw "Guest Hole did not remain active after Start (status: $status). No capture or release was attempted."
    }
    $guestHoleConfirmedActive = $true

    Write-Host ''
    Write-Host 'Guest Hole is confirmed Active. Capturing Windows state now...'
    Invoke-StateCapture `
        -Phase GuestHole `
        -Note 'Guest Hole active; self-contained deterministic diagnostic capture'

    $status = Get-DiagnosticStatus
    if ($status -ne 'Active')
    {
        throw "Guest Hole stopped during its capture (status: $status). Release will not be issued against a replacement tunnel."
    }

    Write-Host ''
    Write-Host 'Guest Hole capture is complete. Releasing the held callback through the genuine manager teardown...'
    & $diagnosticScript Release -TimeoutSeconds $TimeoutSeconds
    $releaseCompleted = $true

    $status = Get-DiagnosticStatus
    if ($status -ne 'Idle')
    {
        throw "Guest Hole did not reach Idle after release (status: $status). The disconnected-state capture was not started."
    }

    Write-Host ''
    Write-Host 'Guest Hole is Idle. Capturing the immediate keep-enabled disconnected state...'
    Invoke-StateCapture `
        -Phase KeepEnabledDisconnected `
        -Note 'Guest Hole ended; VPN deliberately left disconnected; no settings touched; self-contained cycle'

    Write-Host ''
    Write-Host 'Guest Hole smoke cycle complete.'
    Write-Host 'Both GuestHole and KeepEnabledDisconnected snapshots were captured.'
    Write-Host 'It is now safe to reconnect the ordinary VPN manually.'
}
finally
{
    if ($guestHoleConfirmedActive -and -not $releaseCompleted)
    {
        try
        {
            $status = Get-DiagnosticStatus
            if ($status -eq 'Active')
            {
                Write-Warning 'The cycle exited before normal release while Guest Hole is still Active. Releasing it now to avoid leaving the machine in the held diagnostic tunnel.'
                & $diagnosticScript Release -TimeoutSeconds $TimeoutSeconds
            }
            else
            {
                Write-Warning "The cycle exited before normal release and Guest Hole status is '$status'. No release signal was sent."
            }
        }
        catch
        {
            Write-Warning ('Could not complete the guarded cleanup check: {0}' -f $_.Exception.Message)
        }
    }
}
