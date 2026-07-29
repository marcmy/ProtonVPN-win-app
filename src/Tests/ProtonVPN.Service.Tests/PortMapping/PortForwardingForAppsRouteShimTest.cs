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
    }

    [TestMethod]
    public void ReadyMapping_WhenVpnIsConnected_AddsAndThenRemovesRoute()
    {
        using PortForwardingForAppsRouteShim shim = CreateShim(_routeOperations);
        shim.SetVpnState(ConnectedState());

        RaisePortForwardingState(ReadyMapping());

        _routeOperations.Received(1).AddRoute(42);

        shim.SetVpnState(VpnState.Default);

        _routeOperations.Received(2).DeleteRoute(42);
    }

    [TestMethod]
    public async Task DisconnectDuringRouteAdd_LeavesNoTrackedOrInstalledRoute()
    {
        BlockingRouteOperations routeOperations = new();
        using PortForwardingForAppsRouteShim shim = CreateShim(routeOperations);
        shim.SetVpnState(ConnectedState());

        Task addTask = Task.Run(() => RaisePortForwardingState(ReadyMapping()));
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

        RaisePortForwardingState(ReadyMapping());

        _routeOperations.Received(1).AddRoute(42);
        _routeOperations.Received(2).DeleteRoute(42);
    }

    private PortForwardingForAppsRouteShim CreateShim(
        IPortForwardingRouteOperations routeOperations)
    {
        return new(
            _logger,
            _serviceSettings,
            _portMappingProtocolClient,
            routeOperations);
    }

    private void RaisePortForwardingState(PortForwardingState state)
    {
        _portMappingProtocolClient.StateChanged +=
            Raise.Event<EventHandler<EventArgs<PortForwardingState>>>(
                _portMappingProtocolClient,
                new EventArgs<PortForwardingState>(state));
    }

    private static VpnState ConnectedState()
    {
        return new(
            VpnStatus.Connected,
            VpnError.None,
            "10.2.0.2",
            "198.51.100.1",
            443,
            VpnProtocol.WireGuardUdp,
            portForwarding: true);
    }

    private static PortForwardingState ReadyMapping()
    {
        return new()
        {
            Status = PortMappingStatus.SleepingUntilRefresh,
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
    }
}
