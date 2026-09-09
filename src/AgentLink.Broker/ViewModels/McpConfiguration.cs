using System.Text.Json;
using AgentLink.Core;

namespace AgentLink.Broker.ViewModels;

internal enum McpTarget { Discover, CurrentSession, FixedConnection }

internal static class McpConfiguration
{
    internal static string? ResolveOutgoingConnectionId(PeerConnectionSnapshot peer, IReadOnlyList<ConnectionInfo> connections)
    {
        if (peer.Direction != PeerDirection.RemoteDesktop || peer.Status != PeerConnectionStatus.Connected ||
            !peer.TrustedLocally || !peer.TrustedRemotely || peer.SessionId <= 0 ||
            peer.Fingerprint.Length != 64 || !peer.Fingerprint.All(Uri.IsHexDigit))
            return null;
        var matches = connections.Where(connection => connection.RemoteSessionId == peer.SessionId &&
            string.Equals(connection.PeerFingerprint, peer.Fingerprint, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 && !string.IsNullOrWhiteSpace(matches[0].ConnectionId) ? matches[0].ConnectionId : null;
    }

    internal static string Create(McpTarget target, string? connectionId = null)
    {
        string[] arguments = target switch
        {
            McpTarget.Discover => [],
            McpTarget.CurrentSession => ["--current-session"],
            McpTarget.FixedConnection when !string.IsNullOrWhiteSpace(connectionId) => ["--connection", connectionId],
            McpTarget.FixedConnection => throw new ArgumentException("Use the exact connection ID returned by rdp_list_connections."),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        return JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["remote-agent-link"] = new { type = "local", command = "ralink-mcp.exe", args = arguments, tools = new[] { "*" } }
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
