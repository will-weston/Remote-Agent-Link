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

internal sealed class HumanConsent : IConsentService
{
    private readonly object _gate = new();
    private readonly Channel<PendingConsent> _queue = Channel.CreateBounded<PendingConsent>(16);
    private TaskCompletionSource _connected = NewSignal();
    private TaskCompletionSource _desktopRevoked = NewSignal();
    private CancellationTokenSource _desktopOperationsEpoch = new();
    private bool _isConnected;
    private bool _desktopAccessEnabled = true;
    private readonly HashSet<string> _observers = [];
    private readonly HashSet<PendingConsent> _pending = [];
    private string? _desktopOwner;
    private bool _prompting;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsConnected { get { lock (_gate) return _isConnected; } }
    public bool DesktopAccessEnabled { get { lock (_gate) return _desktopAccessEnabled; } }
    public bool DesktopAllowed { get { lock (_gate) return _desktopAccessEnabled && _isConnected && _desktopOwner is not null && _desktopOwner == OperationContext.ActorId.Value && !_prompting; } }
    public bool ObservationAllowed { get { lock (_gate) return _desktopAccessEnabled && _isConnected && !_prompting && OperationContext.ActorId.Value is { } actor && _observers.Contains(actor); } }
    public bool HasDesktopLease { get { lock (_gate) return _desktopOwner is not null; } }
    public ChannelReader<PendingConsent> Requests => _queue.Reader;
    public event Action? Changed;

    public void SetConnected(bool connected)
    {
        if (SetConnectionState(connected))
            NotifyChanged();
    }

    internal bool SetConnectionState(bool connected)
    {
        lock (_gate)
        {
            if (_isConnected == connected)
                return false;
            _isConnected = connected;
            _desktopOwner = null;
            _observers.Clear();
            if (!connected)
                AdvanceDesktopEpoch();
            RevokePending(_ => true);
            while (_queue.Reader.TryRead(out _)) { }
            if (connected)
                _connected.TrySetResult();
            else
                _connected = NewSignal();
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
        lock (_gate)
            RevokeDesktopLocked();
        NotifyChanged();
    }

    internal void SetDesktopAccessEnabled(bool enabled)
    {
        if (SetDesktopAccessState(enabled))
            NotifyChanged();
    }

    internal bool SetDesktopAccessState(bool enabled)
    {
        lock (_gate)
        {
            if (_desktopAccessEnabled == enabled && enabled)
                return false;
            _desktopAccessEnabled = enabled;
            if (!enabled)
                RevokeDesktopLocked();
            return true;
        }
    }

    internal CancellationToken DesktopAccessLifetime
    {
        get
        {
            lock (_gate)
            {
                if (!_desktopAccessEnabled)
                    throw DesktopAccessDisabled();
                return _desktopOperationsEpoch.Token;
            }
        }
    }

    private void RevokeDesktopLocked()
    {
        _desktopOwner = null;
        _observers.Clear();
        AdvanceDesktopEpoch();
        RevokePending(pending => IsDesktop(pending.Request.Capability));
        List<PendingConsent> retained = [];
        while (_queue.Reader.TryRead(out var pending))
            if (!IsDesktop(pending.Request.Capability))
                retained.Add(pending);
        foreach (var pending in retained)
            if (!_queue.Writer.TryWrite(pending))
                throw new InvalidOperationException("The non-desktop approval queue could not be preserved.");
    }

    private void AdvanceDesktopEpoch()
    {
        var previous = _desktopRevoked;
        _desktopRevoked = NewSignal();
        previous.TrySetResult();
        var operations = _desktopOperationsEpoch;
        _desktopOperationsEpoch = new();
        CancelDesktopEpoch(operations);
    }

    private static void CancelDesktopEpoch(CancellationTokenSource epoch)
    {
        try { epoch.Cancel(); }
        catch (Exception exception) { BrokerDiagnostics.ObserverFailed(BrokerObserver.ConsentChanged, exception); }
        finally { epoch.Dispose(); }
    }

    private static AgentLinkException DesktopAccessDisabled() =>
        new("desktop_access_disabled", "Desktop access is turned off on this device. A local human must turn it on before new observation or control approval can be requested.");

    public void ReleaseDesktop()
    {
        var actor = OperationContext.ActorId.Value;
        lock (_gate)
        {
            if (_desktopOwner is not null && _desktopOwner != actor)
                throw new AgentLinkException("not_control_owner", "Only the owning agent or the human Stop control can release this desktop lease.");
            _desktopOwner = null;
            if (actor is not null)
                _observers.Remove(actor);
            RevokePending(pending => pending.ActorId == actor && IsDesktop(pending.Request.Capability));
        }
        NotifyChanged();
    }

    private static bool IsDesktop(string capability) => capability is "observe" or "desktop-control";

    private void RevokePending(Func<PendingConsent, bool> predicate)
    {
        foreach (var pending in _pending.Where(predicate))
        {
            pending.Revoked = true;
            pending.Answer.TrySetResult(false);
        }
    }

    public async Task<bool> RequestAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        var actor = OperationContext.ActorId.Value ?? "broker-job";
        var desktop = IsDesktop(request.Capability);
        Task connection;
        Task? desktopRevoked;
        lock (_gate)
        {
            if (desktop && !_desktopAccessEnabled)
                throw DesktopAccessDisabled();
            if (request.Capability == "desktop-control" && _desktopOwner is not null && _desktopOwner != actor)
                throw new AgentLinkException("desktop_busy", "Another agent owns desktop control. The human must stop that lease before granting another.");
            if (_isConnected && !_prompting &&
                ((request.Capability == "observe" && _observers.Contains(actor)) ||
                 (request.Capability == "desktop-control" && _desktopOwner == actor)))
                return true;
            connection = _connected.Task;
            desktopRevoked = desktop ? _desktopRevoked.Task : null;
        }
        if (desktopRevoked is not null &&
            await Task.WhenAny(connection, desktopRevoked).WaitAsync(cancellationToken) == desktopRevoked)
            return false;
        await connection.WaitAsync(cancellationToken);
        var pending = new PendingConsent(request, actor);
        using var registration = cancellationToken.Register(() => pending.Answer.TrySetCanceled(cancellationToken));
        lock (_gate)
        {
            if (!_isConnected || (desktop && (!_desktopAccessEnabled || desktopRevoked != _desktopRevoked.Task)))
                return false;
            _pending.Add(pending);
            if (!_queue.Writer.TryWrite(pending))
            {
                _pending.Remove(pending);
                throw new AgentLinkException("approval_queue_full", "Too many pending human approvals. Wait for existing requests to finish.");
            }
        }
        try
        {
            var allowed = await pending.Answer.Task;
            if (allowed)
            {
                lock (_gate)
                {
                    if (!_isConnected || pending.Revoked ||
                        (desktop && (!_desktopAccessEnabled || desktopRevoked != _desktopRevoked.Task)))
                        return false;
                    if (request.Capability == "observe")
                    {
                        if (_observers.Count >= 64)
                            throw new AgentLinkException("too_many_observers", "Turn off desktop access to clear old observer grants.");
                        _observers.Add(actor);
                    }
                    else if (request.Capability == "desktop-control")
                    {
                        if (_desktopOwner is not null && _desktopOwner != actor)
                            throw new AgentLinkException("desktop_busy", "Another agent acquired desktop control while approval was pending.");
                        _desktopOwner = actor;
                        _observers.Add(actor);
                    }
                }
            }
            return allowed;
        }
        finally
        {
            lock (_gate) _pending.Remove(pending);
            NotifyChanged();
        }
    }

    internal void NotifyChanged()
    {
        if (Changed is not { } handlers)
            return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception exception) { BrokerDiagnostics.ObserverFailed(BrokerObserver.ConsentChanged, exception); }
        }
    }
}
