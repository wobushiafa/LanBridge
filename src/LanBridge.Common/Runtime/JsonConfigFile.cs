using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace LanBridge.Common.Runtime;

public static class JsonConfigFile
{
    public static T? Load<T>(string[] args, JsonTypeInfo<T> typeInfo, params string[] optionNames)
    {
        return Load(args, typeInfo, normalize: null, optionNames);
    }

    public static T? Load<T>(
        string[] args,
        JsonTypeInfo<T> typeInfo,
        Func<JsonObject, JsonObject>? normalize,
        params string[] optionNames)
    {
        var configPath = CommandLineArguments.FindOptionValue(args, optionNames);
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return default;
        }

        var json = File.ReadAllText(configPath);
        if (normalize is null)
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }

        var node = JsonNode.Parse(
            json,
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

        if (node is not JsonObject root)
        {
            throw new InvalidDataException($"Configuration file '{configPath}' must contain a JSON object.");
        }

        var normalized = normalize(root);
        json = normalized.ToJsonString();
        return JsonSerializer.Deserialize(json, typeInfo);
    }
}
