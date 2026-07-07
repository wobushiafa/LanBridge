using System.Globalization;

namespace LanBridge.Common.Configuration;

public static class EndpointParser
{
    public static bool TryParseOptionalPort(string value, out string host, out int? port)
    {
        host = value;
        port = null;

        var colonIndex = value.LastIndexOf(':');
        if (colonIndex < 0)
        {
            return !string.IsNullOrWhiteSpace(host);
        }

        if (colonIndex == 0 || colonIndex == value.Length - 1)
        {
            return false;
        }

        host = value[..colonIndex];
        var portText = value[(colonIndex + 1)..];
        if (portText is "*" or "any")
        {
            return !string.IsNullOrWhiteSpace(host);
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) || parsedPort <= 0 || parsedPort > 65535)
        {
            return false;
        }

        port = parsedPort;
        return !string.IsNullOrWhiteSpace(host);
    }

    public static bool TryParseTarget(string value, out TargetEndpoint target)
    {
        target = new TargetEndpoint();
        if (!TryParseOptionalPort(value, out var host, out var port) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        target = new TargetEndpoint { Host = host, Port = port };
        return true;
    }

    public static void AddTargets(string value, List<TargetEndpoint> targets)
    {
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseTarget(item, out var target))
            {
                targets.Add(target);
            }
        }
    }

    public static bool TryParseHostPortProtocol(string value, out string host, out int port, out string protocol)
    {
        host = string.Empty;
        port = 0;
        protocol = "tcp";

        var parts = value.Split(':');
        if (parts.Length < 2)
        {
            return false;
        }

        var lastPart = parts[^1];
        if (string.Equals(lastPart, "udp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lastPart, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            protocol = lastPart.ToLowerInvariant();
            if (parts.Length < 3 || !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out port))
            {
                return false;
            }
            host = string.Join(":", parts[..^2]);
        }
        else
        {
            if (!int.TryParse(lastPart, NumberStyles.None, CultureInfo.InvariantCulture, out port))
            {
                return false;
            }
            host = string.Join(":", parts[..^1]);
        }

        return !string.IsNullOrWhiteSpace(host) && port > 0 && port <= 65535;
    }

    public static bool TryParseTunnelMapping(string value, out TunnelMapping mapping)
    {
        mapping = new TunnelMapping();
        var equalsIndex = value.IndexOf('=');
        if (equalsIndex <= 0 || equalsIndex == value.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(value[..equalsIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var localPort))
        {
            return false;
        }

        var targetPart = value[(equalsIndex + 1)..];
        if (!TryParseHostPortProtocol(targetPart, out var targetHost, out var targetPort, out var protocol))
        {
            return false;
        }

        mapping = new TunnelMapping
        {
            LocalPort = localPort,
            TargetHost = targetHost,
            TargetPort = targetPort,
            Protocol = protocol
        };
        return true;
    }
}
