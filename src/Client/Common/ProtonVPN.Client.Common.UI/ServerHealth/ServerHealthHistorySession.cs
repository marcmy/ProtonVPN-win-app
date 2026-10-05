using System;
using System.IO;
using System.Net.NetworkInformation;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public static class ServerHealthHistorySession
{
    public static ServerHealthHistoryStore Current { get; } = new(maximumConcurrentProbes: 1,
        minimumProbeInterval: TimeSpan.FromMinutes(1),
        cachePath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtonVPN", "ServerHealth", "ping-cache-v1.json"));

    public static ServerHealthRefreshScheduler Refresh { get; } = new(Current);

    static ServerHealthHistorySession()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => Current.InvalidateFreshness();
    }
}
