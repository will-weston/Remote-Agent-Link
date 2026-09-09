using AgentLink.Core;

namespace AgentLink.Broker.ViewModels;

public sealed class PeerViewModel
{
    internal PeerViewModel(PeerConnectionSnapshot peer, IReadOnlyList<ConnectionInfo>? connections = null)
    {
        Id = peer.Id;
        Fingerprint = peer.Fingerprint;
        CanReviewPairing = peer.Fingerprint.Length == 64 && peer.Fingerprint.All(Uri.IsHexDigit);
        SessionId = peer.SessionId;
        IsIncoming = peer.Direction == PeerDirection.IncomingAgent;
        DisplayName = FriendlyName(peer.DisplayName);
        IsReady = peer.Status == PeerConnectionStatus.Connected && peer.TrustedLocally && peer.TrustedRemotely && CanReviewPairing;
        NeedsVerification = !peer.TrustedLocally;
        Status = IsReady ? "Paired · channel connected" :
            peer.Status == PeerConnectionStatus.Connecting ? "Connecting…" : "Channel connected · approval required";
        StatusGlyph = IsReady ? "\uE73E" : "\uE946";
        Pairing = !CanReviewPairing ? "Waiting for the device’s certificate identity" : peer.TrustedLocally
            ? peer.TrustedRemotely ? "✓ Identity verified on both devices" : "✓ Verified here · verify this device on the other end"
            : "Identity not verified · compare fingerprints before pairing";
        Detail = peer.Detail ?? (peer.Status == PeerConnectionStatus.Connecting
            ? "Opening the channel. No tools can run until the connection and both identity checks are complete." : IsReady
            ? "Pairing is complete. Desktop and agent capabilities still need separate human permission."
            : "The encrypted channel is available for pairing only. Tools cannot run yet.");
        Direction = $"{(peer.Direction == PeerDirection.RemoteDesktop ? "Remote desktop" : "Incoming agent")} · " +
            (peer.SessionId > 0 ? $"Windows session {peer.SessionId}" : "Windows session unknown");
        ConnectedSince = $"{(peer.Status == PeerConnectionStatus.Connecting ? "Connection attempt started" : "Channel opened")} {peer.ConnectedAt.ToLocalTime():g}";
        ActionLabel = !CanReviewPairing ? "Waiting for device identity" : NeedsVerification ? "Verify device" : "Pairing details";
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
    public string Pairing { get; }
    public string Detail { get; }
    public string Direction { get; }
    public string ConnectedSince { get; }
    public string ActionLabel { get; }
    public bool CanReviewPairing { get; }
    public bool IsReady { get; }
    public bool NeedsVerification { get; }

    internal static string FriendlyName(string name)
    {
        var safe = new string(name.Where(character => !char.IsControl(character) &&
            char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format).Take(120).ToArray()).Trim();
        return safe.Length == 0 ? "Windows device" : safe;
    }
}
