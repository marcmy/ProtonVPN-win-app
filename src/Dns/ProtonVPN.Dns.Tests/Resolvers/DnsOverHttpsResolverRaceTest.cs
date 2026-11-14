/*
 * Copyright (c) 2026 Proton AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ARSoft.Tools.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using CoreTaskExtensions = ProtonVPN.Common.Core.Extensions.TaskExtensions;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Dns.Contracts;
using ProtonVPN.Dns.Resolvers;
using ProtonVPN.Dns.Tests.Mocks;
using IHttpClientFactory = ProtonVPN.Dns.HttpClients.IHttpClientFactory;

namespace ProtonVPN.Dns.Tests.Resolvers;

// Captures the process-wide fire-and-forget handler, so it must not run alongside other tests.
[TestClass]
[DoNotParallelize]
public class DnsOverHttpsResolverRaceTest
{
    private const string HOST = "api.protonvpn.ch";
    private const string DNS_RESPONSE = "AACBgAABAAEAAAAAA2FwaQlwcm90b252cG4CY2gAAAEAAcAMAAEAAQAAAfAABLmfn6o=";

    private const string FAST_IP_ADDRESS = "192.168.1.1";
    private const string SLOW_IP_ADDRESS_1 = "192.168.2.2";
    private const string SLOW_IP_ADDRESS_2 = "192.168.3.3";
    private const string SECOND_PROVIDER_IP_ADDRESS = "192.168.4.4";

    private ConcurrentBag<Exception> _unhandledExceptions;
    private ScriptedHttpMessageHandler _httpMessageHandler;
    private DnsOverHttpsResolver _resolver;

    [TestInitialize]
    public void TestInitialize()
    {
        _unhandledExceptions = new ConcurrentBag<Exception>();
        CoreTaskExtensions.SetDefaultExceptionHandler(_unhandledExceptions.Add);

        _httpMessageHandler = new ScriptedHttpMessageHandler();
        IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.Create().Returns(_ => new HttpClient(_httpMessageHandler));

        IConfiguration configuration = Substitute.For<IConfiguration>();
        configuration.DohClientTimeout.Returns(TimeSpan.FromSeconds(10));
        configuration.DnsOverHttpsPerProviderTimeout.Returns(TimeSpan.FromSeconds(20));
        configuration.DnsResolveTimeout.Returns(TimeSpan.FromSeconds(30));
        configuration.DefaultDnsTimeToLive.Returns(TimeSpan.FromMinutes(20));
        configuration.DohProviders.Returns(new List<string>
        {
            "https://dns.google/dns-query",
            "https://dns11.quad9.net/dns-query",
        });

        IDnsOverHttpsProvidersManager providersManager = Substitute.For<IDnsOverHttpsProvidersManager>();
        providersManager.GetAsync("dns.google", Arg.Any<CancellationToken>()).Returns(new List<IpAddress>
        {
            new(IPAddress.Parse(FAST_IP_ADDRESS)),
            new(IPAddress.Parse(SLOW_IP_ADDRESS_1)),
            new(IPAddress.Parse(SLOW_IP_ADDRESS_2)),
        });
        providersManager.GetAsync("dns11.quad9.net", Arg.Any<CancellationToken>()).Returns(new List<IpAddress>
        {
            new(IPAddress.Parse(SECOND_PROVIDER_IP_ADDRESS)),
        });

        _resolver = new DnsOverHttpsResolver(configuration, new MockOfLogger(), httpClientFactory, providersManager);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        CoreTaskExtensions.SetDefaultExceptionHandler(_ => { });
    }

    [TestMethod]
    public async Task ResolveAsync_WhenFirstProviderAddressIsUnreachable_ShouldUseRemainingAddresses()
    {
        _httpMessageHandler.RespondUnreachable(FAST_IP_ADDRESS, TimeSpan.Zero);
        _httpMessageHandler.RespondSuccessfully(SLOW_IP_ADDRESS_1, TimeSpan.FromMilliseconds(200));
        _httpMessageHandler.RespondSuccessfully(SLOW_IP_ADDRESS_2, TimeSpan.FromMilliseconds(200));
        _httpMessageHandler.RespondUnreachable(SECOND_PROVIDER_IP_ADDRESS, TimeSpan.Zero);

        DnsResponse response = await _resolver.ResolveAsync(HOST, CancellationToken.None);

        Assert.IsNotNull(response, "One unreachable DoH address must not abandon the other addresses.");
        Assert.IsNotEmpty(response.IpAddresses);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenProviderAddressesAreUnreachable_ShouldNotReportThemAsUnhandled()
    {
        // Matches the kill switch blocking DoH while connecting: every address of the first provider fails,
        // the first one immediately, while another provider is still racing.
        _httpMessageHandler.RespondUnreachable(FAST_IP_ADDRESS, TimeSpan.Zero);
        _httpMessageHandler.RespondUnreachable(SLOW_IP_ADDRESS_1, TimeSpan.FromMilliseconds(200));
        _httpMessageHandler.RespondUnreachable(SLOW_IP_ADDRESS_2, TimeSpan.FromMilliseconds(200));
        _httpMessageHandler.RespondSuccessfully(SECOND_PROVIDER_IP_ADDRESS, TimeSpan.FromMilliseconds(500));

        DnsResponse response = await _resolver.ResolveAsync(HOST, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.IsNotNull(response);
        Assert.IsEmpty(_unhandledExceptions,
            "Expected DoH connection failures must not reach the global unhandled-exception handler.");
    }

    [TestMethod]
    public async Task ResolveAsync_WhenAnAddressWins_ShouldCancelLosingRequestsWithoutReportingThemAsUnhandled()
    {
        _httpMessageHandler.RespondSuccessfully(FAST_IP_ADDRESS, TimeSpan.Zero);
        _httpMessageHandler.RespondUnreachable(SLOW_IP_ADDRESS_1, TimeSpan.FromMilliseconds(200));
        _httpMessageHandler.RespondUnreachable(SLOW_IP_ADDRESS_2, TimeSpan.FromMilliseconds(200));
        _httpMessageHandler.RespondUnreachable(SECOND_PROVIDER_IP_ADDRESS, TimeSpan.FromMilliseconds(200));

        DnsResponse response = await _resolver.ResolveAsync(HOST, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        Assert.IsNotNull(response);
        Assert.AreEqual(3, _httpMessageHandler.CancelledRequestCount,
            "Requests that lost the race must be cancelled instead of running to completion.");
        Assert.IsEmpty(_unhandledExceptions,
            "Requests that lost the race must not reach the global unhandled-exception handler.");
    }

    // Behaves like a real socket: honours cancellation and faults the request when the host is unreachable.
    private class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();
        private int _cancelledRequestCount;

        public int CancelledRequestCount => _cancelledRequestCount;

        public void RespondSuccessfully(string ipAddress, TimeSpan delay)
        {
            _responses[ipAddress] = async cancellationToken =>
            {
                await DelayAsync(delay, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(DNS_RESPONSE.FromBase64String())
                };
            };
        }

        public void RespondUnreachable(string ipAddress, TimeSpan delay)
        {
            _responses[ipAddress] = async cancellationToken =>
            {
                await DelayAsync(delay, cancellationToken);
                throw new HttpRequestException($"A socket operation was attempted to an unreachable host. ({ipAddress}:443)",
                    new SocketException((int)SocketError.HostUnreachable));
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _responses[request.RequestUri!.Host](cancellationToken);
        }

        private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelledRequestCount);
                throw;
            }
        }
    }
}
