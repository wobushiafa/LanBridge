using System.Text.Json.Nodes;

namespace LanBridge.Common.Configuration;

public static class ConfigJsonCompatibility
{
    public static JsonObject NormalizeExtranet(JsonObject root)
    {
        Promote(root, "identity", "nodeId", "nodeId");
        Promote(root, "signaling", "host", "signalingServerHost");
        Promote(root, "signaling", "port", "signalingServerPort");
        Promote(root, "stun", "host", "stunServerHost");
        Promote(root, "stun", "port", "stunServerPort");
        Promote(root, "stun", "alternatePort", "stunAlternateServerPort");
        Promote(root, "connection", "targetNodeId", "targetNodeId");
        Promote(root, "connection", "holePunchTimeoutMs", "holePunchTimeoutMs");
        Promote(root, "connection", "enableRelayFallback", "enableRelayFallback");
        Promote(root, "proxy", "localPort", "localProxyPort");
        Promote(root, "transport", "udpPort", "udpPort");
        Promote(root, "transport", "verbose", "verbose");
        Promote(root, "transport", "enableKcpCongestionControl", "enableKcpCongestionControl");
        return root;
    }

    public static JsonObject NormalizeIntranet(JsonObject root)
    {
        Promote(root, "identity", "nodeId", "nodeId");
        Promote(root, "identity", "token", "token");
        Promote(root, "signaling", "host", "signalingServerHost");
        Promote(root, "signaling", "port", "signalingServerPort");
        Promote(root, "stun", "host", "stunServerHost");
        Promote(root, "stun", "port", "stunServerPort");
        Promote(root, "stun", "alternatePort", "stunAlternateServerPort");
        Promote(root, "target", "host", "targetSourceHost");
        Promote(root, "target", "port", "targetSourcePort");
        Promote(root, "transport", "udpPort", "udpPort");
        Promote(root, "transport", "verbose", "verbose");
        Promote(root, "transport", "enableKcpCongestionControl", "enableKcpCongestionControl");
        return root;
    }

    public static JsonObject NormalizeServer(JsonObject root)
    {
        Promote(root, "ports", "signalingPort", "signalingPort");
        Promote(root, "ports", "stunPort", "stunPort");
        Promote(root, "ports", "stunAlternatePort", "stunAlternatePort");
        Promote(root, "ports", "relayPort", "relayPort");
        Promote(root, "relay", "maxSessions", "maxRelaySessions");
        Promote(root, "relay", "idleTimeoutMs", "relayTimeoutMs");
        Promote(root, "security", "requireRegistrationToken", "requireRegistrationToken");
        Promote(root, "security", "registrationTokens", "registrationTokens");
        Promote(root, "metrics", "reportIntervalSeconds", "metricsReportIntervalSeconds");
        return root;
    }

    private static void Promote(JsonObject root, string sectionName, string nestedPropertyName, string flatPropertyName)
    {
        if (ContainsProperty(root, flatPropertyName))
        {
            return;
        }

        if (FindProperty(root, sectionName) is not JsonObject section)
        {
            return;
        }

        if (FindProperty(section, nestedPropertyName) is not JsonNode value)
        {
            return;
        }

        root[flatPropertyName] = value.DeepClone();
    }

    private static bool ContainsProperty(JsonObject root, string propertyName)
    {
        return root.Any(kvp => string.Equals(kvp.Key, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonNode? FindProperty(JsonObject root, string propertyName)
    {
        foreach (var kvp in root)
        {
            if (string.Equals(kvp.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }
}
