using System.Threading.Channels;
using AgentLink.Core;

namespace AgentLink.Broker;

internal sealed class PendingConsent(ConsentRequest request, string actorId)
{
    public ConsentRequest Request { get; } = request;
    public string ActorId { get; } = actorId;
    public bool Revoked { get; set; }
    public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// Kept under the existing name for desktop adapters. Authorization comes from
// authenticated transport and the local device preference, never an approval queue.
internal sealed class HumanConsent : IConsentService
{
    private readonly object _gate = new();
    private readonly Channel<PendingConsent> _queue = Channel.CreateBounded<PendingConsent>(1);
    private CancellationTokenSource _desktopEpoch = new();
    private CancellationTokenSource _deviceEpoch = new();
    private bool _isConnected;
    private bool _enabled = true;
    private bool _prompting;
    private string? _desktopOwner;

    public bool IsConnected { get { lock (_gate) return _isConnected; } }
    public bool DeviceAccessEnabled { get { lock (_gate) return _enabled; } }
    public bool DesktopAccessEnabled => DeviceAccessEnabled;
    public bool DesktopAllowed { get { lock (_gate) return _enabled && _isConnected && !_prompting &&
        _desktopOwner is not null && _desktopOwner == OperationContext.ActorId.Value; } }
    public bool ObservationAllowed { get { lock (_gate) return _enabled && _isConnected && !_prompting &&
        OperationContext.ActorId.Value is not null; } }
    public bool HasDesktopLease { get { lock (_gate) return _desktopOwner is not null; } }
    public ChannelReader<PendingConsent> Requests => _queue.Reader;
    public event Action? Changed;

    public void SetConnected(bool connected)
    {
        if (SetConnectionState(connected)) NotifyChanged();
    }

    internal bool SetConnectionState(bool connected)
    {
        lock (_gate)
        {
            if (_isConnected == connected) return false;
            _isConnected = connected;
            RevokeDesktopLocked();
            return true;
        }
    }

    public void SetPrompting(bool prompting)
    {
        lock (_gate) _prompting = prompting;
        NotifyChanged();
    }

    public void RevokeDesktop()
    {
        lock (_gate) RevokeDesktopLocked();
        NotifyChanged();
    }

    internal void SetDesktopAccessEnabled(bool enabled)
    {
        if (SetDesktopAccessState(enabled)) NotifyChanged();
    }

    internal bool SetDesktopAccessState(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled && enabled) return false;
            _enabled = enabled;
            if (!enabled)
            {
                RevokeDesktopLocked();
                AdvanceEpoch(ref _deviceEpoch);
            }
            return true;
        }
    }

    public CancellationToken DeviceAccessLifetime
    {
        get { lock (_gate) { RequireEnabled(); return _deviceEpoch.Token; } }
    }

    internal CancellationToken DesktopAccessLifetime
    {
        get { lock (_gate) { RequireEnabled(); return _desktopEpoch.Token; } }
    }

    private void RequireEnabled()
    {
        if (!_enabled)
            throw new AgentLinkException("device_access_disabled",
                "Allow agents to use this device is turned off. A local user can turn it on in Remote Agent Link.");
    }

    private void RevokeDesktopLocked()
    {
        _desktopOwner = null;
        AdvanceEpoch(ref _desktopEpoch);
    }

    private static void AdvanceEpoch(ref CancellationTokenSource epoch)
    {
        var previous = epoch;
        epoch = new();
        try { previous.Cancel(); }
        catch (Exception exception) { BrokerDiagnostics.ObserverFailed(BrokerObserver.ConsentChanged, exception); }
        finally { previous.Dispose(); }
    }

    public void ReleaseDesktop()
    {
        lock (_gate)
        {
            if (_desktopOwner is not null && _desktopOwner != OperationContext.ActorId.Value)
                throw new AgentLinkException("not_control_owner", "Only the owning agent or the local Stop control can release this desktop lease.");
            RevokeDesktopLocked();
        }
        NotifyChanged();
    }

    public Task<bool> RequestAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireEnabled();
            if (!_isConnected)
                throw new AgentLinkException("disconnected", "An authenticated session is required.");
            if (request.Capability is "observe" or "desktop-control")
            {
                if (_prompting) return Task.FromResult(false);
                var actor = OperationContext.ActorId.Value ??
                    throw new AgentLinkException("invalid_actor", "An authenticated agent identity is required.");
                if (request.Capability == "desktop-control")
                {
                    if (_desktopOwner is not null && _desktopOwner != actor)
                        throw new AgentLinkException("desktop_busy", "Another agent owns desktop control. It must release control before another agent can acquire it.");
                    _desktopOwner = actor;
                }
            }
        }
        NotifyChanged();
        return Task.FromResult(true);
    }

    internal void NotifyChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception exception) { BrokerDiagnostics.ObserverFailed(BrokerObserver.ConsentChanged, exception); }
        }
    }
}
