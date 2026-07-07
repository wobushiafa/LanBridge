using System.Net;
using System.Numerics;

namespace LanBridge.Common.Configuration;

public static class CidrParser
{
    public static bool TryParse(string value, out AllowedSubnet subnet)
    {
        subnet = new AllowedSubnet();
        if (!EndpointParser.TryParseOptionalPort(value, out var cidr, out var port))
        {
            return false;
        }

        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefixLength) || prefixLength < 0 || prefixLength > 32)
        {
            return false;
        }

        subnet = new AllowedSubnet { Cidr = cidr, Port = port };
        return true;
    }

    public static void AddSubnets(string value, List<AllowedSubnet> subnets)
    {
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParse(item, out var subnet))
            {
                subnets.Add(subnet);
            }
        }
    }

    public static bool IsInCidr(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var network) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        return IsInCidr(address, network, prefixLength);
    }

    private static bool IsInCidr(IPAddress address, IPAddress network, int prefixLength)
    {
        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length || prefixLength < 0)
        {
            return false;
        }

        if (addressBytes.Length == 4)
        {
            if (prefixLength > 32) return false;
            var addressValue = BitConverter.ToUInt32(addressBytes.Reverse().ToArray());
            var networkValue = BitConverter.ToUInt32(networkBytes.Reverse().ToArray());
            var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
            return (addressValue & mask) == (networkValue & mask);
        }

        if (addressBytes.Length == 16)
        {
            if (prefixLength > 128) return false;
            var addressValue = new BigInteger(addressBytes, isUnsigned: true, isBigEndian: true);
            var networkValue = new BigInteger(networkBytes, isUnsigned: true, isBigEndian: true);
            var mask = prefixLength == 0 ? BigInteger.Zero : (BigInteger.One << prefixLength) - 1;
            mask <<= 128 - prefixLength;
            return (addressValue & mask) == (networkValue & mask);
        }

        return false;
    }
}
