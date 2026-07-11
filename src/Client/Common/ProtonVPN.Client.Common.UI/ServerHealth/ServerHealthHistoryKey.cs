namespace ProtonVPN.Client.Common.UI.ServerHealth;

public readonly record struct ServerHealthHistoryKey(string ServerId, string ProbeAddress)
{
    public static ServerHealthHistoryKey Create(string serverId, string probeAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(probeAddress);
        return new(
            serverId.Trim().ToUpperInvariant(),
            probeAddress.Trim().ToUpperInvariant());
    }
}
