# Copyright (c) 2026 Proton AG
#
# Diagnostic-only Windows smoke-test helper for Guest Hole transitions.
# This script does not change Proton VPN settings, firewall policy, routes,
# services, processes, or connection state. It only records state and performs
# read-only reachability / DNS probes.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Soft', 'Hard')]
    [string]$KillSwitchMode,

    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'Baseline',
        'GuestHole',
        'KeepEnabledDisconnected',
        'RecoveryReconnect',
        'RecoverySettingsReapply',
        'RecoveryServiceRestart',
        'RecoveryClientRestart',
        'RecoveryReboot')]
    [string]$Phase,

    [string[]]$LanTargets = @(),

    [int[]]$LanTcpPorts = @(),

    [string[]]$DnsNames = @('protonvpn.com', 'example.com'),

    [string]$OutputRoot = (Join-Path (Get-Location) 'guest-hole-smoke-results'),

    [string]$ServiceSettingsPath,

    [ValidateSet('Enabled', 'Disabled', 'Unknown')]
    [string]$UiLanAccessState = 'Unknown',

    [string]$Note = '',

    [switch]$SkipWfp
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-IsAdministrator
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

function Write-Capture
{
    param(
        [string]$Name,
        [scriptblock]$ScriptBlock
    )

    $path = Join-Path $script:SnapshotDirectory $Name
    try
    {
        $text = (& $ScriptBlock 2>&1 | Out-String -Width 4096)
        Set-Content -LiteralPath $path -Value $text -Encoding UTF8
        return $null
    }
    catch
    {
        $message = 'ERROR: {0}' -f $_.Exception.Message
        Set-Content -LiteralPath $path -Value $message -Encoding UTF8
        return $message
    }
}

function Test-TcpPort
{
    param(
        [string]$Target,
        [int]$Port,
        [int]$TimeoutMilliseconds = 3000
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try
    {
        $asyncResult = $client.BeginConnect($Target, $Port, $null, $null)
        if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMilliseconds, $false))
        {
            return $false
        }

        $client.EndConnect($asyncResult)
        return $client.Connected
    }
    catch
    {
        return $false
    }
    finally
    {
        $client.Close()
    }
}

function Find-ServiceSettingsFile
{
    if (-not [string]::IsNullOrWhiteSpace($ServiceSettingsPath))
    {
        if (Test-Path -LiteralPath $ServiceSettingsPath -PathType Leaf)
        {
            return (Resolve-Path -LiteralPath $ServiceSettingsPath).Path
        }

        return $null
    }

    if ([string]::IsNullOrWhiteSpace($env:ProgramData) -or -not (Test-Path -LiteralPath $env:ProgramData))
    {
        return $null
    }

    $protonRoots = @(
        Get-ChildItem -LiteralPath $env:ProgramData -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '(?i)proton' }
    )

    foreach ($root in $protonRoots)
    {
        $jsonFiles = @(
            Get-ChildItem -LiteralPath $root.FullName -File -Recurse -Filter '*.json' -ErrorAction SilentlyContinue |
                Where-Object { $_.Length -lt 2MB }
        )

        foreach ($file in $jsonFiles)
        {
            try
            {
                $json = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                $names = @($json.PSObject.Properties.Name)
                if (($names -contains 'KillSwitchMode') -and
                    ($names -contains 'IsLocalAreaNetworkAccessEnabled') -and
                    ($names -contains 'DnsBlockMode'))
                {
                    return $file.FullName
                }
            }
            catch
            {
                # Ignore unrelated or malformed JSON files during targeted discovery.
            }
        }
    }

    return $null
}

function Get-SafeServiceSettings
{
    $path = Find-ServiceSettingsFile
    if ([string]::IsNullOrWhiteSpace($path))
    {
        return [ordered]@{
            Path = $null
            Error = if ([string]::IsNullOrWhiteSpace($ServiceSettingsPath)) {
                'Service settings file was not auto-discovered under a Proton ProgramData directory.'
            } else {
                'The supplied ServiceSettingsPath does not exist or is not a file.'
            }
        }
    }

    try
    {
        $json = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $splitTunnel = Get-OptionalProperty -Object $json -Name 'SplitTunnel'
        if ($null -eq $splitTunnel)
        {
            $splitTunnel = Get-OptionalProperty -Object $json -Name 'SplitTunnelSettings'
        }

        return [ordered]@{
            Path = $path
            KillSwitchMode = Get-OptionalProperty -Object $json -Name 'KillSwitchMode'
            IsLocalAreaNetworkAccessEnabled = Get-OptionalProperty -Object $json -Name 'IsLocalAreaNetworkAccessEnabled'
            DnsBlockMode = Get-OptionalProperty -Object $json -Name 'DnsBlockMode'
            PortForwardingForApps = Get-OptionalProperty -Object $json -Name 'PortForwardingForApps'
            IsIpv6Enabled = Get-OptionalProperty -Object $json -Name 'IsIpv6Enabled'
            Ipv6LeakProtection = Get-OptionalProperty -Object $json -Name 'Ipv6LeakProtection'
            SplitTunnelMode = Get-OptionalProperty -Object $splitTunnel -Name 'Mode'
            SplitTunnelEnabled = Get-OptionalProperty -Object $splitTunnel -Name 'IsEnabled'
            Error = $null
        }
    }
    catch
    {
        return [ordered]@{
            Path = $path
            Error = 'Could not parse service settings JSON: {0}' -f $_.Exception.Message
        }
    }
}

function Get-NrptSnapshot
{
    $result = [ordered]@{
        EffectivePolicy = @()
        Rules = @()
        EffectivePolicyError = $null
        RulesError = $null
    }

    if (Get-Command Get-DnsClientNrptPolicy -ErrorAction SilentlyContinue)
    {
        try
        {
            $result.EffectivePolicy = @(
                Get-DnsClientNrptPolicy -Effective -ErrorAction Stop |
                    Select-Object Namespace, NameServers, DirectAccessDnsServers, DnsSecEnabled, DnsSecValidationRequired
            )
        }
        catch
        {
            $result.EffectivePolicyError = $_.Exception.Message
        }
    }
    else
    {
        $result.EffectivePolicyError = 'Get-DnsClientNrptPolicy is unavailable.'
    }

    if (Get-Command Get-DnsClientNrptRule -ErrorAction SilentlyContinue)
    {
        try
        {
            $result.Rules = @(
                Get-DnsClientNrptRule -ErrorAction Stop |
                    Select-Object Name, Namespace, NameServers, Comment, DisplayName
            )
        }
        catch
        {
            $result.RulesError = $_.Exception.Message
        }
    }
    else
    {
        $result.RulesError = 'Get-DnsClientNrptRule is unavailable.'
    }

    return $result
}

function Get-DnsProbeResults
{
    $results = @()
    foreach ($name in $DnsNames)
    {
        try
        {
            $answers = @(
                Resolve-DnsName -Name $name -DnsOnly -ErrorAction Stop |
                    Where-Object { $_.Type -in @('A', 'AAAA', 'CNAME') } |
                    Select-Object Name, Type, IPAddress, NameHost
            )

            $results += [pscustomobject]@{
                Name = $name
                Success = $true
                Answers = $answers
                Error = $null
            }
        }
        catch
        {
            $results += [pscustomobject]@{
                Name = $name
                Success = $false
                Answers = @()
                Error = $_.Exception.Message
            }
        }
    }

    return $results
}

function Get-LanProbeResults
{
    $results = @()
    foreach ($target in $LanTargets)
    {
        $icmp = $false
        $icmpError = $null
        try
        {
            $icmp = [bool](Test-Connection -ComputerName $target -Count 1 -Quiet -ErrorAction Stop)
        }
        catch
        {
            $icmpError = $_.Exception.Message
        }

        $tcp = @()
        foreach ($port in $LanTcpPorts)
        {
            $tcp += [pscustomobject]@{
                Port = $port
                Reachable = [bool](Test-TcpPort -Target $target -Port $port)
            }
        }

        $results += [pscustomobject]@{
            Target = $target
            IcmpReachable = $icmp
            IcmpError = $icmpError
            Tcp = $tcp
        }
    }

    return $results
}

function Get-ProtonProcesses
{
    $results = @()
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '(?i)proton' }))
    {
        $path = $null
        $startTime = $null
        try { $path = $process.Path } catch { }
        try { $startTime = $process.StartTime.ToString('o') } catch { }

        $results += [pscustomobject]@{
            ProcessName = $process.ProcessName
            Id = $process.Id
            Path = $path
            StartTime = $startTime
        }
    }

    return $results
}

function Get-ProtonServices
{
    try
    {
        return @(
            Get-CimInstance Win32_Service -ErrorAction Stop |
                Where-Object { $_.Name -match '(?i)proton' -or $_.DisplayName -match '(?i)proton' } |
                Select-Object Name, DisplayName, State, StartMode, ProcessId, PathName
        )
    }
    catch
    {
        return @([pscustomobject]@{ Error = $_.Exception.Message })
    }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss.fff'
if (-not (Test-Path -LiteralPath $OutputRoot))
{
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
}

$resolvedOutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
$script:SnapshotDirectory = Join-Path $resolvedOutputRoot ('{0}-{1}-{2}' -f $timestamp, $KillSwitchMode, $Phase)
New-Item -ItemType Directory -Path $script:SnapshotDirectory -Force | Out-Null

$isAdministrator = Get-IsAdministrator

Write-Capture -Name 'routes.txt' -ScriptBlock {
    Get-NetRoute -ErrorAction Stop |
        Sort-Object AddressFamily, DestinationPrefix, RouteMetric, InterfaceMetric |
        Format-Table -AutoSize -Wrap
} | Out-Null

Write-Capture -Name 'route-print.txt' -ScriptBlock {
    & route.exe print
} | Out-Null

Write-Capture -Name 'ip-configuration.txt' -ScriptBlock {
    Get-NetIPConfiguration -Detailed -ErrorAction Stop | Format-List *
} | Out-Null

Write-Capture -Name 'ip-interfaces.txt' -ScriptBlock {
    Get-NetIPInterface -ErrorAction Stop |
        Sort-Object AddressFamily, InterfaceMetric |
        Format-Table -AutoSize -Wrap
} | Out-Null

Write-Capture -Name 'adapters.txt' -ScriptBlock {
    Get-NetAdapter -IncludeHidden -ErrorAction Stop |
        Sort-Object Name |
        Format-Table -AutoSize -Wrap Name, InterfaceDescription, Status, MacAddress, LinkSpeed, ifIndex
} | Out-Null

Write-Capture -Name 'dns-client.txt' -ScriptBlock {
    Get-DnsClient -ErrorAction Stop | Format-List *
} | Out-Null

Write-Capture -Name 'dns-servers.txt' -ScriptBlock {
    Get-DnsClientServerAddress -ErrorAction Stop |
        Sort-Object InterfaceIndex, AddressFamily |
        Format-Table -AutoSize -Wrap
} | Out-Null

$nrpt = Get-NrptSnapshot
Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'nrpt.json') -Value ($nrpt | ConvertTo-Json -Depth 8) -Encoding UTF8

Write-Capture -Name 'nrpt-effective.txt' -ScriptBlock {
    if (Get-Command Get-DnsClientNrptPolicy -ErrorAction SilentlyContinue)
    {
        Get-DnsClientNrptPolicy -Effective -ErrorAction Stop | Format-List *
    }
    else
    {
        'Get-DnsClientNrptPolicy is unavailable.'
    }
} | Out-Null

Write-Capture -Name 'nrpt-rules.txt' -ScriptBlock {
    if (Get-Command Get-DnsClientNrptRule -ErrorAction SilentlyContinue)
    {
        Get-DnsClientNrptRule -ErrorAction Stop | Format-List *
    }
    else
    {
        'Get-DnsClientNrptRule is unavailable.'
    }
} | Out-Null

$dnsResults = Get-DnsProbeResults
Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'dns-probes.json') -Value ($dnsResults | ConvertTo-Json -Depth 8) -Encoding UTF8

Write-Capture -Name 'nslookup.txt' -ScriptBlock {
    foreach ($name in $DnsNames)
    {
        '===== {0} =====' -f $name
        & nslookup.exe $name
        ''
    }
} | Out-Null

$lanResults = Get-LanProbeResults
Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'lan-probes.json') -Value ($lanResults | ConvertTo-Json -Depth 8) -Encoding UTF8

Write-Capture -Name 'target-routes.txt' -ScriptBlock {
    $targets = @($LanTargets) + @('10.2.0.1')
    foreach ($target in ($targets | Select-Object -Unique))
    {
        '===== {0} =====' -f $target
        try
        {
            Find-NetRoute -RemoteIPAddress $target -ErrorAction Stop | Format-List *
        }
        catch
        {
            'ERROR: {0}' -f $_.Exception.Message
        }
        ''
    }
} | Out-Null

$firewallProfiles = @()
try
{
    $firewallProfiles = @(
        Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop |
            Select-Object Name, Enabled, DefaultInboundAction, DefaultOutboundAction, AllowInboundRules, AllowLocalFirewallRules
    )
}
catch
{
    $firewallProfiles = @([pscustomobject]@{ Error = $_.Exception.Message })
}

$protonFirewallRules = @()
try
{
    $protonFirewallRules = @(
        Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop |
            Where-Object {
                $_.DisplayName -match '(?i)proton' -or
                $_.Group -match '(?i)proton' -or
                $_.Description -match '(?i)proton'
            } |
            Select-Object Name, DisplayName, Group, Enabled, Direction, Action, Profile, PrimaryStatus, Status
    )
}
catch
{
    $protonFirewallRules = @([pscustomobject]@{ Error = $_.Exception.Message })
}

Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'firewall-profiles.json') -Value ($firewallProfiles | ConvertTo-Json -Depth 6) -Encoding UTF8
Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'firewall-rules-proton.json') -Value ($protonFirewallRules | ConvertTo-Json -Depth 6) -Encoding UTF8

Write-Capture -Name 'firewall-profiles.txt' -ScriptBlock {
    Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop | Format-List *
} | Out-Null

Write-Capture -Name 'firewall-rules-proton.txt' -ScriptBlock {
    Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop |
        Where-Object {
            $_.DisplayName -match '(?i)proton' -or
            $_.Group -match '(?i)proton' -or
            $_.Description -match '(?i)proton'
        } |
        Sort-Object DisplayName |
        Format-List Name, DisplayName, Group, Enabled, Direction, Action, Profile, PrimaryStatus, Status
} | Out-Null

Write-Capture -Name 'netsh-advfirewall.txt' -ScriptBlock {
    & netsh.exe advfirewall show allprofiles
} | Out-Null

$wfp = [ordered]@{
    Requested = -not $SkipWfp.IsPresent
    Captured = $false
    Path = $null
    Sha256 = $null
    ProtonMatchCount = $null
    ExitCode = $null
    Error = $null
}

if (-not $SkipWfp.IsPresent)
{
    $wfpPath = Join-Path $script:SnapshotDirectory 'wfp-state.xml'
    try
    {
        $netshOutput = @(& netsh.exe wfp show state "file=$wfpPath" 2>&1)
        $wfp.ExitCode = $LASTEXITCODE
        Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'wfp-netsh.txt') -Value ($netshOutput | Out-String -Width 4096) -Encoding UTF8

        if (($LASTEXITCODE -eq 0) -and (Test-Path -LiteralPath $wfpPath -PathType Leaf))
        {
            $wfp.Captured = $true
            $wfp.Path = $wfpPath
            $wfp.Sha256 = (Get-FileHash -LiteralPath $wfpPath -Algorithm SHA256).Hash
            $matches = @(Select-String -LiteralPath $wfpPath -Pattern 'Proton' -SimpleMatch -ErrorAction SilentlyContinue)
            $wfp.ProtonMatchCount = $matches.Count
            $matches | Select-Object LineNumber, Line | Format-Table -AutoSize -Wrap |
                Out-String -Width 4096 |
                Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'wfp-proton-matches.txt') -Encoding UTF8
        }
        else
        {
            $wfp.Error = 'netsh wfp show state did not create the expected state file.'
        }
    }
    catch
    {
        $wfp.Error = $_.Exception.Message
        Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'wfp-netsh.txt') -Value ('ERROR: {0}' -f $_.Exception.Message) -Encoding UTF8
    }
}

$protonProcesses = Get-ProtonProcesses
$protonServices = Get-ProtonServices
Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'proton-processes.json') -Value ($protonProcesses | ConvertTo-Json -Depth 6) -Encoding UTF8
Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'proton-services.json') -Value ($protonServices | ConvertTo-Json -Depth 6) -Encoding UTF8

$serviceSettings = Get-SafeServiceSettings
Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'service-settings-selected.json') -Value ($serviceSettings | ConvertTo-Json -Depth 6) -Encoding UTF8

$operatingSystem = $null
try
{
    $operatingSystem = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop |
        Select-Object Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime
}
catch
{
    $operatingSystem = [pscustomobject]@{ Error = $_.Exception.Message }
}

$summary = [ordered]@{
    SchemaVersion = 1
    CapturedAt = (Get-Date).ToString('o')
    ComputerName = $env:COMPUTERNAME
    KillSwitchMode = $KillSwitchMode
    Phase = $Phase
    IsAdministrator = $isAdministrator
    UiLanAccessState = $UiLanAccessState
    Note = $Note
    OperatingSystem = $operatingSystem
    LanTargets = @($LanTargets)
    LanTcpPorts = @($LanTcpPorts)
    LanProbeResults = @($lanResults)
    DnsNames = @($DnsNames)
    DnsProbeResults = @($dnsResults)
    Nrpt = [ordered]@{
        EffectivePolicyCount = @($nrpt.EffectivePolicy).Count
        RuleCount = @($nrpt.Rules).Count
        EffectivePolicy = @($nrpt.EffectivePolicy)
        Rules = @($nrpt.Rules)
        EffectivePolicyError = $nrpt.EffectivePolicyError
        RulesError = $nrpt.RulesError
    }
    Firewall = [ordered]@{
        Profiles = @($firewallProfiles)
        ProtonRuleCount = @($protonFirewallRules | Where-Object { $null -eq $_.PSObject.Properties['Error'] }).Count
        Wfp = $wfp
    }
    ProtonProcesses = @($protonProcesses)
    ProtonServices = @($protonServices)
    ServiceSettings = $serviceSettings
}

$summaryPath = Join-Path $script:SnapshotDirectory 'summary.json'
Set-Content -LiteralPath $summaryPath -Value ($summary | ConvertTo-Json -Depth 12) -Encoding UTF8

$readme = @(
    'Guest Hole Windows smoke snapshot',
    '=================================',
    '',
    ('Captured:           {0}' -f $summary.CapturedAt),
    ('Kill switch mode:   {0}' -f $KillSwitchMode),
    ('Phase:              {0}' -f $Phase),
    ('Administrator:      {0}' -f $isAdministrator),
    ('UI LAN access:      {0}' -f $UiLanAccessState),
    ('Service settings:   {0}' -f $serviceSettings.Path),
    ('WFP captured:       {0}' -f $wfp.Captured),
    '',
    'This capture is diagnostic-only. No Proton VPN, firewall, route, service,',
    'process, or Windows network setting was changed by the script.',
    '',
    'Use Compare-GuestHoleWindowsState.ps1 against summary.json files from',
    'the same Soft/Hard scenario to compare transitions.'
)
Set-Content -LiteralPath (Join-Path $script:SnapshotDirectory 'README.txt') -Value $readme -Encoding UTF8

Write-Host ''
Write-Host 'Guest Hole Windows state capture complete.'
Write-Host ('Snapshot: {0}' -f $script:SnapshotDirectory)
if (-not $isAdministrator)
{
    Write-Warning 'The shell is not elevated. WFP/firewall/service observations may be incomplete; rerun from an elevated PowerShell for authoritative smoke results.'
}
if ($null -eq $serviceSettings.Path)
{
    Write-Warning 'Service settings were not found. Pass -ServiceSettingsPath with the exact persisted service-settings JSON path for the next capture.'
}
if ((-not $SkipWfp.IsPresent) -and (-not $wfp.Captured))
{
    Write-Warning ('WFP state was not captured: {0}' -f $wfp.Error)
}
