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
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ProtonVPN.Service.PortMapping;

internal sealed class PortForwardingRouteOperations : IPortForwardingRouteOperations
{
    private const string PROTON_NAT_PMP_GATEWAY_IP = "10.2.0.1";
    private const string DEFAULT_ROUTE_PREFIX = "0.0.0.0/0";
    private const int NETSH_TIMEOUT_IN_MILLISECONDS = 5000;

    public int GetInterfaceIndexForLocalIp(string? localIp)
    {
        if (!IPAddress.TryParse(localIp, out IPAddress? address))
        {
            return 0;
        }

        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties = networkInterface.GetIPProperties();
            UnicastIPAddressInformation? match = properties.UnicastAddresses
                .FirstOrDefault(candidate =>
                    candidate.Address.AddressFamily == AddressFamily.InterNetwork &&
                    candidate.Address.Equals(address));

            if (match is not null)
            {
                return properties.GetIPv4Properties()?.Index ?? 0;
            }
        }

        return 0;
    }

    public void AddRoute(int interfaceIndex)
    {
        RunNetsh(
            $"interface ipv4 add route prefix={DEFAULT_ROUTE_PREFIX} interface={interfaceIndex} " +
            $"nexthop={PROTON_NAT_PMP_GATEWAY_IP} metric=1 store=active");
    }

    public void DeleteRoute(int interfaceIndex)
    {
        RunNetsh(
            $"interface ipv4 delete route prefix={DEFAULT_ROUTE_PREFIX} interface={interfaceIndex} " +
            $"nexthop={PROTON_NAT_PMP_GATEWAY_IP} store=active");
    }

    private static void RunNetsh(string arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "netsh.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start netsh.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(NETSH_TIMEOUT_IN_MILLISECONDS))
        {
            TryKill(process);
            throw new TimeoutException($"netsh timed out. Arguments: {arguments}");
        }

        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"netsh failed with exit code {process.ExitCode}. Arguments: {arguments}. " +
                $"Output: {output}. Error: {error}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch
        {
        }
    }
}
