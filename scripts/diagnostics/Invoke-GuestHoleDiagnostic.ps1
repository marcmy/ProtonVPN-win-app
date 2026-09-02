# Copyright (c) 2026 Proton AG
#
# Diagnostic-only controller for the genuine Guest Hole path exposed by the
# diagnostics/guest-hole-windows-smoke-matrix client build.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('Start', 'Stop', 'Status')]
    [string]$Action,

    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$eventNames = [ordered]@{
    Start  = 'Local\ProtonVPN.Diagnostics.GuestHole.Start'
    Stop   = 'Local\ProtonVPN.Diagnostics.GuestHole.Stop'
    Active = 'Local\ProtonVPN.Diagnostics.GuestHole.Active'
    Idle   = 'Local\ProtonVPN.Diagnostics.GuestHole.Idle'
    Failed = 'Local\ProtonVPN.Diagnostics.GuestHole.Failed'
}

function Open-DiagnosticEvent
{
    param([string]$Name)

    try
    {
        return [System.Threading.EventWaitHandle]::OpenExisting($Name)
    }
    catch [System.Threading.WaitHandleCannotBeOpenedException]
    {
        throw @"
The Guest Hole diagnostic controller is not available in the running Proton VPN client.
Build and run diagnostics/guest-hole-windows-smoke-matrix after pulling the latest branch commits,
then run this command again. The normal production client does not expose these diagnostic events.
"@
    }
}

$events = @{}
try
{
    foreach ($entry in $eventNames.GetEnumerator())
    {
        $events[$entry.Key] = Open-DiagnosticEvent -Name $entry.Value
    }

    switch ($Action)
    {
        'Status'
        {
            if ($events.Failed.WaitOne(0))
            {
                'Failed'
            }
            elseif ($events.Active.WaitOne(0))
            {
                'Active'
            }
            elseif ($events.Idle.WaitOne(0))
            {
                'Idle'
            }
            else
            {
                'Transitioning'
            }
        }

        'Start'
        {
            if ($events.Active.WaitOne(0))
            {
                'Guest Hole is already active.'
                break
            }

            # Failed is manual-reset and intentionally remains set after a failed run so Status can
            # report it. Clear it before issuing the next attempt.
            $events.Failed.Reset() | Out-Null
            $events.Start.Set() | Out-Null

            $waitHandles = [System.Threading.WaitHandle[]]@($events.Active, $events.Failed)
            $waitResult = [System.Threading.WaitHandle]::WaitAny($waitHandles, [TimeSpan]::FromSeconds($TimeoutSeconds))

            if ($waitResult -eq 0)
            {
                'Guest Hole is active and held open for capture.'
            }
            elseif ($waitResult -eq 1)
            {
                throw 'The genuine Guest Hole attempt failed. Check the Proton VPN client log for "Guest Hole diagnostic" and GuestHoleLog entries.'
            }
            else
            {
                throw "Timed out after $TimeoutSeconds seconds waiting for Guest Hole to become active."
            }
        }

        'Stop'
        {
            if ($events.Idle.WaitOne(0) -and -not $events.Active.WaitOne(0))
            {
                'Guest Hole diagnostic is already idle.'
                break
            }

            # Stop may be issued while the connector is still transitioning. AutoReset preserves the
            # signal until HoldGuestHoleAsync begins waiting, so the tunnel will be released promptly
            # if it reaches Connected after this command.
            $events.Stop.Set() | Out-Null

            if (-not $events.Idle.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds)))
            {
                throw "Timed out after $TimeoutSeconds seconds waiting for Guest Hole to disconnect."
            }

            if ($events.Failed.WaitOne(0))
            {
                'Guest Hole diagnostic is idle after a failed/aborted transition.'
            }
            else
            {
                'Guest Hole was released and the genuine Guest Hole disconnect completed.'
            }
        }
    }
}
finally
{
    foreach ($event in $events.Values)
    {
        $event.Dispose()
    }
}
