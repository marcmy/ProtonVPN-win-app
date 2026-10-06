/*
 * Copyright (c) 2025 Proton AG
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
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using ProtonVPN.Common.Core.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.NetworkFilter;
using ProtonVPN.OperatingSystems.Network.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using ProtonVPN.ProTun.Contracts.Adapters;
using ProtonVPN.Service.Firewall;
using ProtonVPN.Service.Settings;
using ProtonVPN.Service.SplitTunneling;
using ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;
using ProtonVPN.Vpn.Common;
using ProtonVPN.Vpn.SplitTunnel;

namespace ProtonVPN.Service.Tests.SplitTunneling;

[TestClass]
public class SplitTunnelTest
{
    private ILogger _logger;
    private ISplitTunnelRouting _splitTunnelRouting;
    private INetworkUtilities _networkUtilities;
    private ISystemNetworkInterfaces _networkInterfaces;
    private IConfiguration _config;
    private IServiceSettings _serviceSettings;
    private ISplitTunnelClient _splitTunnelClient;
    private IAppFilter _appFilter;
    private IPermittedRemoteAddress _permittedRemoteAddress;
    private IAdapterDetailsCache _proTunAdapterDetailsCache;
    private ISplitTunnelDomainPoller _domainPoller;
    private IFolderAppMonitor _folderMonitor;

    [TestInitialize]
    public void TestInitialize()
    {
        _logger = Substitute.For<ILogger>();
        _splitTunnelRouting = Substitute.For<ISplitTunnelRouting>();
        _networkUtilities = Substitute.For<INetworkUtilities>();
        _networkInterfaces = Substitute.For<ISystemNetworkInterfaces>();
        _config = Substitute.For<IConfiguration>();
        _serviceSettings = Substitute.For<IServiceSettings>();
        _splitTunnelClient = Substitute.For<ISplitTunnelClient>();
        _appFilter = Substitute.For<IAppFilter>();
        _permittedRemoteAddress = Substitute.For<IPermittedRemoteAddress>();
        _proTunAdapterDetailsCache = Substitute.For<IAdapterDetailsCache>();
        _domainPoller = Substitute.For<ISplitTunnelDomainPoller>();
        _folderMonitor = Substitute.For<IFolderAppMonitor>();
        _folderMonitor.AppPaths.Returns(Array.Empty<string>());
    }

    [TestMethod]
    public void OnVpnConnecting_WhenBlockMode_DisableReversed()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block
        });
        SplitTunnel splitTunnel = GetSplitTunnel(false, true);

        // Act
        splitTunnel.OnVpnConnecting(GetConnectingVpnState());

        // Assert
        _splitTunnelClient.Received(1).Disable();
    }

    [TestMethod]
    public void OnVpnConnecting_WhenBlockMode_Disable()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit
        });
        SplitTunnel splitTunnel = GetSplitTunnel(true);

        // Act
        splitTunnel.OnVpnConnecting(GetConnectingVpnState());

        // Assert
        _splitTunnelClient.Received(1).Disable();
    }

    [TestMethod]
    public void OnVpnConnected_PermitRemoteAddressesOnBlockMode()
    {
        // Arrange
        string[] addresses = ["127.0.0.1", "192.168.0.1", "8.8.8.8"];
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            Ips = addresses,
            AppPaths = [],
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Assert
        _permittedRemoteAddress.Received(1).Add(
            Arg.Is<string[]>(actual => actual.SequenceEqual(addresses)),
            NetworkFilter.Action.HardPermit);
    }

    [TestMethod]
    public void OnVpnConnected_WhenBlockMode_CallEnable()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [],
            Ips = [],
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Assert
        _splitTunnelClient.Received(1).EnableExcludeMode(Arg.Any<string[]>(), Arg.Any<IPAddress>(), Arg.Any<IPAddress>());
    }

    [TestMethod]
    public void OnVpnConnected_WhenBlockMode_CalloutDriverStart()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [],
            Ips = [],
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());
    }

    [TestMethod]
    public void OnVpnConnected_WhenPermitMode_CalloutDriverStart()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());
    }

    [TestMethod]
    public void OnVpnConnected_WhenDisabled_CalloutDriverDoNotStart()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Disabled
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());
    }

    [TestMethod]
    public void OnVpnConnected_WhenDisabled_DoNotEnable()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Disabled
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Assert
        _splitTunnelClient.Received(0);
    }

    [TestMethod]
    public void OnVpnConnected_WhenPermitMode_EnableReversed()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Assert
        _splitTunnelClient
            .Received(1)
            .EnableIncludeMode(Arg.Any<string[]>(), Arg.Any<IPAddress>(), Arg.Any<IPAddress>());
    }

    [TestMethod]
    public void OnVpnConnected_PermitAppsOnBlockMode()
    {
        // Arrange
        string[] apps = ["app1", "app2", "app3"];
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = apps,
            Ips = [],
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Assert
        _appFilter.Received(1).Add(Arg.Is<string[]>(paths => paths.SequenceEqual(apps)), Arg.Any<Tuple<Layer, NetworkFilter.Action>[]>());
    }

    [TestMethod]
    public void OnVpnConnecting_ShouldBlockApps_WhenModeIsPermit()
    {
        // Arrange
        string[] apps = ["app1", "app2", "app3"];
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit,
            AppPaths = apps
        });
        SplitTunnel splitTunnel = GetSplitTunnel(true);

        // Act
        splitTunnel.OnVpnConnecting(GetConnectingVpnState());

        // Assert
        _appFilter.Received(1).Add(Arg.Is<string[]>(paths => paths.SequenceEqual(apps)), Arg.Any<Tuple<Layer, NetworkFilter.Action>[]>());
    }

    [TestMethod]
    public void OnVpnDisconnected_ManualDisconnect_ShouldDisable()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block
        });
        SplitTunnel splitTunnel = GetSplitTunnel(true);

        // Act
        splitTunnel.OnVpnDisconnected(GetDisconnectedVpnState(true));

        // Assert
        _splitTunnelClient.Received(1).Disable();
    }

    [TestMethod]
    public void OnVpnDisconnected_ManualDisconnect_ShouldDisableReversed()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit
        });
        SplitTunnel splitTunnel = GetSplitTunnel(false, true);

        // Act
        splitTunnel.OnVpnDisconnected(GetDisconnectedVpnState(true));

        // Assert
        _splitTunnelClient.Received(1).Disable();
    }

    [TestMethod]
    public void OnVpnDisconnected_ManualDisconnect_ShouldStopCalloutDriver()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnDisconnected(GetDisconnectedVpnState(true));
    }

    [TestMethod]
    public void OnServiceSettingsChanged_WhenConnectedInBlockMode_ReappliesSplitTunnelSettings()
    {
        // Arrange
        string[] initialApps = ["initial-app.exe"];
        string[] updatedApps = ["updated-app.exe"];
        SplitTunnelSettingsIpcEntity splitTunnelSettings = new()
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = initialApps,
            Ips = [],
        };
        _serviceSettings.SplitTunnelSettings.Returns(_ => splitTunnelSettings);
        SplitTunnel splitTunnel = GetSplitTunnel();
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Act
        splitTunnelSettings = new()
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = updatedApps,
            Ips = [],
        };
        ((IServiceSettingsAware)splitTunnel).OnServiceSettingsChanged(new MainSettingsIpcEntity());

        // Assert
        _splitTunnelClient.Received(1).EnableExcludeMode(
            Arg.Is<string[]>(paths => paths.SequenceEqual(updatedApps)),
            Arg.Any<IPAddress>(),
            Arg.Any<IPAddress>());
    }

    [TestMethod]
    public void OnServiceSettingsChanged_WhenConnected_ReplacesRoutesWithCurrentAddresses()
    {
        // Arrange
        string[] initialAddresses = ["192.0.2.10"];
        string[] updatedAddresses = ["198.51.100.20"];
        SplitTunnelSettingsIpcEntity splitTunnelSettings = new()
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [],
            Ips = initialAddresses,
        };
        _serviceSettings.SplitTunnelSettings.Returns(_ => splitTunnelSettings);

        SplitTunnel splitTunnel = GetSplitTunnel();
        splitTunnel.UpdateContext(CreateSplitTunnelContext(initialAddresses));
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Act
        splitTunnelSettings = new()
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [],
            Ips = updatedAddresses,
        };
        ((IServiceSettingsAware)splitTunnel).OnServiceSettingsChanged(new MainSettingsIpcEntity());

        // Assert
        _splitTunnelRouting.Received(1).DeleteRoutes(
            Arg.Is<VpnConfig>(config => config.SplitTunnelIPs.SequenceEqual(initialAddresses)));
        _splitTunnelRouting.Received(1).SetUpRoutingTable(
            Arg.Is<VpnConfig>(config => config.SplitTunnelIPs.SequenceEqual(updatedAddresses)),
            "1.1.1.1",
            Arg.Any<bool>());
    }

    [TestMethod]
    public void OnVpnConnected_WhenBlockModeWithDomainRule_StartsDomainPoller()
    {
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [],
            Ips = ["*.Example.com"],
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        _domainPoller.Received(1).ReplaceRules(
            Arg.Is<string[]>(rules => rules.SequenceEqual(new[] { "example.com" })));
        _domainPoller.Received(1).Start();
        _permittedRemoteAddress.DidNotReceive().Add(
            Arg.Is<string[]>(addresses => addresses.Contains("example.com")),
            Arg.Any<NetworkFilter.Action>());
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
            Arg.Is<string[]>(addresses => addresses.Length == 1 && addresses[0].StartsWith("8.8.8.8")),
            NetworkFilter.Action.HardPermit);
    }

    [TestMethod]
    public void DomainAddressesChanged_WhenConnected_ReplacesFiltersAndRoutesWithCombinedAddresses()
    {
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [],
            Ips = ["8.8.8.8", "example.com"],
        });
        SplitTunnel splitTunnel = GetSplitTunnel();
        splitTunnel.UpdateContext(CreateSplitTunnelContext(["8.8.8.8"]));
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        _domainPoller.AddressesChanged += Raise.Event<EventHandler<string[]>>(
            _domainPoller,
            new[] { "203.0.113.10" });

        _permittedRemoteAddress.Received().Add(
            Arg.Is<string[]>(addresses =>
                addresses.Any(address => address.StartsWith("8.8.8.8")) &&
                addresses.Contains("203.0.113.10")),
            NetworkFilter.Action.HardPermit);
        _splitTunnelRouting.Received().SetUpRoutingTable(
            Arg.Is<VpnConfig>(config =>
                config.SplitTunnelIPs.Any(address => address.StartsWith("8.8.8.8")) &&
                config.SplitTunnelIPs.Contains("203.0.113.10")),
            "1.1.1.1",
            Arg.Any<bool>());
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

        _domainPoller.Received().Stop();
    }

    [TestMethod]
    [DataRow(SplitTunnelModeIpcEntity.Block)]
    [DataRow(SplitTunnelModeIpcEntity.Permit)]
    public void AppPatterns_UseConcretePathsForConnectingConnectedAndLiveApply(SplitTunnelModeIpcEntity mode)
    {
        string root = Directory.CreateTempSubdirectory("proton-service-app-pattern-").FullName;
        try
        {
            string firstDirectory = Directory.CreateDirectory(Path.Combine(root, "1.0")).FullName;
            string firstApp = Path.Combine(firstDirectory, "app.exe");
            File.WriteAllText(firstApp, string.Empty);
            string pattern = Path.Combine(root, "*", "app.exe");
            SplitTunnelSettingsIpcEntity settings = new()
            {
                Mode = mode,
                AppPaths = [pattern, firstApp],
                Ips = [],
            };
            _serviceSettings.SplitTunnelSettings.Returns(settings);
            SplitTunnel splitTunnel = GetSplitTunnel();
            // Exercise the OpenVPN include-mode IPv6 firewall as well as the callout path.
            VpnState connected = new(VpnStatus.Connected, VpnError.None,
                "1.1.1.1", "2.2.2.2", 443, VpnProtocol.OpenVpnUdp);

            splitTunnel.OnVpnConnecting(GetConnectingVpnState());
            if (mode == SplitTunnelModeIpcEntity.Permit)
            {
                _appFilter.Received(1).Add(
                    Arg.Is<string[]>(paths => paths.SequenceEqual(new[] { firstApp })),
                    Arg.Is<Tuple<Layer, NetworkFilter.Action>[]>(filters => filters.All(filter => filter.Item2 == NetworkFilter.Action.SoftBlock)));
            }
            splitTunnel.OnVpnConnected(connected);
            AssertAppPathsApplied(mode, [firstApp]);

            string secondDirectory = Directory.CreateDirectory(Path.Combine(root, "2.0")).FullName;
            string secondApp = Path.Combine(secondDirectory, "app.exe");
            File.WriteAllText(secondApp, string.Empty);
            _appFilter.ClearReceivedCalls();
            _splitTunnelClient.ClearReceivedCalls();

            // Same saved settings; Apply must rediscover the new version, not use the UI's old snapshot.
            splitTunnel.OnServiceSettingsChanged(new MainSettingsIpcEntity());
            AssertAppPathsApplied(mode, [firstApp, secondApp]);
            CollectionAssert.AreEqual(new[] { pattern, firstApp }, settings.AppPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void AssertAppPathsApplied(SplitTunnelModeIpcEntity mode, string[] expected)
    {
        if (mode == SplitTunnelModeIpcEntity.Block)
        {
            _splitTunnelClient.Received(1).EnableExcludeMode(
                Arg.Is<string[]>(paths => paths.Length == expected.Length && expected.All(path => paths.Contains(path))),
                Arg.Any<IPAddress>(), Arg.Any<IPAddress>());
        }
        else
        {
            _splitTunnelClient.Received(1).EnableIncludeMode(
                Arg.Is<string[]>(paths => paths.Length == expected.Length && expected.All(path => paths.Contains(path))),
                Arg.Any<IPAddress>(), Arg.Any<IPAddress>());
        }
        _appFilter.Received().Add(
            Arg.Is<string[]>(paths => paths.Length == expected.Length && expected.All(path => paths.Contains(path))),
            Arg.Any<Tuple<Layer, NetworkFilter.Action>[]>());
        _appFilter.DidNotReceive().Add(
            Arg.Is<string[]>(paths => paths.Any(path => path.Contains('*'))),
            Arg.Any<Tuple<Layer, NetworkFilter.Action>[]>());
    }

    [TestMethod]
    [DataRow(SplitTunnelModeIpcEntity.Block)]
    [DataRow(SplitTunnelModeIpcEntity.Permit)]
    public async Task PreparedFolderAddition_LiveApplyUsesNewAndExistingAppsWithoutWaitingForScan(SplitTunnelModeIpcEntity mode)
    {
        string root = Directory.CreateTempSubdirectory("proton-live-folder-apply-").FullName;
        string first = Path.Combine(root, "first");
        string second = Path.Combine(root, "second");
        int scans = 0;
        using FolderAppMonitor monitor = new(_logger, (folder, _, _) =>
        {
            scans++;
            return Task.FromResult(new FolderScanResult([Path.Combine(folder, "app.exe")], null));
        });
        _folderMonitor = monitor;
        try
        {
            await monitor.PrepareRuleAsync(first, default);
            _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
            { Mode = mode, FolderPaths = [first], AppPaths = [@"C:\explicit.exe"], Ips = [] });
            SplitTunnel splitTunnel = GetSplitTunnel();
            splitTunnel.OnVpnConnected(new(VpnStatus.Connected, VpnError.None,
                "1.1.1.1", "2.2.2.2", 443, VpnProtocol.OpenVpnUdp));
            await monitor.PrepareRuleAsync(second, default);
            _splitTunnelClient.ClearReceivedCalls();
            _appFilter.ClearReceivedCalls();
            _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
            { Mode = mode, FolderPaths = [first, second], AppPaths = [@"C:\explicit.exe"], Ips = [] });
            splitTunnel.OnServiceSettingsChanged(new());
            AssertAppPathsApplied(mode, [@"C:\explicit.exe", Path.Combine(first, "app.exe"), Path.Combine(second, "app.exe")]);
            Assert.AreEqual(2, monitor.Status.Executables);
            Assert.IsFalse(monitor.Status.IsScanning);
            Assert.AreEqual(2, scans);
        }
        finally { monitor.Stop(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(SplitTunnelModeIpcEntity.Block)]
    [DataRow(SplitTunnelModeIpcEntity.Permit)]
    public void FolderChanges_UpdateCompletePolicyAndRetainExplicitOwner(SplitTunnelModeIpcEntity mode)
    {
        string explicitApp = @"C:\tools\explicit.exe";
        string folderApp = @"C:\tools\nested\new.exe";
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = mode, AppPaths = [explicitApp], FolderPaths = [@"C:\tools"], Ips = [],
        });
        _folderMonitor.AppPaths.Returns(new[] { explicitApp });
        SplitTunnel splitTunnel = GetSplitTunnel();
        VpnState connected = new(VpnStatus.Connected, VpnError.None, "1.1.1.1", "2.2.2.2", 443, VpnProtocol.OpenVpnUdp);
        splitTunnel.OnVpnConnecting(GetConnectingVpnState());
        splitTunnel.OnVpnConnected(connected);
        _folderMonitor.Received().ReplaceRules(Arg.Is<string[]>(folders => folders.SequenceEqual(new[] { @"C:\tools" })));
        _folderMonitor.Received(1).Start();

        _splitTunnelClient.ClearReceivedCalls();
        _appFilter.ClearReceivedCalls();
        _splitTunnelRouting.ClearReceivedCalls();
        _domainPoller.ClearReceivedCalls();
        _folderMonitor.AppPaths.Returns(new[] { explicitApp, folderApp });
        _folderMonitor.PathsChanged += Raise.Event<EventHandler>(_folderMonitor, EventArgs.Empty);
        AssertAppPathsApplied(mode, [explicitApp, folderApp]);
        _splitTunnelRouting.DidNotReceive().DeleteRoutes(Arg.Any<VpnConfig>());
        _domainPoller.DidNotReceive().Stop();

        _splitTunnelClient.ClearReceivedCalls();
        _appFilter.ClearReceivedCalls();
        _folderMonitor.AppPaths.Returns(Array.Empty<string>());
        _folderMonitor.PathsChanged += Raise.Event<EventHandler>(_folderMonitor, EventArgs.Empty);
        AssertAppPathsApplied(mode, [explicitApp]);
        splitTunnel.OnVpnDisconnected(GetDisconnectedVpnState(true));
        _folderMonitor.Received().Stop();
        _splitTunnelClient.ClearReceivedCalls();
        _folderMonitor.PathsChanged += Raise.Event<EventHandler>(_folderMonitor, EventArgs.Empty);
        _splitTunnelClient.DidNotReceive().EnableExcludeMode(Arg.Any<string[]>(), Arg.Any<IPAddress>(), Arg.Any<IPAddress>());
        _splitTunnelClient.DidNotReceive().EnableIncludeMode(Arg.Any<string[]>(), Arg.Any<IPAddress>(), Arg.Any<IPAddress>());
    }

    [TestMethod]
    public void FolderChanges_BlockMode_RestoresConfiguredAndResolvedDomainPermits()
    {
        string explicitApp = @"C:\tools\explicit.exe";
        string folderApp = @"C:\tools\nested\new.exe";
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [explicitApp],
            FolderPaths = [@"C:\tools"],
            Ips = ["8.8.8.8", "example.com"],
        });
        _folderMonitor.AppPaths.Returns(new[] { explicitApp });
        SplitTunnel splitTunnel = GetSplitTunnel();
        VpnState connected = new(VpnStatus.Connected, VpnError.None,
            "1.1.1.1", "2.2.2.2", 443, VpnProtocol.OpenVpnUdp);
        splitTunnel.OnVpnConnected(connected);

        _domainPoller.AddressesChanged += Raise.Event<EventHandler<string[]>>(
            _domainPoller,
            new[] { "203.0.113.10" });

        _permittedRemoteAddress.ClearReceivedCalls();
        _splitTunnelRouting.ClearReceivedCalls();
        _domainPoller.ClearReceivedCalls();
        _folderMonitor.AppPaths.Returns(new[] { explicitApp, folderApp });

        _folderMonitor.PathsChanged += Raise.Event<EventHandler>(_folderMonitor, EventArgs.Empty);

        _permittedRemoteAddress.Received(1).Add(
            Arg.Is<string[]>(addresses =>
                addresses.Any(address => address.StartsWith("8.8.8.8")) &&
                addresses.Contains("203.0.113.10")),
            NetworkFilter.Action.HardPermit);
        _splitTunnelRouting.DidNotReceive().DeleteRoutes(Arg.Any<VpnConfig>());
        _domainPoller.DidNotReceive().Stop();
    }

    private SplitTunnel GetSplitTunnel(bool enabled = false, bool reverseEnabled = false)
    {
        return new SplitTunnel(
            enabled,
            reverseEnabled,
            _logger,
            _splitTunnelRouting,
            _networkUtilities,
            _networkInterfaces,
            _config,
            _serviceSettings,
            _splitTunnelClient,
            _appFilter,
            _permittedRemoteAddress,
            _proTunAdapterDetailsCache,
            _domainPoller,
            _folderMonitor);
    }

    private VpnState GetConnectedVpnState()
    {
        return new VpnState(
            VpnStatus.Connected,
            VpnError.None,
            "1.1.1.1",
            "2.2.2.2",
            443,
            VpnProtocol.Smart);
    }

    private VpnState GetDisconnectedVpnState(bool manualDisconnect = false)
    {
        return new VpnState(
            VpnStatus.Disconnected,
            manualDisconnect ? VpnError.None : VpnError.Unknown,
            "1.1.1.1",
            "2.2.2.2",
            443,
            VpnProtocol.Smart);
    }

    private VpnState GetConnectingVpnState()
    {
        return new VpnState(
            VpnStatus.Disconnected,
            VpnError.None,
            "1.1.1.1",
            "2.2.2.2",
            443,
            VpnProtocol.Smart);
    }

    private static SplitTunnelContext CreateSplitTunnelContext(string[] addresses)
    {
        VpnConfig config = new(new VpnConfigParameters
        {
            SplitTunnelMode = SplitTunnelMode.Block,
            SplitTunnelIPs = addresses,
            VpnProtocol = VpnProtocol.WireGuardUdp,
            PreferredProtocols = [],
        });
        VpnHost server = new(
            "test.protonvpn.net",
            "203.0.113.1",
            string.Empty,
            default,
            string.Empty,
            true,
            null);

        return new SplitTunnelContext(config, new VpnEndpoint(server, VpnProtocol.WireGuardUdp, 443));
    }
}
