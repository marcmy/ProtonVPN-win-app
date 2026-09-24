#nullable enable

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
    public void TestInitialize()
    {
        _dnsCacheReader = Substitute.For<ISystemDnsCacheReader>();
        _dnsCacheReader.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SystemDnsCacheEntry>());
        _poller = new(_dnsCacheReader);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _poller.Dispose();
    }

    [TestMethod]
    public async Task PollOnceAsync_ShouldPublishOnlySuffixBoundaryMatches()
    {
        _dnsCacheReader.ReadAsync(Arg.Any<CancellationToken>()).Returns([
            new SystemDnsCacheEntry("login.example.com", IPAddress.Parse("203.0.113.10"), 60),
            new SystemDnsCacheEntry("badexample.com", IPAddress.Parse("203.0.113.11"), 60),
            new SystemDnsCacheEntry("unrelated.test", IPAddress.Parse("203.0.113.12"), 60),
        ]);
        string[]? latest = null;
        _poller.AddressesChanged += (_, addresses) => latest = addresses;
        _poller.ReplaceRules(["example.com"]);

        await _poller.PollOnceAsync(CancellationToken.None);

        latest.Should().BeEquivalentTo("203.0.113.10");
    }

    [TestMethod]
    public async Task PollOnceAsync_ShouldTrackAllMatchingOwnersAndIgnoreIpv6()
    {
        _dnsCacheReader.ReadAsync(Arg.Any<CancellationToken>()).Returns([
            new SystemDnsCacheEntry("login.example.com", IPAddress.Parse("203.0.113.10"), 60),
            new SystemDnsCacheEntry("login.example.com", IPAddress.Parse("2001:db8::1"), 60),
        ]);
        string[]? latest = null;
        _poller.AddressesChanged += (_, addresses) => latest = addresses;
        _poller.ReplaceRules(["example.com", "login.example.com"]);

        await _poller.PollOnceAsync(CancellationToken.None);
        _poller.ReplaceRules(["login.example.com"]);

        latest.Should().BeEquivalentTo("203.0.113.10");
    }

    [TestMethod]
    public async Task Stop_ShouldClearPublishedAddresses()
    {
        _dnsCacheReader.ReadAsync(Arg.Any<CancellationToken>()).Returns([
            new SystemDnsCacheEntry("example.com", IPAddress.Parse("203.0.113.10"), 60),
        ]);
        _poller.ReplaceRules(["*.example.com"]);
        await _poller.PollOnceAsync(CancellationToken.None);
        string[]? latest = null;
        _poller.AddressesChanged += (_, addresses) => latest = addresses;

        _poller.Stop();

        latest.Should().BeEmpty();
    }

    [TestMethod]
    public async Task PollOnceAsync_WhenSubscriberFails_ShouldRetrySameAddresses()
    {
        _dnsCacheReader.ReadAsync(Arg.Any<CancellationToken>()).Returns([
            new SystemDnsCacheEntry("example.com", IPAddress.Parse("203.0.113.10"), 60),
        ]);
        int invocations = 0;
        _poller.AddressesChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref invocations) == 1)
            {
                throw new InvalidOperationException("simulated apply failure");
            }
        };
        _poller.ReplaceRules(["example.com"]);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _poller.PollOnceAsync(CancellationToken.None));
        await _poller.PollOnceAsync(CancellationToken.None);

        invocations.Should().Be(2);
    }
}
#nullable enable
