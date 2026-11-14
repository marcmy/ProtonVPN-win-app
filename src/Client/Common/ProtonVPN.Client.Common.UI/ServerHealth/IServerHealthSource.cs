using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public interface IServerHealthSource
{
    string HealthServerId { get; }
    string? HealthProbeAddress { get; }
    double HealthServerLoad { get; }
    Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken cancellationToken);

    Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken cancellationToken, bool quickFirstResponse) =>
        ProbeHealthAsync(cancellationToken);
}
