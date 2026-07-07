using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace LanBridge.Common.Configuration;

public static class ConfigLoader
{
    public static T? LoadConfig<T>(string[] args, JsonTypeInfo<T> typeInfo) where T : class
    {
        var configPath = CommandLineParser.FindOptionValue(args, "--config", "-c");
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return null;
        }

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize(json, typeInfo);
    }
}
