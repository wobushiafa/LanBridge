using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LanBridge.Common.Protocol;

public enum StunNatType
{
    Unknown,
    Blocked,
    FullCone,
    RestrictedCone,
    PortRestrictedCone,
    Symmetric
}

public sealed record StunBindingRequest(byte[] TransactionId, bool ChangeIp, bool ChangePort);

public sealed record StunBindingResult(IPEndPoint PublicEndPoint, IPEndPoint ServerEndPoint, bool StandardProtocol);

public sealed record NatDetectionResult(
    StunNatType NatType,
    string Reason,
    IPEndPoint? PublicEndPoint,
    IPEndPoint? AlternatePublicEndPoint,
    bool PortPreserved);

public static class StunProtocol
{
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccessResponse = 0x0101;
    private const ushort AttributeMappedAddress = 0x0001;
    private const ushort AttributeChangeRequest = 0x0003;
    private const ushort AttributeXorMappedAddress = 0x0020;
    private const ushort AttributeSoftware = 0x8022;
    private const uint MagicCookie = 0x2112A442;
    private static readonly byte[] Software = Encoding.UTF8.GetBytes("LanBridge.SignalingServer");

    public static byte[] CreateBindingRequest(bool changeIp = false, bool changePort = false, byte[]? transactionId = null)
    {
        transactionId ??= RandomNumberGenerator.GetBytes(12);
        var attributesLength = changeIp || changePort ? 8 : 0;
        var buffer = new byte[20 + attributesLength];

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)attributesLength);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), MagicCookie);
        Buffer.BlockCopy(transactionId, 0, buffer, 8, 12);

        if (attributesLength > 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(20, 2), AttributeChangeRequest);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(22, 2), 4);
            var flags = (changeIp ? 0x04u : 0u) | (changePort ? 0x02u : 0u);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(24, 4), flags);
        }

        return buffer;
    }

    public static bool TryParseBindingRequest(byte[] data, out StunBindingRequest request)
    {
        request = new StunBindingRequest(Array.Empty<byte>(), false, false);
        if (data.Length < 20 ||
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)) != BindingRequest ||
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)) != MagicCookie)
        {
            return false;
        }

        var messageLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
        if (20 + messageLength > data.Length)
        {
            return false;
        }

        var transactionId = data.AsSpan(8, 12).ToArray();
        var changeIp = false;
        var changePort = false;

        var offset = 20;
        while (offset + 4 <= 20 + messageLength)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2, 2));
            offset += 4;
            if (offset + length > data.Length)
            {
                return false;
            }

            if (type == AttributeChangeRequest && length == 4)
            {
                var flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                changeIp = (flags & 0x04) != 0;
                changePort = (flags & 0x02) != 0;
            }

            offset += Align4(length);
        }

        request = new StunBindingRequest(transactionId, changeIp, changePort);
        return true;
    }

    public static byte[] CreateBindingSuccessResponse(byte[] transactionId, IPEndPoint mappedEndPoint)
    {
        var mappedAddressLength = mappedEndPoint.AddressFamily == AddressFamily.InterNetwork ? 8 : 20;
        var xorMappedLength = mappedAddressLength;
        var softwareLength = Align4(Software.Length);
        var attributesLength = 4 + xorMappedLength + 4 + mappedAddressLength + 4 + softwareLength;
        var buffer = new byte[20 + attributesLength];

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), BindingSuccessResponse);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)attributesLength);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), MagicCookie);
        Buffer.BlockCopy(transactionId, 0, buffer, 8, 12);

        var offset = 20;
        offset = WriteAddressAttribute(buffer, offset, AttributeXorMappedAddress, mappedEndPoint, xor: true, transactionId);
        offset = WriteAddressAttribute(buffer, offset, AttributeMappedAddress, mappedEndPoint, xor: false, transactionId);

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), AttributeSoftware);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 2, 2), (ushort)Software.Length);
        Buffer.BlockCopy(Software, 0, buffer, offset + 4, Software.Length);

        return buffer;
    }

    public static bool TryParseBindingSuccessResponse(byte[] data, byte[] transactionId, out IPEndPoint? mappedEndPoint)
    {
        mappedEndPoint = null;
        if (data.Length < 20 ||
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)) != BindingSuccessResponse ||
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)) != MagicCookie ||
            !data.AsSpan(8, 12).SequenceEqual(transactionId))
        {
            return false;
        }

        var messageLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
        if (20 + messageLength > data.Length)
        {
            return false;
        }

        IPEndPoint? fallbackMapped = null;
        var offset = 20;
        while (offset + 4 <= 20 + messageLength)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2, 2));
            offset += 4;
            if (offset + length > data.Length)
            {
                return false;
            }

            if ((type == AttributeXorMappedAddress || type == AttributeMappedAddress) &&
                TryReadAddressAttribute(data.AsSpan(offset, length), type == AttributeXorMappedAddress, transactionId, out var endpoint))
            {
                if (type == AttributeXorMappedAddress)
                {
                    mappedEndPoint = endpoint;
                    return true;
                }

                fallbackMapped = endpoint;
            }

            offset += Align4(length);
        }

        mappedEndPoint = fallbackMapped;
        return mappedEndPoint != null;
    }

    private static int WriteAddressAttribute(byte[] buffer, int offset, ushort type, IPEndPoint endpoint, bool xor, byte[] transactionId)
    {
        var addressBytes = endpoint.Address.GetAddressBytes();
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 2, 2), (ushort)(addressBytes.Length == 4 ? 8 : 20));
        buffer[offset + 4] = 0;
        buffer[offset + 5] = addressBytes.Length == 4 ? (byte)0x01 : (byte)0x02;

        var port = xor ? endpoint.Port ^ (int)(MagicCookie >> 16) : endpoint.Port;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 6, 2), (ushort)port);

        if (xor && addressBytes.Length == 4)
        {
            var cookieBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)MagicCookie));
            for (var i = 0; i < 4; i++)
            {
                buffer[offset + 8 + i] = (byte)(addressBytes[i] ^ cookieBytes[i]);
            }
        }
        else if (xor)
        {
            var cookieBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)MagicCookie));
            for (var i = 0; i < 4; i++)
            {
                buffer[offset + 8 + i] = (byte)(addressBytes[i] ^ cookieBytes[i]);
            }
            for (var i = 0; i < 12; i++)
            {
                buffer[offset + 12 + i] = (byte)(addressBytes[i + 4] ^ transactionId[i]);
            }
        }
        else
        {
            Buffer.BlockCopy(addressBytes, 0, buffer, offset + 8, addressBytes.Length);
        }

        return offset + 4 + (addressBytes.Length == 4 ? 8 : 20);
    }

    private static bool TryReadAddressAttribute(ReadOnlySpan<byte> attribute, bool xor, byte[] transactionId, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, 0);
        if (attribute.Length < 8)
        {
            return false;
        }

        var family = attribute[1];
        var port = BinaryPrimitives.ReadUInt16BigEndian(attribute.Slice(2, 2));
        if (xor)
        {
            port ^= (ushort)(MagicCookie >> 16);
        }

        if (family == 0x01 && attribute.Length >= 8)
        {
            var addressBytes = attribute.Slice(4, 4).ToArray();
            if (xor)
            {
                var cookieBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)MagicCookie));
                for (var i = 0; i < 4; i++)
                {
                    addressBytes[i] ^= cookieBytes[i];
                }
            }

            endpoint = new IPEndPoint(new IPAddress(addressBytes), port);
            return true;
        }

        if (family == 0x02 && attribute.Length >= 20)
        {
            var addressBytes = attribute.Slice(4, 16).ToArray();
            if (xor)
            {
                var cookieBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)MagicCookie));
                for (var i = 0; i < 4; i++)
                {
                    addressBytes[i] ^= cookieBytes[i];
                }
                for (var i = 0; i < 12; i++)
                {
                    addressBytes[i + 4] ^= transactionId[i];
                }
            }

            endpoint = new IPEndPoint(new IPAddress(addressBytes), port);
            return true;
        }

        return false;
    }

    private static int Align4(int value)
    {
        return (value + 3) & ~3;
    }
}
