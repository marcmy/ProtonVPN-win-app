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

using System.Runtime.InteropServices;
using Microsoft.Win32;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.NetworkLogs;
using ProtonVPN.OperatingSystems.Network.Contracts;

namespace ProtonVPN.OperatingSystems.Network;

public class ProTunAdapterConfigurator : IProTunAdapterConfigurator
{
    private const uint DNS_INTERFACE_SETTINGS_VERSION1 = 1;
    private const ulong DNS_SETTING_IPV6 = 0x0001;
    private const ulong DNS_SETTING_NAMESERVER = 0x0002;
    private const ulong DNS_SETTING_REGISTRATION_ENABLED = 0x0008;
    private const ulong DNS_SETTING_REGISTER_ADAPTER_NAME = 0x0010;
    private const ulong DNS_SETTINGS_ENABLE_LLMNR = 0x0080;
    private const uint NETBIOS_DISABLED = 2;

    private readonly IStaticConfiguration _config;
    private readonly ILogger _logger;

    public ProTunAdapterConfigurator(IStaticConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public void Configure()
    {
        Guid adapterGuid = _config.ProTun.WintunAdapterGuid;

        DisableDnsRegistrationAndLlmnr(adapterGuid, isIpv6: false);
        DisableDnsRegistrationAndLlmnr(adapterGuid, isIpv6: true);
        DisableNetBios(adapterGuid);
    }

    private void DisableDnsRegistrationAndLlmnr(Guid adapterGuid, bool isIpv6)
    {
        DnsInterfaceSettings settings = new()
        {
            Version = DNS_INTERFACE_SETTINGS_VERSION1,
            Flags = DNS_SETTING_NAMESERVER |
                    DNS_SETTING_REGISTRATION_ENABLED |
                    DNS_SETTING_REGISTER_ADAPTER_NAME |
                    DNS_SETTINGS_ENABLE_LLMNR |
                    (isIpv6 ? DNS_SETTING_IPV6 : 0),
            RegistrationEnabled = 0,
            RegisterAdapterName = 0,
            EnableLlmnr = 0
        };

        try
        {
            uint result = SetInterfaceDnsSettings(adapterGuid, ref settings);
            if (result != 0)
            {
                _logger.Warn<NetworkLog>($"Failed to disable DNS registration and LLMNR on the ProTUN adapter ({(isIpv6 ? "IPv6" : "IPv4")}). Error status: {result}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn<NetworkLog>($"Failed to disable DNS registration and LLMNR on the ProTUN adapter ({(isIpv6 ? "IPv6" : "IPv4")}).", ex);
        }
    }

    private void DisableNetBios(Guid adapterGuid)
    {
        string path = $@"SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces\Tcpip_{adapterGuid:B}";

        try
        {
            using RegistryKey? interfaceKey = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (interfaceKey is null)
            {
                _logger.Warn<NetworkLog>("Failed to open the ProTUN NetBT interface registry key.");
                return;
            }

            interfaceKey.SetValue("NetbiosOptions", NETBIOS_DISABLED, RegistryValueKind.DWord);
            _logger.Info<NetworkLog>("Disabled NetBIOS over TCP/IP on the ProTUN adapter.");
        }
        catch (Exception ex)
        {
            _logger.Warn<NetworkLog>("Failed to disable NetBIOS over TCP/IP on the ProTUN adapter.", ex);
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint SetInterfaceDnsSettings(Guid interfaceGuid, ref DnsInterfaceSettings settings);

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsInterfaceSettings
    {
        public uint Version;
        public ulong Flags;
        public nint Domain;
        public nint NameServer;
        public nint SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLlmnr;
        public uint QueryAdapterName;
        public nint ProfileNameServer;
    }
}
