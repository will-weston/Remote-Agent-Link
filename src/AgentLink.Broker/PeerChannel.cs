using System.Security.Cryptography.X509Certificates;
using AgentLink.Core;

namespace AgentLink.Broker;

internal sealed class PeerChannel : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private PeerConnectionSnapshot _snapshot;
    private EndpointHello? _hello;
    private bool _legacy;
    private bool _closed;
    private int _disposed;
    private RpcPeer? _peer;
    private X509Certificate2? _certificate;
    private DateTime _validFrom;
    private DateTime _validUntil;

    internal PeerChannel(PeerDirection direction, CancellationToken stop)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _token = _lifetime.Token;
        _snapshot = new(Guid.NewGuid().ToString("N"),
            direction == PeerDirection.RemoteDesktop ? "Remote desktop" : "Incoming agent",
            "", 0, direction, PeerConnectionStatus.Connecting, false, false, DateTimeOffset.UtcNow,
            "Establishing an encrypted channel over authenticated RDP.");
    }

    internal string Id => _snapshot.Id;
    internal CancellationToken Lifetime => _token;
    internal string? LogonId { get { lock (_gate) return _hello?.LogonId; } }
    internal PeerConnectionSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    internal bool Ready { get { lock (_gate) return ReadyLocked; } }
    private bool ReadyLocked => !_closed && !_token.IsCancellationRequested && DateTime.UtcNow >= _validFrom && DateTime.UtcNow < _validUntil &&
        _snapshot.TrustedLocally && _snapshot.TrustedRemotely && (_hello is not null || _legacy);
    internal bool Closed { get { lock (_gate) return _closed; } }
    internal RpcPeer? Peer { get { lock (_gate) return _peer; } }
    internal X509Certificate2 Certificate => _certificate ?? throw new InvalidOperationException("TLS has not completed.");

    internal void Authenticate(X509Certificate certificate, bool trusted)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (_certificate is not null)
                throw new AgentLinkException("identity_changed", "A channel cannot replace its authenticated certificate.");
            _certificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            _validFrom = _certificate.NotBefore.ToUniversalTime();
            _validUntil = _certificate.NotAfter.ToUniversalTime();
            _snapshot = _snapshot with { Fingerprint = EndpointIdentity.FingerprintOf(certificate), TrustedLocally = trusted };
            UpdateStatus();
        }
    }

    internal void Attach(RpcPeer peer)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            _peer = peer;
        }
    }

    internal void ReconcileLocalTrust(bool trusted)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (_snapshot.TrustedLocally == trusted)
                return;
            throw new AgentLinkException("identity_changed", "Channel authentication changed during the handshake.");
        }
    }

    internal void AcceptHello(EndpointHello? hello)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            var legacy = hello?.SupportsPendingPairing != true;
            if ((_hello is not null || _legacy) && (_hello != hello || _legacy != legacy))
                throw new AgentLinkException("identity_changed", "Peer metadata changed during this channel. Reconnect before continuing.");
            _hello = hello;
            _legacy = legacy;
            _snapshot = _snapshot with
            {
                DisplayName = hello?.DisplayName ?? (_snapshot.Direction == PeerDirection.RemoteDesktop ? "Remote desktop" : "Incoming computer"),
                SessionId = hello?.SessionId ?? 0,
                TrustedRemotely = legacy || hello!.TrustedPeer == true
            };
            UpdateStatus();
        }
    }

    internal void RequireReady()
    {
        lock (_gate)
            if (!ReadyLocked)
                throw new AgentLinkException("channel_not_ready", "The authenticated channel is not ready. Update both endpoints and reconnect.");
    }

    internal void WithReady(Action action)
    {
        lock (_gate)
        {
            RequireReady();
            action();
        }
    }

    internal bool Close()
    {
        lock (_gate)
        {
            if (_closed)
                return false;
            _closed = true;
            _ = _lifetime.CancelAsync();
            return true;
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed || _token.IsCancellationRequested)
            throw new OperationCanceledException("The channel has closed.", _token);
    }

    private void UpdateStatus()
    {
        _snapshot = _snapshot with
        {
            Status = ReadyLocked ? PeerConnectionStatus.Connected : PeerConnectionStatus.Connecting,
            Detail = ReadyLocked
                ? "Authenticated RDP channel. Each device controls its own agent access."
                : "Waiting for authenticated endpoint metadata. Update both endpoints if this persists."
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Close();
        _certificate?.Dispose();
        _lifetime.Dispose();
    }
}
