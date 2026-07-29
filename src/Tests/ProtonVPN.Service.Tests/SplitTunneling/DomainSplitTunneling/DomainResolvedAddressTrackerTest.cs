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
    public void AddOrRefresh_ShouldApplyMinimumTtlAndGrace()
    {
        DateTime now = new(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
        DomainResolvedAddressTracker tracker = new(() => now);

        tracker.AddOrRefresh("example.com", IPAddress.Parse("203.0.113.10"), 1);

        tracker.GetActive().Single().ExpiresAtUtc.Should().Be(now.AddMinutes(6));
    }

    [TestMethod]
    public void AddOrRefresh_ShouldClampTtlAndGraceToOneHour()
    {
        DateTime now = new(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
        DomainResolvedAddressTracker tracker = new(() => now);

        tracker.AddOrRefresh("example.com", IPAddress.Parse("203.0.113.10"), 86400);

        tracker.GetActive().Single().ExpiresAtUtc.Should().Be(now.AddHours(1));
    }

    [TestMethod]
    public void Expiry_ShouldKeepSharedIpUntilEveryOwnerExpires()
    {
        DateTime now = new(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
        DomainResolvedAddressTracker tracker = new(() => now);
        IPAddress ipAddress = IPAddress.Parse("203.0.113.10");

        tracker.AddOrRefresh("example.com", ipAddress, 60);
        now = now.AddMinutes(2);
        tracker.AddOrRefresh("other.com", ipAddress, 60);
        now = now.AddMinutes(5);

        tracker.GetActiveIpv4Addresses().Should().ContainSingle("203.0.113.10");
        now = now.AddMinutes(2);
        tracker.GetActiveIpv4Addresses().Should().BeEmpty();
    }

    [TestMethod]
    public void RetainOwners_ShouldRemoveOnlyEntriesOwnedByRemovedRules()
    {
        DateTime now = new(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
        DomainResolvedAddressTracker tracker = new(() => now);
        tracker.AddOrRefresh("example.com", IPAddress.Parse("203.0.113.10"), 60);
        tracker.AddOrRefresh("other.com", IPAddress.Parse("203.0.113.11"), 60);

        tracker.RetainOwners(["example.com"]);

        tracker.GetActive().Should().ContainSingle()
            .Which.OwnerDomain.Should().Be("example.com");
    }
}
