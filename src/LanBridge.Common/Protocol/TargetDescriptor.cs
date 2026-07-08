namespace LanBridge.Common.Protocol;

public readonly record struct TargetDescriptor(string Host, int Port, string Protocol)
{
    public string? NodeId { get; init; }

    public override string ToString()
    {
        var baseStr = $"{Host}:{Port}:{Protocol}";
        return NodeId != null ? $"{baseStr}@{NodeId}" : baseStr;
    }
}

public static class TargetDescriptorParser
{
    public static bool TryParse(string value, out TargetDescriptor descriptor)
    {
        descriptor = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Extract @nodeId suffix if present
        string? nodeId = null;
        var atIndex = value.LastIndexOf('@');
        string targetPart;
        if (atIndex > 0 && atIndex < value.Length - 1)
        {
            nodeId = value[(atIndex + 1)..];
            targetPart = value[..atIndex];
        }
        else
        {
            targetPart = value;
        }

        var parts = targetPart.Split(':');
        if (parts.Length < 2)
        {
            return false;
        }

        string protocol = "tcp";
        int port;
        string host;

        var lastPart = parts[^1];
        if (string.Equals(lastPart, "udp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lastPart, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            protocol = lastPart.ToLowerInvariant();
            if (parts.Length < 3 || !int.TryParse(parts[^2], out port))
            {
                return false;
            }

            host = string.Join(":", parts[..^2]);
        }
        else
        {
            if (!int.TryParse(lastPart, out port))
            {
                return false;
            }

            host = string.Join(":", parts[..^1]);
        }

        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            return false;
        }

        descriptor = new TargetDescriptor(host, port, protocol) { NodeId = nodeId };
        return true;
    }
}
