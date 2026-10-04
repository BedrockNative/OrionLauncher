using Orion.Domain;

namespace Orion.Infrastructure.Runtime;

public interface IXodusService
{
    event Action<string>? OutputReceived;
    Task EnsureAsync(RuntimeInstallation installation, CancellationToken ct);
}
