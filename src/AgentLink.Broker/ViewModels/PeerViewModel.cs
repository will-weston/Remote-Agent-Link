using AgentLink.Core;

namespace AgentLink.Broker.ViewModels;

public sealed class PeerViewModel
{
    internal PeerViewModel(PeerConnectionSnapshot peer, IReadOnlyList<ConnectionInfo>? connections = null)
    {
        Id = peer.Id;
        Fingerprint = peer.Fingerprint;
        SessionId = peer.SessionId;
        IsIncoming = peer.Direction == PeerDirection.IncomingAgent;
        DisplayName = FriendlyName(peer.DisplayName);
        IsReady = peer.Status == PeerConnectionStatus.Connected;
        Status = IsReady ? "Connected over RDP" : "Connecting…";
        StatusGlyph = IsReady ? "\uE73E" : "\uE946";
        Detail = peer.Detail ?? (IsReady
            ? "Uses the signed-in Windows/RDP session. Agent access is controlled on the target computer."
            : "Waiting for the RDP channel. No agent connection is implied.");
        Direction = $"{(peer.Direction == PeerDirection.RemoteDesktop ? "Remote computer" : "Computer connected to this session")} · " +
            (peer.SessionId > 0 ? $"Windows session {peer.SessionId}" : "Windows session unknown");
        ConnectedSince = $"{(IsReady ? "Channel opened" : "Connection attempt started")} {peer.ConnectedAt.ToLocalTime():g}";
        McpConnectionId = McpConfiguration.ResolveOutgoingConnectionId(peer, connections ?? Array.Empty<ConnectionInfo>());
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Fingerprint { get; }
    public int SessionId { get; }
    public bool IsIncoming { get; }
    public string? McpConnectionId { get; }
    public bool CanCopyMcpConfiguration => IsReady && (IsIncoming || McpConnectionId is not null);
    public string McpActionLabel => IsIncoming ? "Copy MCP config for this desktop" : "Copy MCP configuration";
    public string McpDetails => IsIncoming ? "An MCP client on this desktop uses --current-session." :
        McpConnectionId is { } id ? $"MCP connection ID:\n{id}" :
        "No unique ready MCP target is currently available. Use rdp_list_connections after the connection is ready.";
    public string Status { get; }
    public string StatusGlyph { get; }
    public string Detail { get; }
    public string Direction { get; }
    public string ConnectedSince { get; }
    public bool IsReady { get; }

    internal static string FriendlyName(string name)
    {
        var safe = new string(name.Where(character => !char.IsControl(character) &&
            char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format).Take(120).ToArray()).Trim();
        return safe.Length == 0 ? "Windows device" : safe;
    }
}
