using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace LanBridge.Common.Network.Nat;

public sealed class NatMappingManager : IDisposable
{
    private readonly ConcurrentDictionary<int, MappingInfo> _activeMappings = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _renewalTask;
    private INatMapper? _activeMapper;
    private IPAddress? _gatewayAddress;
    private IPAddress? _externalIp;
    private bool _isStarted;
    private bool _isDisposed;

    public event Action<string>? OnLog;

    public IPAddress? ExternalIp => _externalIp;
    public string ActiveProtocol => _activeMapper?.Protocol ?? "None";

    private sealed record MappingInfo(int LocalPort, int RequestedExternalPort, int MappedExternalPort, int LifetimeSeconds);

    public async Task<int> StartMappingAsync(int localPort, int requestedExternalPort = 0, int lifetimeSeconds = 3600)
    {
        if (_isDisposed) return 0;

        // 1. 获取默认网关
        if (_gatewayAddress == null)
        {
            _gatewayAddress = GetDefaultGateway();
            if (_gatewayAddress != null)
            {
                OnLog?.Invoke($"[NAT] Detected default gateway: {_gatewayAddress}");
            }
            else
            {
                OnLog?.Invoke("[NAT] No active IPv4 gateway found.");
            }
        }

        // 2. 首先尝试 NAT-PMP (如果网关地址可用)
        if (_gatewayAddress != null && _activeMapper == null)
        {
            try
            {
                var pmp = new NatPmpMapper(_gatewayAddress);
                OnLog?.Invoke("[NAT] Trying NAT-PMP mapping...");
                int mapped = await pmp.CreatePortMappingAsync(localPort, requestedExternalPort, lifetimeSeconds, _cts.Token);
                if (mapped > 0)
                {
                    _activeMapper = pmp;
                    _externalIp = await pmp.GetExternalIpAsync(_cts.Token);
                    _activeMappings[localPort] = new MappingInfo(localPort, requestedExternalPort, mapped, lifetimeSeconds);
                    OnLog?.Invoke($"[NAT] NAT-PMP map success: Local {localPort} -> External {mapped}. Public IP: {_externalIp}");
                    EnsureRenewalTaskStarted();
                    return mapped;
                }
                else
                {
                    pmp.Dispose();
                    OnLog?.Invoke("[NAT] NAT-PMP mapping failed or not supported by gateway.");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[NAT] NAT-PMP attempt error: {ex.Message}");
            }
        }

        // 3. 降级尝试 UPnP
        if (_activeMapper == null)
        {
            try
            {
                var upnp = new UpnpMapper();
                OnLog?.Invoke("[NAT] Trying UPnP mapping...");
                bool discovered = await upnp.DiscoverAsync(_cts.Token);
                if (discovered)
                {
                    int mapped = await upnp.CreatePortMappingAsync(localPort, requestedExternalPort, lifetimeSeconds, _cts.Token);
                    if (mapped > 0)
                    {
                        _activeMapper = upnp;
                        _externalIp = await upnp.GetExternalIpAsync(_cts.Token);
                        _activeMappings[localPort] = new MappingInfo(localPort, requestedExternalPort, mapped, lifetimeSeconds);
                        OnLog?.Invoke($"[NAT] UPnP map success: Local {localPort} -> External {mapped}. Public IP: {_externalIp}");
                        EnsureRenewalTaskStarted();
                        return mapped;
                    }
                }
                upnp.Dispose();
                OnLog?.Invoke("[NAT] UPnP mapping failed or not supported by gateway.");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[NAT] UPnP attempt error: {ex.Message}");
            }
        }

        OnLog?.Invoke("[NAT] Port mapping failed through all protocols.");
        return 0;
    }

    public async Task StopMappingAsync(int localPort)
    {
        if (_activeMappings.TryRemove(localPort, out var mapping))
        {
            if (_activeMapper != null)
            {
                try
                {
                    OnLog?.Invoke($"[NAT] Deleting port mapping for local port {localPort} ({_activeMapper.Protocol})...");
                    await _activeMapper.DeletePortMappingAsync(
                        mapping.LocalPort,
                        mapping.MappedExternalPort,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[NAT] Delete port mapping error: {ex.Message}");
                }
            }
        }
    }

    private void EnsureRenewalTaskStarted()
    {
        if (_isStarted) return;
        _isStarted = true;

        _renewalTask = Task.Run(async () =>
        {
            // 每 15 分钟（900 秒）做一次续期，或者依据实际租期的 1/3 做周期检测
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(_cts.Token);
                    await RenewMappingsAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[NAT] Mapping renewal loop exception: {ex.Message}");
                }
            }
        });
    }

    private async Task RenewMappingsAsync()
    {
        if (_activeMapper == null || _activeMappings.IsEmpty) return;

        foreach (var mapping in _activeMappings.Values)
        {
            try
            {
                OnLog?.Invoke($"[NAT] Renewing mapping for local port {mapping.LocalPort} ({_activeMapper.Protocol})...");
                int mapped = await _activeMapper.CreatePortMappingAsync(
                    mapping.LocalPort, 
                    mapping.RequestedExternalPort, 
                    mapping.LifetimeSeconds, 
                    _cts.Token);
                
                if (mapped > 0)
                {
                    OnLog?.Invoke($"[NAT] Renew success: Local {mapping.LocalPort} -> External {mapped}");
                }
                else
                {
                    OnLog?.Invoke($"[NAT] Renew failed for local port {mapping.LocalPort}");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[NAT] Renew error for local port {mapping.LocalPort}: {ex.Message}");
            }
        }
    }

    public static IPAddress? GetDefaultGateway()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up && 
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    var props = ni.GetIPProperties();
                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            // 排除常见的 Docker、vEthernet 等虚拟网卡带来的非公网网关地址可能带来的干扰
                            // 我们主要选择正常的 IPv4 局域网网关
                            string ipStr = gw.Address.ToString();
                            if (ipStr.StartsWith("192.168.") || ipStr.StartsWith("10.") || 
                                (ipStr.StartsWith("172.") && IsPrivateIPv4ClassB(gw.Address)))
                            {
                                return gw.Address;
                            }
                        }
                    }
                }
            }

            // 兜底：如果没筛选出符合私网段的网关，就把发现的第一个 IPv4 网关返回
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up && 
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    var props = ni.GetIPProperties();
                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            return gw.Address;
                        }
                    }
                }
            }
        }
        catch
        {
            // 忽略网卡查询异常
        }
        return null;
    }

    private static bool IsPrivateIPv4ClassB(IPAddress ip)
    {
        byte[] bytes = ip.GetAddressBytes();
        return bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        try
        {
            // 清理所有的活动映射
            foreach (var port in _activeMappings.Keys)
            {
                StopMappingAsync(port).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // 忽略释放期间的清理错误
        }

        _activeMapper?.Dispose();
        _cts.Dispose();
    }
}
