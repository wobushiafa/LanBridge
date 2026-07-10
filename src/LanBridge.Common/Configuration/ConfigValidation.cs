using System.Net;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Configuration;

public static class ConfigValidation
{
    public static void EnsureNodeId(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldName} cannot be empty.");
        }
    }

    public static void EnsureHost(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldName} cannot be empty.");
        }
    }

    public static void EnsurePort(int port, string fieldName)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{fieldName} must be between 1 and 65535.");
        }
    }

    public static void EnsurePositive(int value, string fieldName)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"{fieldName} must be greater than 0.");
        }
    }

    public static void EnsureSupportedProtocol(string protocol, string fieldName)
    {
        if (!string.Equals(protocol, "tcp", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{fieldName} must be tcp or udp.");
        }
    }

    public static void EnsureSupportedSignalingTransport(string? transport, string fieldName)
    {
        if (!string.Equals(transport, "tcp", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(transport, "ws", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(transport, "auto", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{fieldName} must be tcp, ws, or auto.");
        }
    }

    public static void EnsureWebSocketPortForTransport(string? transport, int wsPort, string transportFieldName, string wsPortFieldName)
    {
        EnsureSupportedSignalingTransport(transport, transportFieldName);

        if ((string.Equals(transport, "ws", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(transport, "auto", StringComparison.OrdinalIgnoreCase)) &&
            (wsPort < 1 || wsPort > 65535))
        {
            throw new InvalidOperationException(
                $"{wsPortFieldName} must be between 1 and 65535 when {transportFieldName} is ws or auto.");
        }

        if (wsPort != 0)
        {
            EnsurePort(wsPort, wsPortFieldName);
        }
    }

    public static void EnsureCidr(string cidr, string fieldName)
    {
        if (!CidrMatcher.TryParseCidr(cidr, out _))
        {
            throw new InvalidOperationException($"{fieldName} must be a valid IPv4 CIDR.");
        }
    }

    public static void EnsureRegistrationTokenPolicy(IEnumerable<string> tokens, bool requireRegistrationToken)
    {
        if (requireRegistrationToken && !tokens.Any())
        {
            throw new InvalidOperationException("RequireRegistrationToken is enabled but no registration tokens were configured.");
        }
    }
}
