namespace AgentLink.Broker;

internal sealed record AgentConnectionSnapshot(string Id, string ComputerName, bool IsLocal, DateTimeOffset LastSeenAt);

internal sealed class AgentPresenceRegistry(TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(45);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (AgentConnectionSnapshot Snapshot, string? Channel)> _agents = new(StringComparer.Ordinal);

    internal void Update(string id, string computerName, string? channel, bool connected)
    {
        lock (_gate)
        {
            Expire();
            if (!connected) { _agents.Remove(id); return; }
            if (_agents.Count >= 256 && !_agents.ContainsKey(id))
                throw new AgentLink.Core.AgentLinkException("too_many_agents", "Too many agents are registered on this device.");
            _agents[id] = (new(id, computerName, channel is null, _clock.GetUtcNow()), channel);
        }
    }

    internal void Disconnect(string channel)
    {
        lock (_gate)
            foreach (var id in _agents.Where(pair => pair.Value.Channel == channel).Select(pair => pair.Key).ToArray())
                _agents.Remove(id);
    }

    internal IReadOnlyList<AgentConnectionSnapshot> Snapshot()
    {
        lock (_gate)
        {
            Expire();
            return _agents.Values.Select(value => value.Snapshot).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        }
    }

    private void Expire()
    {
        var cutoff = _clock.GetUtcNow() - Lifetime;
        foreach (var id in _agents.Where(pair => pair.Value.Snapshot.LastSeenAt <= cutoff).Select(pair => pair.Key).ToArray())
            _agents.Remove(id);
    }
}

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
