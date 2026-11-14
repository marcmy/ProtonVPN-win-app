#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy;
using ProtonVPN.Common.Legacy.PortForwarding;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;
using ProtonVPN.Service.PortMapping;
using ProtonVPN.Service.Settings;
using ProtonVPN.Vpn.PortMapping;

namespace ProtonVPN.Service.Tests.PortMapping;

[TestClass]
public class PortForwardingForAppsRouteShimTest
{
    private ILogger _logger = null!;
    private IServiceSettings _serviceSettings = null!;
    private IPortMappingProtocolClient _portMappingProtocolClient = null!;
    private IPortForwardingRouteOperations _routeOperations = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _logger = Substitute.For<ILogger>();
        _serviceSettings = Substitute.For<IServiceSettings>();
        _serviceSettings.IsPortForwardingForAppsEnabled.Returns(true);
        _portMappingProtocolClient = Substitute.For<IPortMappingProtocolClient>();
        _routeOperations = Substitute.For<IPortForwardingRouteOperations>();
        _routeOperations.GetInterfaceIndexForLocalIp("10.2.0.2").Returns(42);
        _routeOperations.RouteExists(42).Returns(true);
    }

    [TestMethod]
    public void PortForwardingVpn_WhenConnected_AddsAndThenRemovesRoute()
    {
        using PortForwardingForAppsRouteShim shim = CreateShim(_routeOperations);
        shim.SetVpnState(ConnectedState());

        _routeOperations.Received(1).AddRoute(42);

        shim.SetVpnState(VpnState.Default);

        _routeOperations.Received(2).DeleteRoute(42);
    }

    [TestMethod]
    public async Task DisconnectDuringRouteAdd_LeavesNoTrackedOrInstalledRoute()
    {
        BlockingRouteOperations routeOperations = new();
        using PortForwardingForAppsRouteShim shim = CreateShim(routeOperations);

        Task addTask = Task.Run(() => shim.SetVpnState(ConnectedState()));
        Assert.IsTrue(routeOperations.AddStarted.Wait(TimeSpan.FromSeconds(2)));

        Task disconnectTask = Task.Run(() => shim.SetVpnState(VpnState.Default));
        Assert.IsTrue(SpinWait.SpinUntil(
            () => disconnectTask.Status != TaskStatus.WaitingForActivation,
            TimeSpan.FromSeconds(1)));
        routeOperations.AllowAddToFinish.Set();
        await Task.WhenAll(addTask, disconnectTask);

        Assert.AreEqual(1, routeOperations.AddCount);
        Assert.IsTrue(routeOperations.Operations.Last() == "delete:42");
    }

    [TestMethod]
    public void RouteAddFailure_AttemptsCleanup()
    {
        _routeOperations
            .When(operations => operations.AddRoute(42))
            .Do(_ => throw new InvalidOperationException("simulated add failure"));
        using PortForwardingForAppsRouteShim shim = CreateShim(_routeOperations);

        shim.SetVpnState(ConnectedState());

        _routeOperations.Received(1).AddRoute(42);
        _routeOperations.Received(2).DeleteRoute(42);
    }

    [TestMethod]
    public void PortMappingRenewal_KeepsRouteInstalled()
    {
        using PortForwardingForAppsRouteShim shim = CreateShim(_routeOperations);
        shim.SetVpnState(ConnectedState());

        _routeOperations.ClearReceivedCalls();

        RaisePortForwardingState(RefreshingMapping());

        _routeOperations.Received(1).RouteExists(42);
        _routeOperations.DidNotReceive().AddRoute(Arg.Any<int>());
        _routeOperations.DidNotReceive().DeleteRoute(Arg.Any<int>());
    }

    [TestMethod]
    public void PortMappingError_KeepsRouteInstalled()
    {
        using PortForwardingForAppsRouteShim shim = CreateShim(_routeOperations);
        shim.SetVpnState(ConnectedState());

        _routeOperations.ClearReceivedCalls();

        RaisePortForwardingState(Mapping(PortMappingStatus.Error));

        _routeOperations.Received(1).RouteExists(42);
        _routeOperations.DidNotReceive().AddRoute(Arg.Any<int>());
        _routeOperations.DidNotReceive().DeleteRoute(Arg.Any<int>());
    }

    [TestMethod]
    public void MissingTrackedRoute_OnStateChange_IsRecreated()
    {
        using PortForwardingForAppsRouteShim shim = CreateShim(_routeOperations);
        shim.SetVpnState(ConnectedState());

        _routeOperations.RouteExists(42).Returns(false);
        _routeOperations
            .When(operations => operations.DeleteRoute(42))
            .Do(_ => throw new InvalidOperationException("route not found"));

        RaisePortForwardingState(RefreshingMapping());

        _routeOperations.Received(2).AddRoute(42);
    }

    [TestMethod]
    public void MissingTrackedRoute_IsRecreatedByPeriodicHealthCheck()
    {
        ReconciledRouteOperations routeOperations = new();
        using PortForwardingForAppsRouteShim shim = CreateShim(
            routeOperations,
            TimeSpan.FromMilliseconds(10));
        shim.SetVpnState(ConnectedState());

        routeOperations.SimulateRouteLoss();

        Assert.IsTrue(routeOperations.RouteRecreated.Wait(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(2, routeOperations.AddCount);
    }

    [TestMethod]
    public void ConnectedVpnWithoutPortForwarding_DoesNotAddRoute()
    {
        using PortForwardingForAppsRouteShim shim = CreateShim(_routeOperations);

        shim.SetVpnState(ConnectedState(portForwarding: false));

        _routeOperations.DidNotReceive().AddRoute(Arg.Any<int>());
    }

    [TestMethod]
    public void DisablingPortForwardingForApps_RemovesRoute()
    {
        using PortForwardingForAppsRouteShim shim = CreateShim(_routeOperations);
        shim.SetVpnState(ConnectedState());

        _routeOperations.ClearReceivedCalls();
        _serviceSettings.IsPortForwardingForAppsEnabled.Returns(false);

        _serviceSettings.SettingsChanged +=
            Raise.Event<EventHandler<MainSettingsIpcEntity>>(
                _serviceSettings,
                new MainSettingsIpcEntity());

        _routeOperations.Received(1).DeleteRoute(42);
    }

    private PortForwardingForAppsRouteShim CreateShim(
        IPortForwardingRouteOperations routeOperations,
        TimeSpan? reconciliationInterval = null)
    {
        return reconciliationInterval is null
            ? new(
                _logger,
                _serviceSettings,
                _portMappingProtocolClient,
                routeOperations)
            : new(
                _logger,
                _serviceSettings,
                _portMappingProtocolClient,
                routeOperations,
                reconciliationInterval.Value);
    }

    private void RaisePortForwardingState(PortForwardingState state)
    {
        _portMappingProtocolClient.StateChanged +=
            Raise.Event<EventHandler<EventArgs<PortForwardingState>>>(
                _portMappingProtocolClient,
                new EventArgs<PortForwardingState>(state));
    }

    private static VpnState ConnectedState(bool portForwarding = true)
    {
        return new(
            VpnStatus.Connected,
            VpnError.None,
            "10.2.0.2",
            "198.51.100.1",
            443,
            VpnProtocol.WireGuardUdp,
            portForwarding: portForwarding);
    }

    private static PortForwardingState RefreshingMapping()
    {
        return Mapping(PortMappingStatus.PortMappingCommunication);
    }

    private static PortForwardingState Mapping(PortMappingStatus status)
    {
        return new()
        {
            Status = status,
            MappedPort = new TemporaryMappedPort
            {
                MappedPort = new MappedPort(54321, 54321),
            },
        };
    }

    private sealed class BlockingRouteOperations : IPortForwardingRouteOperations
    {
        public ManualResetEventSlim AddStarted { get; } = new();
        public ManualResetEventSlim AllowAddToFinish { get; } = new();
        public ConcurrentQueue<string> Operations { get; } = new();
        public int AddCount => _addCount;

        private int _addCount;

        public int GetInterfaceIndexForLocalIp(string? localIp)
        {
            return 42;
        }

        public void AddRoute(int interfaceIndex)
        {
            Interlocked.Increment(ref _addCount);
            Operations.Enqueue($"add:{interfaceIndex}");
            AddStarted.Set();
            Assert.IsTrue(AllowAddToFinish.Wait(TimeSpan.FromSeconds(2)));
        }

        public void DeleteRoute(int interfaceIndex)
        {
            Operations.Enqueue($"delete:{interfaceIndex}");
        }

        public bool RouteExists(int interfaceIndex)
        {
            return true;
        }
    }

    private sealed class ReconciledRouteOperations : IPortForwardingRouteOperations
    {
        private int _addCount;
        private int _routePresent;

        public ManualResetEventSlim RouteRecreated { get; } = new();
        public int AddCount => Volatile.Read(ref _addCount);

        public int GetInterfaceIndexForLocalIp(string? localIp)
        {
            return 42;
        }

        public void AddRoute(int interfaceIndex)
        {
            int addCount = Interlocked.Increment(ref _addCount);
            Volatile.Write(ref _routePresent, 1);
            if (addCount == 2)
            {
                RouteRecreated.Set();
            }
        }

        public void DeleteRoute(int interfaceIndex)
        {
            Volatile.Write(ref _routePresent, 0);
        }

        public bool RouteExists(int interfaceIndex)
        {
            return Volatile.Read(ref _routePresent) == 1;
        }

        public void SimulateRouteLoss()
        {
            Volatile.Write(ref _routePresent, 0);
        }
    }
}
