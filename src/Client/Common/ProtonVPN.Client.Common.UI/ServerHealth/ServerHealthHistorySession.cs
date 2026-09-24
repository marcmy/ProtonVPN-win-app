namespace ProtonVPN.Client.Common.UI.ServerHealth;

public static class ServerHealthHistorySession
{
    public static ServerHealthHistoryStore Current { get; } = new();
}
