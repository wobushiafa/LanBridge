using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LanBridge.Common.Network.Nat;

public sealed class UpnpMapper : INatMapper
{
    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        // 忽略 SSL 证书错误（网关通常不使用 SSL，或者使用自签名）
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private string? _controlUrl;
    private string? _serviceType;
    private string? _localIp;

    public string Protocol => "UPnP";

    public async Task<bool> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. 通过 SSDP 发现网关描述文件地址
            string? locationUrl = await DiscoverLocationUrlAsync(cancellationToken);
            if (string.IsNullOrEmpty(locationUrl))
            {
                return false;
            }

            // 2. 获取本地与该网关通信的 IP 地址
            _localIp = GetLocalIpToGateway(locationUrl);
            if (string.IsNullOrEmpty(_localIp))
            {
                return false;
            }

            // 3. 下载描述 XML，并解析 Control URL 与 Service Type
            string xmlContent = await _httpClient.GetStringAsync(locationUrl, cancellationToken);
            return ParseDescriptionXml(locationUrl, xmlContent);
        }
        catch
        {
            return false;
        }
    }

    public async Task<IPAddress?> GetExternalIpAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_controlUrl) || string.IsNullOrEmpty(_serviceType))
        {
            if (!await DiscoverAsync(cancellationToken))
            {
                return null;
            }
        }

        try
        {
            string soapBody = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:GetExternalIPAddress xmlns:u="{_serviceType}" />
              </s:Body>
            </s:Envelope>
            """;

            string? responseXml = await SendSoapRequestAsync("GetExternalIPAddress", soapBody, cancellationToken);
            if (responseXml == null)
            {
                return null;
            }

            // 提取 NewExternalIPAddress
            var match = Regex.Match(responseXml, @"<NewExternalIPAddress>(.*?)</NewExternalIPAddress>", RegexOptions.IgnoreCase);
            if (match.Success && IPAddress.TryParse(match.Groups[1].Value.Trim(), out var ip))
            {
                return ip;
            }
        }
        catch
        {
            // 忽略错误，返回 null
        }

        return null;
    }

    public async Task<int> CreatePortMappingAsync(int localPort, int externalPort, int lifetimeSeconds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_controlUrl) || string.IsNullOrEmpty(_serviceType) || string.IsNullOrEmpty(_localIp))
        {
            if (!await DiscoverAsync(cancellationToken))
            {
                return 0;
            }
        }

        // UPnP 若 externalPort 传入 0，有些旧路由器会不支持。为稳妥起见，若为 0，我们可以用 localPort 代替
        int portToMap = externalPort == 0 ? localPort : externalPort;

        try
        {
            // 某些老式路由器可能不支持非 0 的 NewLeaseDuration，所以在尝试带有租期的映射失败后，我们可以降级为 0（永久映射，但需要我们退出时手动清理）
            bool success = await TryAddPortMappingSoapAsync(localPort, portToMap, lifetimeSeconds, cancellationToken);
            if (!success && lifetimeSeconds > 0)
            {
                // 降级为 0（永久生命周期，或网关内部决定）
                success = await TryAddPortMappingSoapAsync(localPort, portToMap, 0, cancellationToken);
            }

            return success ? portToMap : 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task DeletePortMappingAsync(int localPort, int externalPort, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_controlUrl) || string.IsNullOrEmpty(_serviceType))
        {
            // 未初始化过，就不需要删除或者尝试发现
            return;
        }

        // UPnP 删除映射按外部端口定位，必须使用创建映射时网关实际分配的端口。
        try
        {
            string soapBody = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:DeletePortMapping xmlns:u="{_serviceType}">
                  <NewRemoteHost></NewRemoteHost>
                  <NewExternalPort>{externalPort}</NewExternalPort>
                  <NewProtocol>UDP</NewProtocol>
                </u:DeletePortMapping>
              </s:Body>
            </s:Envelope>
            """;

            await SendSoapRequestAsync("DeletePortMapping", soapBody, cancellationToken);
        }
        catch
        {
            // 忽略删除失败
        }
    }

    private async Task<bool> TryAddPortMappingSoapAsync(int localPort, int externalPort, int lifetimeSeconds, CancellationToken cancellationToken)
    {
        string soapBody = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <u:AddPortMapping xmlns:u="{_serviceType}">
              <NewRemoteHost></NewRemoteHost>
              <NewExternalPort>{externalPort}</NewExternalPort>
              <NewProtocol>UDP</NewProtocol>
              <NewInternalPort>{localPort}</NewInternalPort>
              <NewInternalClient>{_localIp}</NewInternalClient>
              <NewEnabled>1</NewEnabled>
              <NewPortMappingDescription>LanBridge-{localPort}</NewPortMappingDescription>
              <NewLeaseDuration>{lifetimeSeconds}</NewLeaseDuration>
            </u:AddPortMapping>
          </s:Body>
        </s:Envelope>
        """;

        string? response = await SendSoapRequestAsync("AddPortMapping", soapBody, cancellationToken);
        return response != null && response.Contains("AddPortMappingResponse");
    }

    private async Task<string?> SendSoapRequestAsync(string action, string soapBody, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_controlUrl) || string.IsNullOrEmpty(_serviceType))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _controlUrl);
            request.Content = new StringContent(soapBody, Encoding.UTF8, "text/xml");
            request.Headers.Add("SOAPACTION", $"\"{_serviceType}#{action}\"");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            
            // 记录一下某些错误的返回值，以便调试
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> DiscoverLocationUrlAsync(CancellationToken cancellationToken)
    {
        using var udpClient = new UdpClient();
        udpClient.Client.LingerState = new LingerOption(false, 0);
        
        // 绑定随机端口
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        string mSearch = 
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n\r\n";

        byte[] requestBytes = Encoding.ASCII.GetBytes(mSearch);
        var targetEp = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        // 尝试发送 SSDP 广播
        try
        {
            await udpClient.SendAsync(requestBytes, requestBytes.Length, targetEp);
        }
        catch
        {
            // 如果多播受阻，直接发送广播到 255.255.255.255
            try
            {
                udpClient.EnableBroadcast = true;
                await udpClient.SendAsync(requestBytes, requestBytes.Length, new IPEndPoint(IPAddress.Broadcast, 1900));
            }
            catch
            {
                return null;
            }
        }

        // 接收响应并设定超时
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2)); // 等待 2 秒

        while (!cts.IsCancellationRequested)
        {
            try
            {
                var receiveResult = await udpClient.ReceiveAsync(cts.Token);
                string responseStr = Encoding.UTF8.GetString(receiveResult.Buffer);
                if (responseStr.Contains("200 OK", StringComparison.OrdinalIgnoreCase))
                {
                    // 提取 LOCATION 头
                    var lines = responseStr.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                        {
                            return line.Substring("LOCATION:".Length).Trim();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }
        }

        return null;
    }

    private string? GetLocalIpToGateway(string locationUrl)
    {
        try
        {
            var uri = new Uri(locationUrl);
            // 建立一个临时的 UDP 连接到网关主机，以判断哪张网卡的 IP 是与网关通信的
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(uri.Host, uri.Port);
            if (socket.LocalEndPoint is IPEndPoint localEp)
            {
                return localEp.Address.ToString();
            }
        }
        catch
        {
            // 降级使用本机的第一个非 Loopback 的 IPv4
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }

        return null;
    }

    private bool ParseDescriptionXml(string locationUrl, string xmlContent)
    {
        try
        {
            // 使用 AOT 兼容的 XDocument 简单解析
            var doc = XDocument.Parse(xmlContent);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            // 寻找服务列表
            var services = doc.Descendants(ns + "service");
            foreach (var svc in services)
            {
                var svcType = svc.Element(ns + "serviceType")?.Value;
                if (svcType != null && (
                    svcType.Contains("WANIPConnection:1") || 
                    svcType.Contains("WANIPConnection:2") ||
                    svcType.Contains("WANPPPConnection:1")))
                {
                    _serviceType = svcType;
                    var ctrlUrl = svc.Element(ns + "controlURL")?.Value;
                    if (!string.IsNullOrEmpty(ctrlUrl))
                    {
                        var baseUri = new Uri(locationUrl);
                        var controlUri = new Uri(baseUri, ctrlUrl);
                        _controlUrl = controlUri.ToString();
                        return true;
                    }
                }
            }
        }
        catch
        {
            // 降级为手动字符串提取，防止 XML Namespace 导致解析失败
            try
            {
                var matchSvc = Regex.Match(xmlContent, @"<service>([\s\S]*?)</service>");
                while (matchSvc.Success)
                {
                    var svcContent = matchSvc.Groups[1].Value;
                    var svcTypeMatch = Regex.Match(svcContent, @"<serviceType>(.*?WAN(?:IP|PPP)Connection:\d+)</serviceType>", RegexOptions.IgnoreCase);
                    var ctrlUrlMatch = Regex.Match(svcContent, @"<controlURL>(.*?)</controlURL>", RegexOptions.IgnoreCase);

                    if (svcTypeMatch.Success && ctrlUrlMatch.Success)
                    {
                        _serviceType = svcTypeMatch.Groups[1].Value.Trim();
                        var ctrlUrl = ctrlUrlMatch.Groups[1].Value.Trim();

                        var baseUri = new Uri(locationUrl);
                        var controlUri = new Uri(baseUri, ctrlUrl);
                        _controlUrl = controlUri.ToString();
                        return true;
                    }
                    matchSvc = matchSvc.NextMatch();
                }
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public void Dispose()
    {
    }
}
