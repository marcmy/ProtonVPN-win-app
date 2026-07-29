#nullable enable

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.OperatingSystems.Network.Contracts;
using ProtonVPN.OperatingSystems.Network.Contracts.Routing;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using ProtonVPN.Service.Firewall;
using ProtonVPN.Service.ServerHealth;
using ProtonVPN.Service.Settings;

namespace ProtonVPN.Service.Tests.ServerHealth;

[TestClass]
public class ServerHealthProbeServiceTest
{
    private IConfiguration _configuration = null!;
    private IServiceSettings _serviceSettings = null!;
    private ISystemNetworkInterfaces _networkInterfaces = null!;
    private IRoutingTableHelper _routingTableHelper = null!;
    private IServerHealthPermitManager _permitManager = null!;
    private IServerHealthPermitLease _permitLease = null!;
    private IServerHealthPingProbe _pingProbe = null!;
    private IIpv6 _ipv6 = null!;
    private INetworkInterface _physicalInterface = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _configuration = Substitute.For<IConfiguration>();
        _configuration.GetHardwareId(Arg.Any<VpnProtocol>(), Arg.Any<OpenVpnAdapter>())
            .Returns("vpn-adapter");
        _serviceSettings = Substitute.For<IServiceSettings>();
        _networkInterfaces = Substitute.For<ISystemNetworkInterfaces>();
        _routingTableHelper = Substitute.For<IRoutingTableHelper>();
        _permitManager = Substitute.For<IServerHealthPermitManager>();
        _permitLease = Substitute.For<IServerHealthPermitLease>();
        _permitManager.TryCreate(Arg.Any<IPAddress>()).Returns(_permitLease);
        _pingProbe = Substitute.For<IServerHealthPingProbe>();
        _pingProbe.MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _ipv6 = Substitute.For<IIpv6>();
        _ipv6.VpnProtocol.Returns(VpnProtocol.WireGuardUdp);
        _physicalInterface = Substitute.For<INetworkInterface>();
        _physicalInterface.Index.Returns((uint)12);
        _physicalInterface.DefaultGateway.Returns(IPAddress.Parse("192.0.2.1"));
        _networkInterfaces.GetBestInterfaceExcludingHardwareId("vpn-adapter")
            .Returns(_physicalInterface);
    }

    [TestMethod]
    public async Task ProbeAsync_WhenRouteIsCreated_AlwaysDeletesRouteAndPermit()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(false, true);
        ServerHealthProbeService service = CreateService();

        ServerHealthProbeResultIpcEntity result =
            await service.ProbeAsync("203.0.113.10", CancellationToken.None);

        Assert.IsTrue(result.UsedPhysicalRoute);
        _routingTableHelper.Received(1).CreateRoute(Arg.Any<RouteConfiguration>());
        _routingTableHelper.Received(1).DeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
        Assert.AreEqual(0, service.ActiveAddressLockCount);
    }

    [TestMethod]
    public async Task ProbeAsync_WhenRouteAlreadyExists_DoesNotDeleteUnownedRoute()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(true);
        ServerHealthProbeService service = CreateService();

        await service.ProbeAsync("203.0.113.10", CancellationToken.None);

        _routingTableHelper.DidNotReceive().CreateRoute(Arg.Any<RouteConfiguration>());
        _routingTableHelper.DidNotReceive().DeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
    }

    [TestMethod]
    public async Task ProbeAsync_WhenRouteCreationThrowsAfterCreatingRoute_CleansUpOwnedRoute()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(false, true);
        _routingTableHelper
            .When(helper => helper.CreateRoute(Arg.Any<RouteConfiguration>()))
            .Do(_ => throw new InvalidOperationException("simulated route creation failure"));
        ServerHealthProbeService service = CreateService();

        ServerHealthProbeResultIpcEntity result =
            await service.ProbeAsync("203.0.113.10", CancellationToken.None);

        Assert.IsFalse(result.UsedPhysicalRoute);
        _routingTableHelper.Received(1).DeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
        Assert.AreEqual(0, service.ActiveAddressLockCount);
    }

    [TestMethod]
    public async Task ProbeAsync_WhenCancelledDuringMeasurement_CleansUpRoutePermitAndAddressLock()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(false, true);
        TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pingProbe.MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
                return SuccessResult();
            });
        ServerHealthProbeService service = CreateService();
        using CancellationTokenSource cancellation = new();

        Task<ServerHealthProbeResultIpcEntity> pending =
            service.ProbeAsync("203.0.113.10", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => pending);
        _routingTableHelper.Received(1).DeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
        Assert.AreEqual(0, service.ActiveAddressLockCount);
    }

    [TestMethod]
    public async Task ProbeAsync_ForSameAddress_SerializesWorkAndPrunesAddressLock()
    {
        int routeExistsCall = 0;
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>())
            .Returns(_ => Interlocked.Increment(ref routeExistsCall) % 2 == 0);
        TaskCompletionSource firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int pingCalls = 0;
        _pingProbe.MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                int callNumber = Interlocked.Increment(ref pingCalls);
                if (callNumber == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }

                return SuccessResult();
            });
        ServerHealthProbeService service = CreateService();

        Task<ServerHealthProbeResultIpcEntity> first =
            service.ProbeAsync("203.0.113.10", CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ServerHealthProbeResultIpcEntity> second =
            service.ProbeAsync("203.0.113.10", CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, Volatile.Read(ref pingCalls));
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.AreEqual(2, pingCalls);
        Assert.AreEqual(0, service.ActiveAddressLockCount);
    }

    [TestMethod]
    public async Task ProbeAsync_WhenAddressIsInvalid_DoesNotCreateRouteOrPermit()
    {
        ServerHealthProbeService service = CreateService();

        ServerHealthProbeResultIpcEntity result =
            await service.ProbeAsync("not-an-ip", CancellationToken.None);

        Assert.IsFalse(result.UsedPhysicalRoute);
        _permitManager.DidNotReceive().TryCreate(Arg.Any<IPAddress>());
        _routingTableHelper.DidNotReceive().CreateRoute(Arg.Any<RouteConfiguration>());
    }

    private ServerHealthProbeService CreateService()
    {
        return new(
            _configuration,
            _serviceSettings,
            _networkInterfaces,
            _routingTableHelper,
            _permitManager,
            _pingProbe,
            _ipv6);
    }

    private static ServerHealthProbeResultIpcEntity SuccessResult()
    {
        return new()
        {
            AverageLatencyMilliseconds = 25,
            PacketLossPercent = 0,
            SuccessfulSamples = 4,
            TotalSamples = 4,
            CheckedAtUtc = DateTime.UtcNow,
            UsedPhysicalRoute = true,
        };
    }
}
