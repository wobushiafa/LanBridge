using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace LanBridge.Common.Network.Nat;

public interface INatMapper : IDisposable
{
    string Protocol { get; }
    Task<IPAddress?> GetExternalIpAsync(CancellationToken cancellationToken = default);
    Task<int> CreatePortMappingAsync(int localPort, int externalPort, int lifetimeSeconds, CancellationToken cancellationToken = default);
    Task DeletePortMappingAsync(int localPort, int externalPort, CancellationToken cancellationToken = default);
}
