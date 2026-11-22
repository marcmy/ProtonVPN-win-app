#nullable enable

using System;
using System.Linq;
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
    private const string PHYSICAL_ROUTE = "physical-route";

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
    public async Task ProbeAsync_WhenRouteIsCreated_AlwaysDeletesOwnedRouteAndPermit()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(false, true);
        _routingTableHelper.TryCreateRoute(Arg.Any<RouteConfiguration>()).Returns(true);
        ServerHealthProbeService service = CreateService();

        ServerHealthProbeResultIpcEntity result =
            await service.ProbeAsync("203.0.113.10", CancellationToken.None);

        Assert.IsTrue(result.UsedPhysicalRoute);
        Assert.AreEqual(PHYSICAL_ROUTE, result.RouteContext);
        _routingTableHelper.Received(1).TryCreateRoute(Arg.Any<RouteConfiguration>());
        _routingTableHelper.Received(1).TryDeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
        Assert.AreEqual(0, service.ActiveAddressLockCount);
    }

    [TestMethod]
    public async Task ProbeAsync_WhenRouteAlreadyExists_DoesNotDeleteUnownedRoute()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(true);
        ServerHealthProbeService service = CreateService();

        await service.ProbeAsync("203.0.113.10", CancellationToken.None);

        _routingTableHelper.DidNotReceive().TryCreateRoute(Arg.Any<RouteConfiguration>());
        _routingTableHelper.DidNotReceive().TryDeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
    }

    [TestMethod]
    public async Task ProbeAsync_WhenAnotherActorCreatesRouteDuringCreateRace_DoesNotDeleteUnownedRoute()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(false, true);
        _routingTableHelper.TryCreateRoute(Arg.Any<RouteConfiguration>()).Returns(false);
        ServerHealthProbeService service = CreateService();

        ServerHealthProbeResultIpcEntity result =
            await service.ProbeAsync("203.0.113.10", CancellationToken.None);

        Assert.IsTrue(result.UsedPhysicalRoute);
        _routingTableHelper.Received(1).TryCreateRoute(Arg.Any<RouteConfiguration>());
        _routingTableHelper.DidNotReceive().TryDeleteRoute(Arg.Any<RouteConfiguration>());
        _pingProbe.Received(1).MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>());
        _permitLease.Received(1).Dispose();
    }

    [TestMethod]
    public async Task ProbeAsync_WhenRouteCreationFailsAndRouteDoesNotExist_DoesNotPingOrDelete()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(false);
        _routingTableHelper.TryCreateRoute(Arg.Any<RouteConfiguration>()).Returns(false);
        ServerHealthProbeService service = CreateService();

        ServerHealthProbeResultIpcEntity result =
            await service.ProbeAsync("203.0.113.10", CancellationToken.None);

        Assert.IsFalse(result.UsedPhysicalRoute);
        _routingTableHelper.DidNotReceive().TryDeleteRoute(Arg.Any<RouteConfiguration>());
        _pingProbe.DidNotReceive().MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>());
        _permitLease.Received(1).Dispose();
    }

    [TestMethod]
    public async Task ProbeAsync_WhenCancelledDuringMeasurement_CleansUpOwnedRoutePermitAndAddressLock()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(false, true);
        _routingTableHelper.TryCreateRoute(Arg.Any<RouteConfiguration>()).Returns(true);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pingProbe.MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.SetResult();
                await settled.Task;
                call.ArgAt<CancellationToken>(1).ThrowIfCancellationRequested();
                return SuccessResult();
            });
        int routeLookups = 0;
        ServerHealthProbeService service = CreateService(_ =>
        {
            Interlocked.Increment(ref routeLookups);
            return PHYSICAL_ROUTE;
        });
        using CancellationTokenSource cancellation = new();

        Task<ServerHealthProbeResultIpcEntity> pending =
            service.ProbeAsync("203.0.113.10", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            Assert.IsFalse(pending.IsCompleted);
            _routingTableHelper.DidNotReceive().TryDeleteRoute(Arg.Any<RouteConfiguration>());
            _permitLease.DidNotReceive().Dispose();
        }
        finally
        {
            settled.SetResult();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual(1, routeLookups);
        _routingTableHelper.Received(1).TryDeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
        Assert.AreEqual(0, service.ActiveAddressLockCount);
    }

    [TestMethod]
    public async Task ProbeAsync_WhenMeasurementThrows_CleansUpOwnedRouteAndPermit()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(false, true);
        _routingTableHelper.TryCreateRoute(Arg.Any<RouteConfiguration>()).Returns(true);
        _pingProbe.MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>())
            .Returns<Task<ServerHealthProbeResultIpcEntity>>(_ => throw new InvalidOperationException("simulated ping failure"));
        ServerHealthProbeService service = CreateService();

        ServerHealthProbeResultIpcEntity result =
            await service.ProbeAsync("203.0.113.10", CancellationToken.None);

        Assert.IsFalse(result.UsedPhysicalRoute);
        _routingTableHelper.Received(1).TryDeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
    }

    [TestMethod]
    public async Task ProbeAsync_ForSameAddress_SerializesWorkAndPrunesAddressLock()
    {
        int routeExistsCall = 0;
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>())
            .Returns(_ => Interlocked.Increment(ref routeExistsCall) % 2 == 0);
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        Assert.AreEqual(0, result.Samples!.Length);
        _permitManager.DidNotReceive().TryCreate(Arg.Any<IPAddress>());
        _routingTableHelper.DidNotReceive().TryCreateRoute(Arg.Any<RouteConfiguration>());
    }

    [TestMethod]
    public async Task ProbeAsync_WhenAddressIsTheUnspecifiedDefaultRoute_DoesNotCreateRouteOrPermit()
    {
        ServerHealthProbeService service = CreateService();

        ServerHealthProbeResultIpcEntity result = await service.ProbeAsync("0.0.0.0", CancellationToken.None);

        Assert.IsFalse(result.UsedPhysicalRoute);
        _permitManager.DidNotReceive().TryCreate(Arg.Any<IPAddress>());
        _routingTableHelper.DidNotReceive().TryCreateRoute(Arg.Any<RouteConfiguration>());
    }

    [TestMethod]
    public async Task ProbeAsync_WhenWindowsSelectsADifferentRoute_SkipsTheEchoes()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(false, true);
        _routingTableHelper.TryCreateRoute(Arg.Any<RouteConfiguration>()).Returns(true);
        ServerHealthProbeService service = CreateService(_ => null);

        ServerHealthProbeResultIpcEntity result = await service.ProbeAsync("203.0.113.10", CancellationToken.None);

        Assert.IsFalse(result.UsedPhysicalRoute);
        Assert.IsNull(result.RouteContext);
        Assert.AreEqual(0, result.Samples!.Length);
        _pingProbe.DidNotReceive().MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>());
        _routingTableHelper.Received(1).TryDeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
    }

    [TestMethod]
    public async Task ProbeAsync_WhenVerifiedSourceChanges_DiscardsTheMeasurement()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(true);
        int lookups = 0;
        ServerHealthProbeService service = CreateService(_ => ++lookups == 1 ? PHYSICAL_ROUTE : "changed-source");

        ServerHealthProbeResultIpcEntity result = await service.ProbeAsync("203.0.113.10", CancellationToken.None);

        Assert.IsFalse(result.UsedPhysicalRoute);
        Assert.IsNull(result.AverageLatencyMilliseconds);
        Assert.IsNull(result.RouteContext);
        Assert.AreEqual(0, result.Samples!.Length);
        _routingTableHelper.DidNotReceive().TryDeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
    }

    [TestMethod]
    public async Task QuickSampling_IsForwardedWithoutChangingRouteAndPermitOwnership()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(true);
        _pingProbe.MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>(), true).Returns(SuccessResult());
        ServerHealthProbeService service = CreateService();
        await service.ProbeAsync("203.0.113.10", CancellationToken.None, quickFirstResponse: true);
        _pingProbe.Received(1).MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>(), true);
        _routingTableHelper.DidNotReceive().TryDeleteRoute(Arg.Any<RouteConfiguration>());
        _permitLease.Received(1).Dispose();
        Assert.AreEqual(0, service.ActiveAddressLockCount);
    }

    [TestMethod]
    public async Task ParallelQuickChecks_AreBoundedToThirtyTwoAndReleaseAllAddressLocks()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(true);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        _pingProbe.MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>(), true).Returns(async _ =>
        {
            if (Interlocked.Increment(ref entered) == 32) { started.SetResult(); }
            await release.Task;
            return SuccessResult();
        });
        ServerHealthProbeService service = CreateService();
        Task[] probes = Enumerable.Range(1, 40)
            .Select(index => service.ProbeAsync($"203.0.113.{index}", CancellationToken.None, true)).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(32, Volatile.Read(ref entered));
        release.SetResult();
        await Task.WhenAll(probes);
        Assert.AreEqual(40, entered);
        Assert.AreEqual(0, service.ActiveAddressLockCount);
        _permitLease.Received(40).Dispose();
    }

    [TestMethod]
    public async Task FixedChecks_ReserveEchoCapacityForQuickChecks()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(true);
        TaskCompletionSource fixedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource quickStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int fixedChecks = 0;
        int quickChecks = 0;
        _pingProbe.MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (Interlocked.Increment(ref fixedChecks) == 4)
            {
                fixedStarted.SetResult();
            }

            await release.Task;
            return SuccessResult();
        });
        _pingProbe.MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>(), true).Returns(async _ =>
        {
            if (Interlocked.Increment(ref quickChecks) == 16)
            {
                quickStarted.SetResult();
            }

            await release.Task;
            return SuccessResult();
        });

        ServerHealthProbeService service = CreateService();
        Task[] fixedProbes = Enumerable.Range(1, 10)
            .Select(index => service.ProbeAsync($"203.0.113.{index}", CancellationToken.None)).ToArray();
        Task[] quickProbes = [];
        try
        {
            await fixedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            quickProbes = Enumerable.Range(1, 16)
                .Select(index => service.ProbeAsync($"198.51.100.{index}", CancellationToken.None, true)).ToArray();
            await quickStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(4, Volatile.Read(ref fixedChecks));
            _permitManager.Received(20).TryCreate(Arg.Any<IPAddress>());
        }
        finally
        {
            release.SetResult();
            await Task.WhenAll(fixedProbes.Concat(quickProbes));
        }

        Assert.AreEqual(10, fixedChecks);
        Assert.AreEqual(0, service.ActiveAddressLockCount);
    }

    [TestMethod]
    public async Task CancelledFixedReservation_ReleasesPartiallyReservedEchoSlots()
    {
        _routingTableHelper.RouteExists(Arg.Any<RouteConfiguration>()).Returns(true);
        TaskCompletionSource initialQuickChecksStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allQuickChecksStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        _pingProbe.MeasureAsync(Arg.Any<IPAddress>(), Arg.Any<CancellationToken>(), true).Returns(async _ =>
        {
            int count = Interlocked.Increment(ref started);
            if (count == 30)
            {
                initialQuickChecksStarted.SetResult();
            }
            else if (count == 32)
            {
                allQuickChecksStarted.SetResult();
            }

            await release.Task;
            return SuccessResult();
        });

        ServerHealthProbeService service = CreateService();
        Task[] initial = Enumerable.Range(1, 30)
            .Select(index => service.ProbeAsync($"203.0.113.{index}", CancellationToken.None, true)).ToArray();
        Task[] final = [];
        try
        {
            await initialQuickChecksStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using CancellationTokenSource cancellation = new();
            Task fixedCheck = service.ProbeAsync("198.51.100.1", cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => fixedCheck);
            _permitManager.DidNotReceive().TryCreate(IPAddress.Parse("198.51.100.1"));

            final = Enumerable.Range(31, 2)
                .Select(index => service.ProbeAsync($"203.0.113.{index}", CancellationToken.None, true)).ToArray();
            await allQuickChecksStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.SetResult();
            await Task.WhenAll(initial.Concat(final));
        }

        Assert.AreEqual(0, service.ActiveAddressLockCount);
    }

    private ServerHealthProbeService CreateService(Func<RouteConfiguration, string?>? getRouteContext = null)
    {
        return new(
            _configuration,
            _serviceSettings,
            _networkInterfaces,
            _routingTableHelper,
            _permitManager,
            _pingProbe,
            _ipv6,
            getRouteContext ?? (_ => PHYSICAL_ROUTE));
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
