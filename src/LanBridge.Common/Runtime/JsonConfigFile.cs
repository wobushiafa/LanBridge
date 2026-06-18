using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace LanBridge.Common.Runtime;

public static class JsonConfigFile
{
    public static T? Load<T>(string[] args, JsonTypeInfo<T> typeInfo, params string[] optionNames)
    {
        var configPath = CommandLineArguments.FindOptionValue(args, optionNames);
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return default;
        }

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize(json, typeInfo);
    }
}
