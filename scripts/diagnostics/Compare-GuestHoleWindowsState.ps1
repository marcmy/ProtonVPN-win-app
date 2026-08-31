# Copyright (c) 2026 Proton AG
#
# Compares sanitized summary.json files produced by
# Capture-GuestHoleWindowsState.ps1.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Path
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-OptionalProperty
{
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object)
    {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property)
    {
        return $null
    }

    return $property.Value
}

function Resolve-SummaryPath
{
    param([string]$InputPath)

    $resolved = Resolve-Path -LiteralPath $InputPath -ErrorAction Stop
    $item = Get-Item -LiteralPath $resolved.Path -ErrorAction Stop
    if ($item.PSIsContainer)
    {
        $summaryPath = Join-Path $item.FullName 'summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf))
        {
            throw "No summary.json exists in '$($item.FullName)'."
        }
        return $summaryPath
    }

    return $item.FullName
}

function Format-LanResults
{
    param([object[]]$Results)

    $parts = @()
    foreach ($result in @($Results))
    {
        $target = Get-OptionalProperty -Object $result -Name 'Target'
        $icmp = Get-OptionalProperty -Object $result -Name 'IcmpReachable'
        $tcpParts = @()
        foreach ($tcp in @(Get-OptionalProperty -Object $result -Name 'Tcp'))
        {
            if ($null -ne $tcp)
            {
                $tcpParts += ('{0}:{1}' -f (Get-OptionalProperty -Object $tcp -Name 'Port'), (Get-OptionalProperty -Object $tcp -Name 'Reachable'))
            }
        }

        $parts += ('{0}[icmp={1};tcp={2}]' -f $target, $icmp, ($tcpParts -join ','))
    }

    return ($parts -join ' | ')
}

function Format-DnsResults
{
    param([object[]]$Results)

    $parts = @()
    foreach ($result in @($Results))
    {
        $parts += ('{0}:{1}' -f (Get-OptionalProperty -Object $result -Name 'Name'), (Get-OptionalProperty -Object $result -Name 'Success'))
    }

    return ($parts -join ' | ')
}

function Format-ProtonServices
{
    param([object[]]$Services)

    $parts = @()
    foreach ($service in @($Services))
    {
        $name = Get-OptionalProperty -Object $service -Name 'Name'
        $state = Get-OptionalProperty -Object $service -Name 'State'
        if ($null -ne $name)
        {
            $parts += ('{0}:{1}' -f $name, $state)
        }
    }

    return ($parts -join ', ')
}

function Format-ProtonProcesses
{
    param([object[]]$Processes)

    $names = @()
    foreach ($process in @($Processes))
    {
        $name = Get-OptionalProperty -Object $process -Name 'ProcessName'
        if ($null -ne $name)
        {
            $names += $name
        }
    }

    return (($names | Sort-Object -Unique) -join ', ')
}

function Get-KeyState
{
    param([object]$Summary)

    $serviceSettings = Get-OptionalProperty -Object $Summary -Name 'ServiceSettings'
    $nrpt = Get-OptionalProperty -Object $Summary -Name 'Nrpt'
    $firewall = Get-OptionalProperty -Object $Summary -Name 'Firewall'
    $wfp = Get-OptionalProperty -Object $firewall -Name 'Wfp'

    $wfpHash = Get-OptionalProperty -Object $wfp -Name 'Sha256'
    if (($null -ne $wfpHash) -and ($wfpHash.Length -gt 12))
    {
        $wfpHash = $wfpHash.Substring(0, 12)
    }

    return [ordered]@{
        UiLanAccess = Get-OptionalProperty -Object $Summary -Name 'UiLanAccessState'
        ServiceKillSwitch = Get-OptionalProperty -Object $serviceSettings -Name 'KillSwitchMode'
        ServiceLanAccess = Get-OptionalProperty -Object $serviceSettings -Name 'IsLocalAreaNetworkAccessEnabled'
        ServiceDnsBlockMode = Get-OptionalProperty -Object $serviceSettings -Name 'DnsBlockMode'
        ServicePortForwardingForApps = Get-OptionalProperty -Object $serviceSettings -Name 'PortForwardingForApps'
        ServiceIpv6Enabled = Get-OptionalProperty -Object $serviceSettings -Name 'IsIpv6Enabled'
        ServiceSplitTunnelMode = Get-OptionalProperty -Object $serviceSettings -Name 'SplitTunnelMode'
        NrptEffectivePolicyCount = Get-OptionalProperty -Object $nrpt -Name 'EffectivePolicyCount'
        NrptRuleCount = Get-OptionalProperty -Object $nrpt -Name 'RuleCount'
        ProtonFirewallRuleCount = Get-OptionalProperty -Object $firewall -Name 'ProtonRuleCount'
        WfpCaptured = Get-OptionalProperty -Object $wfp -Name 'Captured'
        WfpProtonMatchCount = Get-OptionalProperty -Object $wfp -Name 'ProtonMatchCount'
        WfpSha256Prefix = $wfpHash
        LanReachability = Format-LanResults -Results @(Get-OptionalProperty -Object $Summary -Name 'LanProbeResults')
        DnsResolution = Format-DnsResults -Results @(Get-OptionalProperty -Object $Summary -Name 'DnsProbeResults')
        ProtonServices = Format-ProtonServices -Services @(Get-OptionalProperty -Object $Summary -Name 'ProtonServices')
        ProtonProcesses = Format-ProtonProcesses -Processes @(Get-OptionalProperty -Object $Summary -Name 'ProtonProcesses')
    }
}

$summaries = @()
foreach ($inputPath in $Path)
{
    $summaryPath = Resolve-SummaryPath -InputPath $inputPath
    $summary = Get-Content -LiteralPath $summaryPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    $summaries += [pscustomobject]@{
        Path = $summaryPath
        Summary = $summary
        State = Get-KeyState -Summary $summary
    }
}

if ($summaries.Count -eq 0)
{
    throw 'No snapshots were supplied.'
}

$rows = @()
foreach ($item in $summaries)
{
    $summary = $item.Summary
    $state = $item.State
    $rows += [pscustomobject]@{
        CapturedAt = Get-OptionalProperty -Object $summary -Name 'CapturedAt'
        Mode = Get-OptionalProperty -Object $summary -Name 'KillSwitchMode'
        Phase = Get-OptionalProperty -Object $summary -Name 'Phase'
        UiLan = $state.UiLanAccess
        ServiceLan = $state.ServiceLanAccess
        ServiceDns = $state.ServiceDnsBlockMode
        NRPTPolicy = $state.NrptEffectivePolicyCount
        NRPTRules = $state.NrptRuleCount
        ProtonRules = $state.ProtonFirewallRuleCount
        WfpMatches = $state.WfpProtonMatchCount
        LAN = $state.LanReachability
        DNS = $state.DnsResolution
    }
}

Write-Host ''
Write-Host 'Guest Hole Windows smoke snapshots'
Write-Host '================================='
$rows | Format-Table -AutoSize -Wrap

$baseline = $summaries[0]
$baselinePhase = Get-OptionalProperty -Object $baseline.Summary -Name 'Phase'

for ($i = 1; $i -lt $summaries.Count; $i++)
{
    $current = $summaries[$i]
    $currentPhase = Get-OptionalProperty -Object $current.Summary -Name 'Phase'

    Write-Host ''
    Write-Host ('Changes: {0} -> {1}' -f $baselinePhase, $currentPhase)
    Write-Host ('-' * 72)

    $changeCount = 0
    foreach ($key in $baseline.State.Keys)
    {
        $before = [string]$baseline.State[$key]
        $after = [string]$current.State[$key]
        if ($before -ne $after)
        {
            $changeCount++
            Write-Host ('{0}: {1} -> {2}' -f $key, $before, $after)
        }
    }

    if ($changeCount -eq 0)
    {
        Write-Host 'No selected key-state changes.'
    }
}

Write-Host ''
Write-Host 'Raw route, NRPT, firewall, WFP, DNS, and adapter captures remain in each snapshot directory for detailed inspection.'
