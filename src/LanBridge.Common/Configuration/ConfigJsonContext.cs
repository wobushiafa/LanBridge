using System.Text.Json.Serialization;

namespace LanBridge.Common.Configuration;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true
)]
[JsonSerializable(typeof(ServerConfig))]
[JsonSerializable(typeof(PeerConfig))]
[JsonSerializable(typeof(PeerBaseConfig))]
[JsonSerializable(typeof(ClientConfig))]
[JsonSerializable(typeof(TargetEndpoint))]
[JsonSerializable(typeof(AllowedSubnet))]
[JsonSerializable(typeof(TunnelMapping))]
public partial class ConfigJsonContext : JsonSerializerContext
{
}