# Domain Split Tunneling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add domain-based split tunneling by observing Windows system DNS cache entries and temporarily excluding resolved IPs from the VPN.

**Architecture:** Keep persisted split-tunnel address settings as strings, classify them in the service as either static IP/CIDR entries or suffix-aware domain rules, then poll the Windows DNS client cache for matching hostnames. Matching DNS A records become temporary IPv4 exclusions managed through the existing permitted-address and route-exclusion path, with TTL clamping and cleanup on settings changes or disconnect.

**Tech Stack:** C#/.NET 8, MSTest, NSubstitute, FluentAssertions, Windows `Get-DnsClientCache`, existing ProtonVPN service split tunneling classes.

---

## File Structure

- Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainRule.cs`
  - Parses and matches user-entered domain rules such as `example.com` and `*.example.com`.
- Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheEntry.cs`
  - Represents one DNS cache record relevant to split tunneling.
- Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheParser.cs`
  - Parses compact JSON output from `Get-DnsClientCache`.
- Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/ISystemDnsCacheReader.cs`
  - Abstraction for reading DNS cache records.
- Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheReader.cs`
  - Runs Windows PowerShell `Get-DnsClientCache` and uses `SystemDnsCacheParser`.
- Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainResolvedAddress.cs`
  - Stores a domain-owned resolved IP with expiry metadata.
- Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainResolvedAddressTracker.cs`
  - Owns temporary domain-derived IP exclusions, TTL clamping, refresh, and expiry.
- Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/ISplitTunnelDomainPoller.cs`
  - Service-facing interface for DNS cache polling.
- Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SplitTunnelDomainPoller.cs`
  - Maintains active domain rules, polls DNS cache, and publishes active temporary IPv4 exclusions.
- Create `src/ProtonVPN.Service/SplitTunneling/ExcludedIpRouteManager.cs`
  - Move the existing nested `SplitTunnel.ExcludedIpRouteManager` class out so it can be tested and used cleanly by combined static/dynamic address logic.
- Modify `src/ProtonVPN.Service/SplitTunneling/SplitTunnel.cs`
  - Separate configured IP/CIDR entries from domain rules.
  - Start/stop the domain poller in standard exclude mode.
  - Apply combined configured + domain-derived IPv4 exclusions.
- Modify `src/ProtonVPN.Service/Start/ServiceModule.cs`
  - Register new domain split tunneling services.
- Test files:
  - Create `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/DomainRuleTest.cs`
  - Create `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/SystemDnsCacheParserTest.cs`
  - Create `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/DomainResolvedAddressTrackerTest.cs`
  - Create `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/SplitTunnelDomainPollerTest.cs`
  - Modify `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/SplitTunnelTest.cs`

## Task 1: Domain Rule Parser

**Files:**
- Create: `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainRule.cs`
- Test: `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/DomainRuleTest.cs`

- [ ] **Step 1: Write the failing domain rule tests**

Create `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/DomainRuleTest.cs`:

```csharp
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

namespace ProtonVPN.Service.Tests.SplitTunneling.DomainSplitTunneling;

[TestClass]
public class DomainRuleTest
{
    [TestMethod]
    public void TryCreate_ShouldNormalizePlainDomain()
    {
        bool result = DomainRule.TryCreate(" Example.COM. ", out DomainRule? rule);

        result.Should().BeTrue();
        rule.Should().NotBeNull();
        rule!.Domain.Should().Be("example.com");
    }

    [TestMethod]
    public void TryCreate_ShouldTreatLeadingWildcardAsAlias()
    {
        bool result = DomainRule.TryCreate("*.Example.COM", out DomainRule? rule);

        result.Should().BeTrue();
        rule.Should().NotBeNull();
        rule!.Domain.Should().Be("example.com");
    }

    [DataTestMethod]
    [DataRow("https://example.com")]
    [DataRow("example.com/path")]
    [DataRow("exa*mple.com")]
    [DataRow("*example.com")]
    [DataRow("")]
    [DataRow(" ")]
    public void TryCreate_ShouldRejectInvalidDomainRules(string value)
    {
        DomainRule.TryCreate(value, out _).Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("example.com")]
    [DataRow("www.example.com")]
    [DataRow("login.example.com")]
    [DataRow("api.foo.example.com")]
    [DataRow("EXAMPLE.COM")]
    public void IsMatch_ShouldMatchDomainAndSubdomains(string hostname)
    {
        DomainRule.TryCreate("example.com", out DomainRule? rule).Should().BeTrue();

        rule!.IsMatch(hostname).Should().BeTrue();
    }

    [DataTestMethod]
    [DataRow("badexample.com")]
    [DataRow("example.com.bad")]
    [DataRow("another.com")]
    [DataRow("")]
    public void IsMatch_ShouldRejectNonBoundaryMatches(string hostname)
    {
        DomainRule.TryCreate("example.com", out DomainRule? rule).Should().BeTrue();

        rule!.IsMatch(hostname).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the failing domain rule tests**

Run:

```powershell
dotnet test src\Tests\ProtonVPN.Service.Tests\ProtonVPN.Service.Tests.csproj --filter FullyQualifiedName~DomainRuleTest --no-restore
```

Expected: build fails because `DomainRule` does not exist.

- [ ] **Step 3: Implement `DomainRule`**

Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainRule.cs`:

```csharp
using System;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public sealed class DomainRule
{
    public string Domain { get; }

    private DomainRule(string domain)
    {
        Domain = domain;
    }

    public static bool TryCreate(string? value, out DomainRule? rule)
    {
        rule = null;
        string normalized = Normalize(value);

        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('/') ||
            normalized.Contains(':'))
        {
            return false;
        }

        if (normalized.StartsWith("*."))
        {
            normalized = normalized[2..];
        }
        else if (normalized.Contains('*'))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(normalized) ||
            Uri.CheckHostName(normalized) != UriHostNameType.Dns)
        {
            return false;
        }

        rule = new DomainRule(normalized);
        return true;
    }

    public bool IsMatch(string? hostname)
    {
        string normalized = Normalize(hostname);
        return normalized.Equals(Domain, StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith($".{Domain}", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
    }
}
```

- [ ] **Step 4: Run the domain rule tests**

Run:

```powershell
dotnet test src\Tests\ProtonVPN.Service.Tests\ProtonVPN.Service.Tests.csproj --filter FullyQualifiedName~DomainRuleTest --no-restore
```

Expected: all `DomainRuleTest` tests pass.

- [ ] **Step 5: Commit the domain rule parser**

```powershell
git add src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainRule.cs src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/DomainRuleTest.cs
git commit -m "Add split tunnel domain rule parser"
```

## Task 2: DNS Cache Parser

**Files:**
- Create: `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheEntry.cs`
- Create: `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheParser.cs`
- Test: `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/SystemDnsCacheParserTest.cs`

- [ ] **Step 1: Write failing DNS cache parser tests**

Create `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/SystemDnsCacheParserTest.cs`:

```csharp
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

namespace ProtonVPN.Service.Tests.SplitTunneling.DomainSplitTunneling;

[TestClass]
public class SystemDnsCacheParserTest
{
    [TestMethod]
    public void Parse_ShouldReadSingleDnsCacheObject()
    {
        string json = """
        {"Entry":"login.example.com","Data":"203.0.113.10","Type":1,"TimeToLive":240}
        """;

        SystemDnsCacheEntry[] entries = SystemDnsCacheParser.Parse(json).ToArray();

        entries.Should().ContainSingle();
        entries[0].Hostname.Should().Be("login.example.com");
        entries[0].IpAddress.ToString().Should().Be("203.0.113.10");
        entries[0].TimeToLiveSeconds.Should().Be(240);
    }

    [TestMethod]
    public void Parse_ShouldReadArrayAndIgnoreInvalidRows()
    {
        string json = """
        [
          {"Entry":"api.example.com","Data":"203.0.113.11","Type":1,"TimeToLive":60},
          {"Entry":"ipv6.example.com","Data":"2001:db8::1","Type":28,"TimeToLive":60},
          {"Entry":"text.example.com","Data":"not-an-ip","Type":16,"TimeToLive":60},
          {"Entry":"","Data":"203.0.113.12","Type":1,"TimeToLive":60}
        ]
        """;

        SystemDnsCacheEntry[] entries = SystemDnsCacheParser.Parse(json).ToArray();

        entries.Should().HaveCount(2);
        entries.Select(e => e.Hostname).Should().BeEquivalentTo("api.example.com", "ipv6.example.com");
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("null")]
    [DataRow("{}")]
    public void Parse_ShouldReturnEmptyForNoUsableEntries(string json)
    {
        SystemDnsCacheParser.Parse(json).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the failing parser tests**

Run:

```powershell
dotnet test src\Tests\ProtonVPN.Service.Tests\ProtonVPN.Service.Tests.csproj --filter FullyQualifiedName~SystemDnsCacheParserTest --no-restore
```

Expected: build fails because `SystemDnsCacheParser` does not exist.

- [ ] **Step 3: Implement DNS cache entry and parser**

Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheEntry.cs`:

```csharp
using System.Net;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public sealed record SystemDnsCacheEntry(
    string Hostname,
    IPAddress IpAddress,
    int TimeToLiveSeconds);
```

Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheParser.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public static class SystemDnsCacheParser
{
    public static IEnumerable<SystemDnsCacheEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().SelectMany(ParseElement).ToArray(),
                JsonValueKind.Object => ParseElement(document.RootElement).ToArray(),
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<SystemDnsCacheEntry> ParseElement(JsonElement element)
    {
        if (!TryGetString(element, "Entry", out string hostname) ||
            !TryGetString(element, "Data", out string data) ||
            !TryGetInt(element, "TimeToLive", out int ttl) ||
            string.IsNullOrWhiteSpace(hostname) ||
            !IPAddress.TryParse(data, out IPAddress? ipAddress))
        {
            yield break;
        }

        yield return new SystemDnsCacheEntry(
            hostname.Trim().TrimEnd('.').ToLowerInvariant(),
            ipAddress,
            ttl);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out JsonElement property) &&
               property.TryGetInt32(out value);
    }
}
```

- [ ] **Step 4: Run parser tests**

Run:

```powershell
dotnet test src\Tests\ProtonVPN.Service.Tests\ProtonVPN.Service.Tests.csproj --filter FullyQualifiedName~SystemDnsCacheParserTest --no-restore
```

Expected: all `SystemDnsCacheParserTest` tests pass.

- [ ] **Step 5: Commit the DNS cache parser**

```powershell
git add src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheEntry.cs src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheParser.cs src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/SystemDnsCacheParserTest.cs
git commit -m "Parse Windows DNS cache entries for split tunneling"
```

## Task 3: DNS Cache Reader

**Files:**
- Create: `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/ISystemDnsCacheReader.cs`
- Create: `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheReader.cs`
- Modify: `src/ProtonVPN.Service/Start/ServiceModule.cs`

- [ ] **Step 1: Implement the DNS cache reader interface**

Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/ISystemDnsCacheReader.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public interface ISystemDnsCacheReader
{
    Task<IReadOnlyCollection<SystemDnsCacheEntry>> ReadAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Implement the PowerShell-backed reader**

Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheReader.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.SplitTunnelLogs;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public sealed class SystemDnsCacheReader : ISystemDnsCacheReader
{
    private const int TIMEOUT_MILLISECONDS = 5000;
    private readonly ILogger _logger;

    public SystemDnsCacheReader(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<SystemDnsCacheEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        string command = "Get-DnsClientCache | " +
                         "Where-Object { ($_.Type -eq 1 -or $_.Type -eq 28) -and $_.Status -eq 0 -and $_.Data } | " +
                         "Select-Object Entry,Data,Type,TimeToLive | ConvertTo-Json -Compress";

        try
        {
            string json = await RunPowerShellAsync(command, cancellationToken);
            return SystemDnsCacheParser.Parse(json).ToArray();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.Warn<SplitTunnelLog>("Failed to read Windows DNS client cache for domain split tunneling.", e);
            return [];
        }
    }

    private static async Task<string> RunPowerShellAsync(string command, CancellationToken cancellationToken)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        process.Start();

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task waitForExitTask = process.WaitForExitAsync(cancellationToken);

        if (await Task.WhenAny(waitForExitTask, Task.Delay(TIMEOUT_MILLISECONDS, cancellationToken)) != waitForExitTask)
        {
            TryKill(process);
            throw new TimeoutException("Timed out reading Windows DNS client cache.");
        }

        string output = await outputTask;
        string error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Get-DnsClientCache failed with exit code {process.ExitCode}. Error: {error}");
        }

        return output;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
        }
    }
}
```

- [ ] **Step 3: Register the DNS cache reader**

Modify `src/ProtonVPN.Service/Start/ServiceModule.cs`, adding this registration beside the split tunneling registrations:

```csharp
builder.RegisterType<SystemDnsCacheReader>().AsImplementedInterfaces().SingleInstance();
```

Add the namespace near the other `using ProtonVPN.Service...` statements:

```csharp
using ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;
```

- [ ] **Step 4: Build service project**

Run:

```powershell
dotnet build src\ProtonVPN.Service\ProtonVPN.Service.csproj --configuration Release --runtime win-x64 --no-restore -p:Platform=x64
```

Expected: service builds successfully. If local SDK is unavailable, run the GitHub fast patch workflow with `build_mode=service` after pushing the branch.

- [ ] **Step 5: Commit the DNS cache reader**

```powershell
git add src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/ISystemDnsCacheReader.cs src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SystemDnsCacheReader.cs src/ProtonVPN.Service/Start/ServiceModule.cs
git commit -m "Read Windows DNS cache for domain split tunneling"
```

## Task 4: Temporary Domain Address Tracker

**Files:**
- Create: `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainResolvedAddress.cs`
- Create: `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainResolvedAddressTracker.cs`
- Test: `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/DomainResolvedAddressTrackerTest.cs`

- [ ] **Step 1: Write failing tracker tests**

Create `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/DomainResolvedAddressTrackerTest.cs`:

```csharp
using System;
using System.Linq;
using System.Net;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

namespace ProtonVPN.Service.Tests.SplitTunneling.DomainSplitTunneling;

[TestClass]
public class DomainResolvedAddressTrackerTest
{
    [TestMethod]
    public void AddOrRefresh_ShouldClampLowTtlAndAddGrace()
    {
        DateTime now = new(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
        DomainResolvedAddressTracker tracker = new(() => now);

        tracker.AddOrRefresh("example.com", IPAddress.Parse("203.0.113.10"), 1);

        DomainResolvedAddress entry = tracker.GetActive().Single();
        entry.ExpiresAtUtc.Should().Be(now.AddMinutes(6));
    }

    [TestMethod]
    public void AddOrRefresh_ShouldClampHighTtlToOneHour()
    {
        DateTime now = new(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
        DomainResolvedAddressTracker tracker = new(() => now);

        tracker.AddOrRefresh("example.com", IPAddress.Parse("203.0.113.10"), 86400);

        DomainResolvedAddress entry = tracker.GetActive().Single();
        entry.ExpiresAtUtc.Should().Be(now.AddHours(1));
    }

    [TestMethod]
    public void PruneExpired_ShouldKeepSharedIpUntilAllOwnersExpire()
    {
        DateTime now = new(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
        DomainResolvedAddressTracker tracker = new(() => now);
        IPAddress ipAddress = IPAddress.Parse("203.0.113.10");

        tracker.AddOrRefresh("example.com", ipAddress, 60);
        now = now.AddMinutes(2);
        tracker.AddOrRefresh("other.com", ipAddress, 60);
        now = now.AddMinutes(5);

        tracker.PruneExpired();

        tracker.GetActive().Select(e => e.IpAddress.ToString()).Should().ContainSingle("203.0.113.10");
    }

    [TestMethod]
    public void Clear_ShouldRemoveAllEntries()
    {
        DateTime now = new(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
        DomainResolvedAddressTracker tracker = new(() => now);
        tracker.AddOrRefresh("example.com", IPAddress.Parse("203.0.113.10"), 60);

        tracker.Clear();

        tracker.GetActive().Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run failing tracker tests**

Run:

```powershell
dotnet test src\Tests\ProtonVPN.Service.Tests\ProtonVPN.Service.Tests.csproj --filter FullyQualifiedName~DomainResolvedAddressTrackerTest --no-restore
```

Expected: build fails because tracker classes do not exist.

- [ ] **Step 3: Implement resolved address record**

Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainResolvedAddress.cs`:

```csharp
using System;
using System.Net;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public sealed record DomainResolvedAddress(
    string OwnerDomain,
    IPAddress IpAddress,
    DateTime ExpiresAtUtc);
```

- [ ] **Step 4: Implement tracker**

Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainResolvedAddressTracker.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public sealed class DomainResolvedAddressTracker
{
    private static readonly TimeSpan MIN_TTL = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GRACE_PERIOD = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MAX_LIFETIME = TimeSpan.FromHours(1);

    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<(string OwnerDomain, string IpAddress), DomainResolvedAddress> _entries = new();

    public DomainResolvedAddressTracker()
        : this(() => DateTime.UtcNow)
    {
    }

    public DomainResolvedAddressTracker(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
    }

    public void AddOrRefresh(string ownerDomain, IPAddress ipAddress, int ttlSeconds)
    {
        string normalizedOwner = ownerDomain.Trim().TrimEnd('.').ToLowerInvariant();
        string ip = ipAddress.ToString();
        TimeSpan ttl = TimeSpan.FromSeconds(Math.Max(0, ttlSeconds));
        TimeSpan lifetime = ttl < MIN_TTL ? MIN_TTL : ttl;
        lifetime += GRACE_PERIOD;
        if (lifetime > MAX_LIFETIME)
        {
            lifetime = MAX_LIFETIME;
        }

        _entries[(normalizedOwner, ip)] = new DomainResolvedAddress(
            normalizedOwner,
            ipAddress,
            _utcNow().Add(lifetime));
    }

    public IReadOnlyCollection<DomainResolvedAddress> GetActive()
    {
        PruneExpired();
        return _entries.Values.ToArray();
    }

    public string[] GetActiveIpv4Addresses()
    {
        return GetActive()
            .Where(entry => entry.IpAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(entry => entry.IpAddress.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void PruneExpired()
    {
        DateTime now = _utcNow();
        foreach (var key in _entries.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _entries.Remove(key);
        }
    }

    public void Clear()
    {
        _entries.Clear();
    }
}
```

- [ ] **Step 5: Run tracker tests**

Run:

```powershell
dotnet test src\Tests\ProtonVPN.Service.Tests\ProtonVPN.Service.Tests.csproj --filter FullyQualifiedName~DomainResolvedAddressTrackerTest --no-restore
```

Expected: all tracker tests pass.

- [ ] **Step 6: Commit tracker**

```powershell
git add src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainResolvedAddress.cs src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/DomainResolvedAddressTracker.cs src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/DomainResolvedAddressTrackerTest.cs
git commit -m "Track temporary domain split tunnel addresses"
```

## Task 5: Domain DNS Poller

**Files:**
- Create: `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/ISplitTunnelDomainPoller.cs`
- Create: `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SplitTunnelDomainPoller.cs`
- Test: `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/SplitTunnelDomainPollerTest.cs`
- Modify: `src/ProtonVPN.Service/Start/ServiceModule.cs`

- [ ] **Step 1: Write failing poller tests**

Create `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/SplitTunnelDomainPollerTest.cs`:

```csharp
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

namespace ProtonVPN.Service.Tests.SplitTunneling.DomainSplitTunneling;

[TestClass]
public class SplitTunnelDomainPollerTest
{
    private ISystemDnsCacheReader _dnsCacheReader = null!;
    private SplitTunnelDomainPoller _poller = null!;

    [TestInitialize]
    public void SetUp()
    {
        _dnsCacheReader = Substitute.For<ISystemDnsCacheReader>();
        _poller = new SplitTunnelDomainPoller(_dnsCacheReader);
    }

    [TestMethod]
    public async Task PollOnceAsync_ShouldPublishMatchingDomainAddresses()
    {
        _dnsCacheReader.ReadAsync(Arg.Any<CancellationToken>()).Returns([
            new SystemDnsCacheEntry("login.example.com", IPAddress.Parse("203.0.113.10"), 60),
            new SystemDnsCacheEntry("unrelated.test", IPAddress.Parse("203.0.113.11"), 60),
        ]);
        _poller.ReplaceRules(["example.com"]);
        string[]? latest = null;
        _poller.AddressesChanged += (_, addresses) => latest = addresses;

        await _poller.PollOnceAsync(CancellationToken.None);

        latest.Should().BeEquivalentTo(["203.0.113.10"]);
    }

    [TestMethod]
    public async Task PollOnceAsync_ShouldIgnoreIpv6AddressesForFirstVersion()
    {
        _dnsCacheReader.ReadAsync(Arg.Any<CancellationToken>()).Returns([
            new SystemDnsCacheEntry("login.example.com", IPAddress.Parse("2001:db8::1"), 60),
        ]);
        _poller.ReplaceRules(["example.com"]);
        string[]? latest = null;
        _poller.AddressesChanged += (_, addresses) => latest = addresses;

        await _poller.PollOnceAsync(CancellationToken.None);

        latest.Should().BeEquivalentTo(Array.Empty<string>());
    }

    [TestMethod]
    public void ReplaceRules_ShouldClearAddressesWhenNoDomainRulesRemain()
    {
        _poller.ReplaceRules(["example.com"]);
        string[]? latest = null;
        _poller.AddressesChanged += (_, addresses) => latest = addresses;

        _poller.ReplaceRules([]);

        latest.Should().BeEquivalentTo(Array.Empty<string>());
    }
}
```

- [ ] **Step 2: Run failing poller tests**

Run:

```powershell
dotnet test src\Tests\ProtonVPN.Service.Tests\ProtonVPN.Service.Tests.csproj --filter FullyQualifiedName~SplitTunnelDomainPollerTest --no-restore
```

Expected: build fails because poller classes do not exist.

- [ ] **Step 3: Implement poller interface**

Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/ISplitTunnelDomainPoller.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public interface ISplitTunnelDomainPoller
{
    event EventHandler<string[]> AddressesChanged;

    void ReplaceRules(string[] rawRules);

    void Stop();

    Task PollOnceAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement poller**

Create `src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SplitTunnelDomainPoller.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public sealed class SplitTunnelDomainPoller : ISplitTunnelDomainPoller, IDisposable
{
    private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds(15);

    private readonly ISystemDnsCacheReader _dnsCacheReader;
    private readonly DomainResolvedAddressTracker _tracker = new();
    private readonly object _sync = new();

    private DomainRule[] _rules = [];
    private CancellationTokenSource? _pollingCts;
    private string[] _lastPublishedAddresses = [];

    public event EventHandler<string[]>? AddressesChanged;

    public SplitTunnelDomainPoller(ISystemDnsCacheReader dnsCacheReader)
    {
        _dnsCacheReader = dnsCacheReader;
    }

    public void ReplaceRules(string[] rawRules)
    {
        DomainRule[] rules = rawRules
            .Select(rawRule => DomainRule.TryCreate(rawRule, out DomainRule? rule) ? rule : null)
            .Where(rule => rule is not null)
            .Select(rule => rule!)
            .DistinctBy(rule => rule.Domain)
            .ToArray();

        lock (_sync)
        {
            _rules = rules;
            _tracker.Clear();
            _lastPublishedAddresses = [];
        }

        Publish([]);

        if (rules.Length == 0)
        {
            Stop();
            return;
        }

        EnsurePollingStarted();
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        DomainRule[] rules;
        lock (_sync)
        {
            rules = _rules;
        }

        if (rules.Length == 0)
        {
            Publish([]);
            return;
        }

        IReadOnlyCollection<SystemDnsCacheEntry> entries = await _dnsCacheReader.ReadAsync(cancellationToken);
        foreach (SystemDnsCacheEntry entry in entries)
        {
            DomainRule? matchingRule = rules.FirstOrDefault(rule => rule.IsMatch(entry.Hostname));
            if (matchingRule is null || entry.IpAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            _tracker.AddOrRefresh(matchingRule.Domain, entry.IpAddress, entry.TimeToLiveSeconds);
        }

        Publish(_tracker.GetActiveIpv4Addresses());
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _pollingCts;
            _pollingCts = null;
            _rules = [];
            _tracker.Clear();
            _lastPublishedAddresses = [];
        }

        cts?.Cancel();
        cts?.Dispose();
        Publish([]);
    }

    private void EnsurePollingStarted()
    {
        lock (_sync)
        {
            if (_pollingCts is not null)
            {
                return;
            }

            _pollingCts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoopAsync(_pollingCts.Token));
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollOnceAsync(cancellationToken);
            await Task.Delay(POLL_INTERVAL, cancellationToken);
        }
    }

    private void Publish(string[] addresses)
    {
        string[] normalized = addresses.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a).ToArray();
        bool changed;
        lock (_sync)
        {
            changed = !_lastPublishedAddresses.SequenceEqual(normalized);
            if (changed)
            {
                _lastPublishedAddresses = normalized;
            }
        }

        if (changed)
        {
            AddressesChanged?.Invoke(this, normalized);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
```

- [ ] **Step 5: Register poller**

Modify `src/ProtonVPN.Service/Start/ServiceModule.cs`, adding:

```csharp
builder.RegisterType<SplitTunnelDomainPoller>().AsImplementedInterfaces().SingleInstance();
```

- [ ] **Step 6: Run poller tests**

Run:

```powershell
dotnet test src\Tests\ProtonVPN.Service.Tests\ProtonVPN.Service.Tests.csproj --filter FullyQualifiedName~SplitTunnelDomainPollerTest --no-restore
```

Expected: all poller tests pass.

- [ ] **Step 7: Commit poller**

```powershell
git add src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/ISplitTunnelDomainPoller.cs src/ProtonVPN.Service/SplitTunneling/DomainSplitTunneling/SplitTunnelDomainPoller.cs src/ProtonVPN.Service/Start/ServiceModule.cs src/Tests/ProtonVPN.Service.Tests/SplitTunneling/DomainSplitTunneling/SplitTunnelDomainPollerTest.cs
git commit -m "Poll DNS cache for domain split tunnel matches"
```

## Task 6: SplitTunnel Integration

**Files:**
- Create: `src/ProtonVPN.Service/SplitTunneling/ExcludedIpRouteManager.cs`
- Modify: `src/ProtonVPN.Service/SplitTunneling/SplitTunnel.cs`
- Modify: `src/Tests/ProtonVPN.Service.Tests/SplitTunneling/SplitTunnelTest.cs`

- [ ] **Step 1: Move `ExcludedIpRouteManager` out of `SplitTunnel`**

Create `src/ProtonVPN.Service/SplitTunneling/ExcludedIpRouteManager.cs` by moving the existing private nested class from `SplitTunnel.cs` unchanged except namespace/class accessibility:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using ProtonVPN.OperatingSystems.Network.Contracts;
using CoreNetworkAddress = ProtonVPN.Common.Core.Networking.NetworkAddress;

namespace ProtonVPN.Service.SplitTunneling;

internal sealed class ExcludedIpRouteManager
{
    private readonly HashSet<ExcludedIpRoute> _routes = [];

    public void Replace(string[] addresses, INetworkInterface networkInterface)
    {
        if (!HasUsableGateway(networkInterface))
        {
            RemoveAll();
            return;
        }

        HashSet<ExcludedIpRoute> desiredRoutes = GetDesiredRoutes(addresses, networkInterface).ToHashSet();
        bool hasFailures = false;

        foreach (ExcludedIpRoute route in desiredRoutes.Where(route => !_routes.Contains(route)).ToList())
        {
            DeleteRoute(route);
            if (TryAddRoute(route))
            {
                _routes.Add(route);
            }
            else
            {
                hasFailures = true;
            }
        }

        if (hasFailures)
        {
            return;
        }

        foreach (ExcludedIpRoute staleRoute in _routes.Where(route => !desiredRoutes.Contains(route)).ToList())
        {
            DeleteRoute(staleRoute);
            _routes.Remove(staleRoute);
        }
    }

    public void RemoveAll()
    {
        foreach (ExcludedIpRoute route in _routes.ToList())
        {
            DeleteRoute(route);
            _routes.Remove(route);
        }
    }

    private static IEnumerable<ExcludedIpRoute> GetDesiredRoutes(string[] addresses, INetworkInterface networkInterface)
    {
        foreach (string address in addresses ?? [])
        {
            if (CoreNetworkAddress.TryParse(address, out CoreNetworkAddress networkAddress) && networkAddress.IsIpV4)
            {
                yield return ExcludedIpRoute.From(networkAddress, networkInterface.Index, networkInterface.DefaultGateway.ToString());
            }
        }
    }

    private static bool HasUsableGateway(INetworkInterface networkInterface)
    {
        return networkInterface is not null &&
               networkInterface.Index > 0 &&
               networkInterface.DefaultGateway is not null &&
               !networkInterface.DefaultGateway.Equals(IPAddress.Any) &&
               !networkInterface.DefaultGateway.Equals(IPAddress.None);
    }

    private static bool TryAddRoute(ExcludedIpRoute route)
    {
        try
        {
            RunNetsh($"interface ipv4 add route prefix={route.Prefix} interface={route.InterfaceIndex} nexthop={route.NextHop} metric=1 store=active");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteRoute(ExcludedIpRoute route)
    {
        try
        {
            RunNetsh($"interface ipv4 delete route prefix={route.Prefix} interface={route.InterfaceIndex} nexthop={route.NextHop} store=active");
        }
        catch
        {
        }
    }

    private static void RunNetsh(string arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        process.Start();

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(5000))
        {
            TryKill(process);
            throw new InvalidOperationException($"netsh timed out. Arguments: {arguments}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"netsh failed with exit code {process.ExitCode}. Arguments: {arguments}. Output: {output}. Error: {error}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch
        {
        }
    }

    private readonly record struct ExcludedIpRoute(string Prefix, uint InterfaceIndex, string NextHop)
    {
        public static ExcludedIpRoute From(CoreNetworkAddress networkAddress, uint interfaceIndex, string nextHop)
        {
            string prefix = networkAddress.IsSingleIp
                ? $"{networkAddress.Ip}/32"
                : networkAddress.ToString();

            return new ExcludedIpRoute(prefix, interfaceIndex, nextHop);
        }
    }
}
```

Delete the nested `ExcludedIpRouteManager` class from `SplitTunnel.cs`.

- [ ] **Step 2: Update `SplitTunnel` constructor to accept the poller**

In `src/ProtonVPN.Service/SplitTunneling/SplitTunnel.cs`, add:

```csharp
using ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;
```

Add fields:

```csharp
private readonly ISplitTunnelDomainPoller _domainPoller;
private readonly object _remoteAddressSync = new();
private string[] _configuredRemoteAddresses = [];
private string[] _domainRemoteAddresses = [];
private INetworkInterface _lastBestInterface;
```

Update both constructors to accept and assign `ISplitTunnelDomainPoller domainPoller`. In the main constructor:

```csharp
_domainPoller = domainPoller;
_domainPoller.AddressesChanged += OnDomainAddressesChanged;
```

- [ ] **Step 3: Add static/domain address separation helpers**

Add these methods to `SplitTunnel.cs`:

```csharp
private string[] GetConfiguredRemoteAddresses(bool allowIpv6)
{
    return (_serviceSettings.SplitTunnelSettings.Ips ?? [])
        .SelectMany(rawAddress => GetConfiguredRemoteAddresses(rawAddress, allowIpv6))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

private IEnumerable<string> GetConfiguredRemoteAddresses(string rawAddress, bool allowIpv6)
{
    string address = rawAddress.Trim();
    if (!CoreNetworkAddress.TryParse(address, out CoreNetworkAddress networkAddress))
    {
        yield break;
    }

    if (!networkAddress.IsIpV6 || allowIpv6)
    {
        yield return networkAddress.ToString();
    }
}

private string[] GetDomainRules()
{
    return (_serviceSettings.SplitTunnelSettings.Ips ?? [])
        .Where(rawAddress => !CoreNetworkAddress.TryParse(rawAddress.Trim(), out _))
        .Where(rawAddress => DomainRule.TryCreate(rawAddress, out _))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
```

- [ ] **Step 4: Add combined remote address application**

Add these methods to `SplitTunnel.cs`:

```csharp
private void ApplyRemoteAddressExclusions(string[] configuredAddresses, string[] domainAddresses, INetworkInterface bestInterface)
{
    string[] combinedAddresses = configuredAddresses
        .Concat(domainAddresses)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    _permittedRemoteAddress.Add(combinedAddresses, Action.HardPermit);
    _excludedIpRouteManager.Replace(combinedAddresses, bestInterface);
}

private void OnDomainAddressesChanged(object sender, string[] domainAddresses)
{
    lock (_remoteAddressSync)
    {
        _domainRemoteAddresses = domainAddresses;
        if (_lastBestInterface is not null)
        {
            ApplyRemoteAddressExclusions(_configuredRemoteAddresses, _domainRemoteAddresses, _lastBestInterface);
        }
    }
}

private void ClearDomainSplitTunnelState()
{
    lock (_remoteAddressSync)
    {
        _configuredRemoteAddresses = [];
        _domainRemoteAddresses = [];
        _lastBestInterface = null;
    }

    _domainPoller.Stop();
}
```

- [ ] **Step 5: Replace current hostname resolution in `Enable`**

In `SplitTunnel.Enable`, replace:

```csharp
string[] permittedRemoteAddresses = GetPermittedRemoteAddresses(localIpv6Address is not null);
_permittedRemoteAddress.Add(permittedRemoteAddresses, Action.HardPermit);
_excludedIpRouteManager.Replace(permittedRemoteAddresses, bestInterface);
```

with:

```csharp
string[] configuredRemoteAddresses = GetConfiguredRemoteAddresses(localIpv6Address is not null);
string[] domainRules = GetDomainRules();

lock (_remoteAddressSync)
{
    _configuredRemoteAddresses = configuredRemoteAddresses;
    _domainRemoteAddresses = [];
    _lastBestInterface = bestInterface;
    ApplyRemoteAddressExclusions(_configuredRemoteAddresses, _domainRemoteAddresses, bestInterface);
}

_domainPoller.ReplaceRules(domainRules);
```

Remove the old `GetPermittedRemoteAddresses`, `ResolveHostname`, and `IsValidHostname` methods from `SplitTunnel.cs`.

- [ ] **Step 6: Stop poller during cleanup paths**

In `DisableSplitTunnel`, `OnVpnDisconnected`, and disabled-mode handling, call:

```csharp
ClearDomainSplitTunnelState();
```

Make sure the final cleanup still calls:

```csharp
_permittedRemoteAddress.RemoveAll();
_excludedIpRouteManager.RemoveAll();
```

- [ ] **Step 7: Update tests for constructor dependency**

Modify `SplitTunnelTest.cs`:

Add field:

```csharp
private ISplitTunnelDomainPoller _domainPoller;
```

Initialize:

```csharp
_domainPoller = Substitute.For<ISplitTunnelDomainPoller>();
```

Pass `_domainPoller` into `new SplitTunnel(...)` in `GetSplitTunnel`.

- [ ] **Step 8: Add split tunnel integration tests**

Add tests to `SplitTunnelTest.cs`:

```csharp
[TestMethod]
public void OnVpnConnected_WhenBlockModeWithDomainRules_StartsDomainPoller()
{
    _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
    {
        Mode = SplitTunnelModeIpcEntity.Block,
        AppPaths = [],
        Ips = ["example.com"],
    });
    SplitTunnel splitTunnel = GetSplitTunnel();

    splitTunnel.OnVpnConnected(GetConnectedVpnState());

    _domainPoller.Received(1).ReplaceRules(Arg.Is<string[]>(rules => rules.SequenceEqual(new[] { "example.com" })));
}

[TestMethod]
public void OnVpnConnected_WhenBlockModeWithIpAndDomain_AppliesOnlyConfiguredIpImmediately()
{
    _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
    {
        Mode = SplitTunnelModeIpcEntity.Block,
        AppPaths = [],
        Ips = ["8.8.8.8", "example.com"],
    });
    SplitTunnel splitTunnel = GetSplitTunnel();

    splitTunnel.OnVpnConnected(GetConnectedVpnState());

    _permittedRemoteAddress.Received(1).Add(
        Arg.Is<string[]>(addresses => addresses.SequenceEqual(new[] { "8.8.8.8/32" }) || addresses.SequenceEqual(new[] { "8.8.8.8" })),
        NetworkFilter.Action.HardPermit);
}

[TestMethod]
public void OnVpnDisconnected_StopsDomainPoller()
{
    _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
    {
        Mode = SplitTunnelModeIpcEntity.Block,
        AppPaths = [],
        Ips = ["example.com"],
    });
    SplitTunnel splitTunnel = GetSplitTunnel();
    splitTunnel.OnVpnConnected(GetConnectedVpnState());

    splitTunnel.OnVpnDisconnected(GetDisconnectedVpnState(true));

    _domainPoller.Received(1).Stop();
}
```

- [ ] **Step 9: Run split tunnel tests**

Run:

```powershell
dotnet test src\Tests\ProtonVPN.Service.Tests\ProtonVPN.Service.Tests.csproj --filter FullyQualifiedName~SplitTunnelTest --no-restore
```

Expected: all split tunnel tests pass.

- [ ] **Step 10: Commit integration**

```powershell
git add src/ProtonVPN.Service/SplitTunneling/ExcludedIpRouteManager.cs src/ProtonVPN.Service/SplitTunneling/SplitTunnel.cs src/Tests/ProtonVPN.Service.Tests/SplitTunneling/SplitTunnelTest.cs
git commit -m "Integrate domain split tunnel DNS exclusions"
```

## Task 7: Service Build and GitHub Artifact

**Files:**
- No source changes unless fixing compile/test failures.

- [ ] **Step 1: Run local service tests if SDK is available**

Run:

```powershell
dotnet test src\Tests\ProtonVPN.Service.Tests\ProtonVPN.Service.Tests.csproj --filter FullyQualifiedName~SplitTunneling --no-restore
```

Expected: all matching tests pass. If local SDK `8.0.100` is unavailable, document that local test execution is blocked and rely on GitHub Actions build validation.

- [ ] **Step 2: Build the service locally if SDK is available**

Run:

```powershell
dotnet build src\ProtonVPN.Service\ProtonVPN.Service.csproj --configuration Release --runtime win-x64 --no-restore -p:Platform=x64
```

Expected: service build succeeds. If local SDK is unavailable, skip to GitHub workflow validation.

- [ ] **Step 3: Push branch**

Run:

```powershell
git status --short --branch
git push origin app-natpmp-mapping-phase1
```

Expected: push succeeds without force-push.

- [ ] **Step 4: Trigger GitHub service patch build**

Run:

```powershell
gh workflow run windows-client-fast-patch.yml --ref app-natpmp-mapping-phase1 -f build_mode=service -f upload_full_bin=false -f target_version=
```

Expected: command prints a GitHub Actions run URL.

- [ ] **Step 5: Watch GitHub build**

Run:

```powershell
gh run watch <run-id> --repo marcmy/ProtonVPN-win-app --exit-status
```

Expected: workflow conclusion is success. Artifact `protonvpn-client-patch-service` is uploaded.

## Task 8: Manual Validation

**Files:**
- No source changes unless live validation exposes a bug.

- [ ] **Step 1: Install the service patch artifact**

Use the existing install-safe service patch flow for this branch. Do not overwrite the official Proton folder and do not modify drivers.

- [ ] **Step 2: Configure browser system DNS**

Disable browser DNS-over-HTTPS/private DNS for the browser being used for validation. Confirm Windows DNS cache sees the target domain after browsing:

```powershell
Get-DnsClientCache | Where-Object { $_.Entry -like '*example.com*' } | Select-Object Entry,Data,Type,TimeToLive
```

Expected: one or more A records appear for the domain or subdomain being tested.

- [ ] **Step 3: Add a domain rule**

In split tunneling standard exclude mode, add a domain that blocks VPN traffic, for example:

```text
example.com
```

Expected: `example.com` should cover `example.com` and all subdomains.

- [ ] **Step 4: Test HTTPS bypass**

Navigate to the target HTTPS site in the browser.

Expected:

- target site behaves as if outside the VPN,
- unrelated browser traffic remains protected by the VPN,
- no full reconnect is required for a DNS-derived IP refresh.

- [ ] **Step 5: Test expiry**

Wait longer than the DNS TTL plus grace period, or remove the rule.

Expected: temporary domain-derived route/filter entries are removed and do not remain as persisted settings.

## Self-Review

Spec coverage:

- System DNS requirement: covered by DNS cache reader and manual validation.
- `example.com` matching subdomains: covered by `DomainRule`.
- `*.example.com` alias: covered by `DomainRule`.
- Temporary TTL plus grace: covered by `DomainResolvedAddressTracker`.
- No HTTPS inspection: maintained by DNS cache polling only.
- No broad subnet exclusions: implementation adds only observed IPv4 addresses.
- Existing IP/CIDR behavior: preserved by `GetConfiguredRemoteAddresses`.
- Cleanup on disconnect/settings change: covered by `ClearDomainSplitTunnelState` and tests.

Known first-version limitation:

- Browser/app private DNS will not work because it bypasses the Windows DNS client cache.
- IPv6 DNS answers are ignored until IPv6 route exclusion support is separately designed.
