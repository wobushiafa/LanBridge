using System.Net;

namespace LanBridge.Common.Configuration;

public static class CidrMatcher
{
    public static bool IsInCidr(IPAddress address, string cidr)
    {
        if (!TryParseCidr(cidr, out var parsed))
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        if (addressBytes.Length != 4 || parsed.NetworkBytes.Length != 4)
        {
            return false;
        }

        var addressValue = BitConverter.ToUInt32(addressBytes.Reverse().ToArray());
        var networkValue = BitConverter.ToUInt32(parsed.NetworkBytes.Reverse().ToArray());
        var mask = parsed.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - parsed.PrefixLength);
        return (addressValue & mask) == (networkValue & mask);
    }

    public static bool TryParseCidr(string cidr, out ParsedCidr parsed)
    {
        parsed = default;
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var network) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var bytes = network.GetAddressBytes();
        if (bytes.Length != 4 || prefixLength is < 0 or > 32)
        {
            return false;
        }

        parsed = new ParsedCidr(bytes, prefixLength);
        return true;
    }

    public readonly record struct ParsedCidr(byte[] NetworkBytes, int PrefixLength);
}
