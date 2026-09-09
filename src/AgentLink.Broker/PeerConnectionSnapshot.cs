namespace AgentLink.Broker;

internal enum PeerDirection
{
    RemoteDesktop,
    IncomingAgent
}

internal enum PeerConnectionStatus
{
    Connecting,
    AwaitingApproval,
    Connected
}

internal sealed record PeerConnectionSnapshot(
    string Id,
    string DisplayName,
    string Fingerprint,
    int SessionId,
    PeerDirection Direction,
    PeerConnectionStatus Status,
    bool TrustedLocally,
    bool TrustedRemotely,
    DateTimeOffset ConnectedAt,
    string? Detail = null);
