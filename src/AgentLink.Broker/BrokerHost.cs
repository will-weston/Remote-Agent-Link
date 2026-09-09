using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using AgentLink.Broker.Shell;
using AgentLink.Core;
using AgentLink.Copilot;
using AgentLink.Desktop;

namespace AgentLink.Broker;

internal sealed record BrokerTransportOptions(string ControlPipe, string TransportPipe, Func<Stream> OpenRemoteTransport);

internal sealed class BrokerHost : IAsyncDisposable
{
    private sealed record Link(ConnectionInfo Info, PeerChannel Channel, RpcPeer Peer);
    private readonly EndpointIdentity _identity;
    private readonly HumanConsent _consent;
    private readonly DesktopAccessSettings _desktopAccessSettings;
    private readonly object _desktopAccessGate = new();
    private readonly DesktopOperations _desktop;
    private readonly CopilotOperations _copilot;
    private readonly Dictionary<string, IOperationHandler> _operations;
    private readonly ConcurrentDictionary<string, Link> _links = new();
    private readonly ConcurrentDictionary<string, PeerChannel> _channels = new();
    private readonly ConcurrentDictionary<Guid, Task> _sessions = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connectionSlots = new(16, 16);
    private readonly SemaphoreSlim _channelSlots = new(17, 17);
    private readonly bool _localRole;
    private readonly bool _remoteRole;
    private readonly BrokerTransportOptions _transport;
    private Task[] _listeners = [];
    private Task[] _listenerObservers = [];
    private readonly object _remoteGate = new();
    private PeerChannel? _remotePeer;
    private CancellationToken _desktopConnection = new(true);
    private int _started;
    private int _running;
    private int _disposed;
    private string? _lastTransportError;

    public BrokerHost(EndpointIdentity identity, HumanConsent consent, string dataDirectory, string role,
        BrokerTransportOptions? transport = null)
    {
        if (role is not ("local" or "remote" or "both"))
            throw new ArgumentException("The broker role must be local, remote or both.", nameof(role));
        _identity = identity;
        _consent = consent;
        _consent.SetDesktopAccessState(false);
        _desktopAccessSettings = new DesktopAccessSettings(dataDirectory);
        _consent.SetDesktopAccessState(_desktopAccessSettings.Read());
        _localRole = role is "local" or "both";
        _remoteRole = role is "remote" or "both";
        _transport = transport ?? new(PipeNames.Control, PipeNames.Transport, () => new WtsStream());
        _desktop = new DesktopOperations(consent);
        _copilot = new CopilotOperations(consent, dataDirectory);
        _operations = new[] { (IOperationHandler)_desktop, _copilot }
            .SelectMany(handler => handler.Tools.Select(tool => (tool.Name, handler)))
            .ToDictionary(entry => entry.Name, entry => entry.handler, StringComparer.Ordinal);
        _identity.TrustChanged += RevokeConnections;
    }

    public event Action<string>? Log;
    public event Action? StatusChanged;
    public event Action? ShowRequested;
    public bool IsRunning => Volatile.Read(ref _running) != 0 && !_stop.IsCancellationRequested;
    public string? LastTransportError => Volatile.Read(ref _lastTransportError);
    public IReadOnlyList<PeerConnectionSnapshot> PeerConnections => _channels.Values
        .Where(channel => !channel.Closed).Select(channel => channel.Snapshot).OrderBy(peer => peer.ConnectedAt).ToArray();
    public IReadOnlyList<ConnectionInfo> Connections
    {
        get
        {
            _identity.RefreshTrust();
            var pins = _identity.TrustedFingerprints.ToHashSet(StringComparer.Ordinal);
            return _links.Values.Where(link => pins.Contains(link.Info.PeerFingerprint) && link.Channel.Ready).Select(link => link.Info).ToArray();
        }
    }
    public IReadOnlyList<AgentTask> ListLocalTasks(bool activeOnly, string? beforeTaskId = null) =>
        _copilot.ListLocalTasks(activeOnly: activeOnly, beforeTaskId: beforeTaskId);
    public AgentTask GetLocalTask(string taskId) => _copilot.GetLocalTask(taskId);
    public AgentTask CancelLocalTask(string taskId) => _copilot.CancelLocalTask(taskId);

    private static string LogonId => WindowsLogon.Current;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        Volatile.Write(ref _running, 1);
        List<Task> listeners = [PipeLoopAsync(_transport.ControlPipe, HandleLocalPipeAsync)];
        if (_localRole)
            listeners.Add(PipeLoopAsync(_transport.TransportPipe, HandleTransportPipeAsync));
        if (_remoteRole)
            listeners.Add(RemoteLoopAsync());
        _listeners = listeners.ToArray();
        _listenerObservers = _listeners.Select(ObserveListenerAsync).ToArray();
        Raise(StatusChanged);
    }

    private async Task ObserveListenerAsync(Task listener)
    {
        try { await listener; }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Volatile.Write(ref _running, 0);
            Volatile.Write(ref _lastTransportError, "The broker listener stopped. Close duplicate brokers, check the installation, then restart Remote Agent Link.");
            Raise(StatusChanged);
            WriteLog($"Listener failed: {exception.GetType().Name}");
            await _stop.CancelAsync();
            RevokeConnections();
        }
    }

    private async Task PipeLoopAsync(string name, Func<NamedPipeServerStream, Task> handler)
    {
        var firstInstance = true;
        while (!_stop.IsCancellationRequested)
        {
            await _connectionSlots.WaitAsync(_stop.Token);
            NamedPipeServerStream? pipe = null;
            var handedOff = false;
            try
            {
                pipe = PipeSecurity.CreateServer(name, firstInstance);
                firstInstance = false;
                await pipe.WaitForConnectionAsync(_stop.Token);
                var id = Guid.NewGuid();
                var accepted = pipe;
                var task = RunPipeAsync(accepted, handler);
                _sessions[id] = task;
                _ = task.ContinueWith(_ => _sessions.TryRemove(id, out var ignored),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                handedOff = true;
            }
            finally
            {
                if (!handedOff)
                {
                    pipe?.Dispose();
                    _connectionSlots.Release();
                }
            }
        }
    }

    private async Task RunPipeAsync(NamedPipeServerStream pipe, Func<NamedPipeServerStream, Task> handler)
    {
        try
        {
            PipeSecurity.VerifyLocalSession(pipe);
            await handler(pipe);
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException or AgentLinkException or OperationCanceledException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            if (!_stop.IsCancellationRequested)
                SetTransportError(TransportError(exception));
            WriteLog($"Connection closed: {exception.GetType().Name}");
        }
        finally
        {
            pipe.Dispose();
            _connectionSlots.Release();
        }
    }

    private async Task HandleLocalPipeAsync(NamedPipeServerStream pipe)
    {
        var processId = PipeSecurity.VerifyLocalSession(pipe);
        using var client = Process.GetProcessById((int)processId);
        var clientIdentity = $"{processId}:{client.StartTime.ToUniversalTime().Ticks}";
        await using var peer = new RpcPeer(pipe, (request, ct) => HandleLocalRequestAsync(request, clientIdentity, ct));
        await peer.Completion.WaitAsync(_stop.Token);
    }

    private Task HandleTransportPipeAsync(NamedPipeServerStream pipe) => HandleOutgoingChannelAsync(pipe);

    internal async Task HandleOutgoingChannelAsync(Stream transport)
    {
        using var channel = AddChannel(PeerDirection.RemoteDesktop);
        Link? link = null;
        try
        {
            using var tls = new SslStream(transport, false, _identity.ValidateChannelPeer);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(channel.Lifetime);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "RemoteAgentLink",
                ClientCertificates = new X509CertificateCollection { _identity.Certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, handshake.Token);
            AuthenticateChannel(tls, channel);
            await using var peer = new RpcPeer(new ChannelFrameStream(tls, () => channel.Ready));
            channel.Attach(peer);
            var response = await peer.CallAsync(new("protocol.hello",
                Json.Element(ChannelProtocol.LocalHello(channel.Snapshot.TrustedLocally, true))), handshake.Token);
            var hello = ChannelProtocol.ReadHelloResult(response);
            _identity.AdmitChannel(channel.Certificate, trusted =>
            {
                channel.ReconcileLocalTrust(trusted);
                channel.AcceptHello(hello);
                if (channel.Ready)
                {
                    var fingerprint = channel.Snapshot.Fingerprint;
                    var id = $"{fingerprint}:{hello.LogonId}";
                    link = new(new(id, fingerprint, hello.SessionId, hello.LogonId,
                        Guid.NewGuid().ToString("N"), hello.DisplayName), channel, peer);
                    if (!_links.TryAdd(id, link))
                        throw new AgentLinkException("duplicate_connection", "An active link already owns this remote logon.");
                }
            });
            SetTransportError(null);
            Raise(StatusChanged);
            await MonitorConnectionAsync(channel, true, () => 0);
        }
        finally
        {
            CloseChannel(channel);
            if (link is not null)
                _links.TryRemove(new KeyValuePair<string, Link>(link.Info.ConnectionId, link));
            RemoveChannel(channel);
        }
    }

    internal async Task HandleIncomingChannelAsync(Stream transport)
    {
        using var channel = AddChannel(PeerDirection.IncomingAgent);
        try
        {
            using var tls = new SslStream(transport, false, _identity.ValidateChannelPeer);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(channel.Lifetime);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _identity.Certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, handshake.Token);
            AuthenticateChannel(tls, channel);
            var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helloReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastRequest = Stopwatch.GetTimestamp();
            await using var peer = new RpcPeer(new ChannelFrameStream(tls, () => channel.Ready), async (request, ct) =>
            {
                await attached.Task.WaitAsync(ct);
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, channel.Lifetime);
                if (request.Method is "protocol.hello" or "protocol.ping")
                {
                    try
                    {
                        ChannelProtocol.ValidateMetadataRequest(request);
                        OperationResult? result = null;
                        _identity.AdmitChannel(channel.Certificate, trusted =>
                        {
                            operation.Token.ThrowIfCancellationRequested();
                            channel.ReconcileLocalTrust(trusted);
                            if (request.Method == "protocol.hello")
                            {
                                var hello = ChannelProtocol.IsEmpty(request.Arguments) ? null : ChannelProtocol.ReadHello(request.Arguments);
                                channel.AcceptHello(hello);
                                ActivateIncoming(channel);
                                result = new(Data: Json.Element(ChannelProtocol.LocalHello(channel.Snapshot.TrustedLocally,
                                    hello?.SupportsPendingPairing == true)));
                                helloReceived.TrySetResult();
                            }
                            else
                                result = new(Data: Json.Element(new { protocolVersion = Wire.ProtocolVersion }));
                        });
                        if (request.Method == "protocol.hello")
                            _consent.NotifyChanged();
                        operation.Token.ThrowIfCancellationRequested();
                        Interlocked.Exchange(ref lastRequest, Stopwatch.GetTimestamp());
                        Raise(StatusChanged);
                        return result!;
                    }
                    catch (AgentLinkException)
                    {
                        CloseChannel(channel);
                        throw;
                    }
                }
                Task<OperationResult>? invocation = null;
                var fingerprint = channel.Snapshot.Fingerprint;
                _identity.WithTrustedFingerprint(fingerprint, () =>
                {
                    channel.RequireReady();
                    operation.Token.ThrowIfCancellationRequested();
                    if (request.Method != "tools.list" && request.ActorId is not { Length: > 0 and <= 256 })
                        throw new AgentLinkException("invalid_actor", "A bounded agent identity is required.");
                    Interlocked.Exchange(ref lastRequest, Stopwatch.GetTimestamp());
                    invocation = DispatchAsync(request with { ActorId = $"remote:{fingerprint}:{request.ActorId}" },
                        operation.Token, channel.Lifetime, $"remote:{fingerprint}");
                });
                return await invocation!;
            });
            try
            {
                channel.Attach(peer);
                attached.TrySetResult();
                await Task.WhenAny(helloReceived.Task, peer.Completion).WaitAsync(handshake.Token);
                if (!helloReceived.Task.IsCompletedSuccessfully)
                    throw new AgentLinkException("protocol_mismatch", "The peer disconnected before exchanging endpoint metadata.");
                SetTransportError(null);
                Raise(StatusChanged);
                await MonitorConnectionAsync(channel, false, () => Interlocked.Read(ref lastRequest));
            }
            finally
            {
                attached.TrySetCanceled();
                CloseChannel(channel);
            }
        }
        finally { RemoveChannel(channel); }
    }

    private async Task RemoteLoopAsync()
    {
        var waitingLogged = false;
        while (!_stop.IsCancellationRequested)
        {
            var opened = false;
            try
            {
                if (Process.GetCurrentProcess().SessionId == 0)
                    throw new AgentLinkException("session_zero", "The remote broker cannot run in Session 0.");
                using var transport = _transport.OpenRemoteTransport();
                opened = true;
                waitingLogged = false;
                await HandleIncomingChannelAsync(transport);
            }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or AuthenticationException or AgentLinkException or OperationCanceledException)
            {
                if (_stop.IsCancellationRequested)
                    break;
                if (!waitingLogged)
                    WriteLog($"Waiting for an RDP channel: {exception.GetType().Name}");
                if (opened || _channels.IsEmpty)
                    SetTransportError(TransportError(exception));
                waitingLogged = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token);
        }
    }

    private async Task MonitorConnectionAsync(PeerChannel channel, bool sendHeartbeat, Func<long> lastRequest)
    {
        var peer = channel.Peer!;
        using var monitor = CancellationTokenSource.CreateLinkedTokenSource(channel.Lifetime);
        try
        {
            while (!peer.Completion.IsCompleted)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(5), monitor.Token);
                if (await Task.WhenAny(peer.Completion, delay) == peer.Completion)
                    return;
                await delay;
                _identity.RefreshTrust();
                monitor.Token.ThrowIfCancellationRequested();
                if (!EndpointIdentity.IsCertificateCurrent(channel.Certificate, DateTime.UtcNow))
                    throw new AuthenticationException("The channel certificate expired.");
                if (sendHeartbeat)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(monitor.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    ChannelProtocol.ValidateHeartbeat(await peer.CallAsync(new("protocol.ping", Json.Element(new { })), deadline.Token));
                }
                else if (Stopwatch.GetElapsedTime(lastRequest()) > TimeSpan.FromSeconds(20))
                    throw new IOException("RDP heartbeat expired; desktop grants are revoked.");
            }
        }
        finally { await monitor.CancelAsync(); }
    }

    private async Task<OperationResult> HandleLocalRequestAsync(OperationRequest request, string clientIdentity, CancellationToken cancellationToken)
    {
        if (request.Method == "ui.show")
        {
            ChannelProtocol.RequireEmpty(request.Arguments);
            if (!Raise(ShowRequested, BrokerObserver.ShowRequested))
                throw new AgentLinkException("ui_unavailable", "The existing Remote Agent Link window could not be requested. Restart the broker if its UI is unavailable.");
            return new(Text: "The existing Remote Agent Link window was requested.");
        }
        if (request.Method == "list")
            return new(Data: Json.Element(Connections));
        if (request.Method == "invoke")
        {
            var route = Arguments.Read<RoutedOperation>(request.Arguments);
            if (!Guid.TryParseExact(route.Request.ActorId, "N", out _))
                throw new AgentLinkException("invalid_actor", "A valid MCP instance identity is required.");
            var ownedRequest = route.Request with { ActorId = $"{clientIdentity}:{route.Request.ActorId}" };
            if (route.CurrentSession)
            {
                if (!_remoteRole)
                    throw new AgentLinkException("role_disabled", "Own-session operations require the remote broker role.");
                return await InvokeCurrentAsync(ownedRequest with { ActorId = $"local:{ownedRequest.ActorId}" }, cancellationToken);
            }
            return await ForwardAsync(GetLink(route.ConnectionId), ownedRequest, cancellationToken);
        }
        var target = Arguments.Read<TargetRequest>(request.Arguments);
        if (request.Method == "get")
        {
            if (target.CurrentSession)
                return new(Data: Json.Element(new { currentSession = true, connected = _consent.IsConnected, sessionId = Process.GetCurrentProcess().SessionId }));
            return new(Data: Json.Element(GetLink(target.ConnectionId).Info));
        }
        if (request.Method == "describe")
        {
            if (target.CurrentSession)
            {
                if (!_remoteRole)
                    throw new AgentLinkException("role_disabled", "Own-session operations require the remote broker role.");
                return await InvokeCurrentAsync(new("tools.list", Json.Element(new { })), cancellationToken);
            }
            return await ForwardAsync(GetLink(target.ConnectionId), new("tools.list", Json.Element(new { }),
                $"catalog:{clientIdentity}"), cancellationToken);
        }
        throw new AgentLinkException("unknown_operation", "Unknown local broker operation.");
    }

    private Link GetLink(string? id)
    {
        if (id is null || !_links.TryGetValue(id, out var link))
            throw new AgentLinkException("disconnected", "The selected authenticated remote logon is not connected.");
        _identity.RequireTrustedFingerprint(link.Info.PeerFingerprint);
        link.Channel.RequireReady();
        return link;
    }

    private async Task<OperationResult> ForwardAsync(Link link, OperationRequest request, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, link.Channel.Lifetime);
        Task<OperationResult>? invocation = null;
        _identity.WithTrustedFingerprint(link.Info.PeerFingerprint, () =>
        {
            link.Channel.RequireReady();
            operation.Token.ThrowIfCancellationRequested();
            invocation = link.Peer.CallAsync(request, operation.Token);
        });
        return await invocation!;
    }

    private Task<OperationResult> InvokeCurrentAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        CancellationToken desktopConnection;
        lock (_remoteGate) desktopConnection = _desktopConnection;
        // The same-user/session control pipe is authenticated independently of
        // RDP. Only desktop work inherits the incoming channel's lifetime.
        return DispatchAsync(request, cancellationToken, desktopConnection, $"local:{LogonId}");
    }

    private OperationResult Catalog() => new(Data: Json.Element(_desktop.Tools.Concat(_copilot.Tools).ToArray()));

    private async Task<OperationResult> DispatchAsync(OperationRequest request, CancellationToken cancellationToken,
        CancellationToken desktopConnection, string agentPrincipal)
    {
        OperationContext.ActorId.Value = request.ActorId;
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method == "tools.list")
            return Catalog();
        if (request.Method == "desktop.release")
        {
            _consent.ReleaseDesktop();
            _desktop.InvalidateObservations();
            return new(Text: "Desktop control revoked.");
        }
        if (request.Method == "desktop.acquire")
        {
            using var availability = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _consent.DesktopAccessLifetime);
            if (!await _consent.RequestAsync(new("desktop-control", "Allow this paired agent to operate your desktop until disconnect or Stop?"), availability.Token))
                throw new AgentLinkException("permission_denied", "The human declined desktop control.");
            availability.Token.ThrowIfCancellationRequested();
            return new(Text: "Desktop control granted until disconnect or human revocation.");
        }
        if (_operations.TryGetValue(request.Method, out var handler))
        {
            if (ReferenceEquals(handler, _desktop))
            {
                using var availability = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _consent.DesktopAccessLifetime);
                return await _desktop.InvokeAsync(request, availability.Token, desktopConnection);
            }
            return await handler.InvokeAsync(request with { ActorId = agentPrincipal }, cancellationToken);
        }
        throw new AgentLinkException("unknown_operation", "The requested operation is not available.");
    }

    private void RevokeConnections()
    {
        StopDesktopControl();
        foreach (var channel in _channels.Values)
            CloseChannel(channel);
        Raise(StatusChanged);
    }

    public void OnSessionUnavailable()
    {
        StopDesktopControl();
        foreach (var channel in _channels.Values.Where(channel => channel.Snapshot.Direction == PeerDirection.IncomingAgent))
            CloseChannel(channel);
    }

    public void StopDesktopControl()
    {
        _consent.RevokeDesktop();
        _desktop.InvalidateObservations();
    }

    internal void SetDesktopAccessEnabled(bool enabled)
    {
        var changed = false;
        try
        {
            lock (_desktopAccessGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new AgentLinkException("broker_stopping", "Desktop availability cannot be changed while the broker is stopping.");
                if (!enabled)
                {
                    changed = _consent.SetDesktopAccessState(false);
                    _desktop.InvalidateObservations();
                }
                _desktopAccessSettings.Save(enabled);
                if (enabled)
                    changed = _consent.SetDesktopAccessState(true);
            }
        }
        finally
        {
            if (changed)
                _consent.NotifyChanged();
        }
    }

    private PeerChannel AddChannel(PeerDirection direction)
    {
        _stop.Token.ThrowIfCancellationRequested();
        if (!_channelSlots.Wait(0))
            throw new AgentLinkException("too_many_connections", "Too many channels are already connected.");
        var channel = new PeerChannel(direction, _stop.Token);
        if (!_channels.TryAdd(channel.Id, channel))
        {
            channel.Dispose();
            _channelSlots.Release();
            throw new AgentLinkException("too_many_connections", "Too many channels are already connected.");
        }
        Raise(StatusChanged);
        return channel;
    }

    private void AuthenticateChannel(SslStream tls, PeerChannel channel)
    {
        if (!tls.IsMutuallyAuthenticated || !tls.IsEncrypted || tls.RemoteCertificate is null)
            throw new AuthenticationException("Mutually authenticated encrypted TLS is required.");
        _identity.AdmitChannel(tls.RemoteCertificate, trusted => channel.Authenticate(tls.RemoteCertificate, trusted));
        Raise(StatusChanged);
    }

    private void ActivateIncoming(PeerChannel channel)
    {
        if (!channel.Ready)
            return;
        lock (_remoteGate)
        {
            channel.RequireReady();
            if (_remotePeer is not null && !ReferenceEquals(_remotePeer, channel))
                throw new AgentLinkException("duplicate_connection", "An incoming channel already owns this Windows session.");
            _remotePeer = channel;
            _desktopConnection = channel.Lifetime;
            _consent.SetConnectionState(true);
            _copilot.OnConnectionChanged(true);
        }
    }

    private void RemoveChannel(PeerChannel channel)
    {
        CloseChannel(channel);
        if (_channels.TryRemove(new KeyValuePair<string, PeerChannel>(channel.Id, channel)))
            _channelSlots.Release();
        Raise(StatusChanged);
    }

    private void CloseChannel(PeerChannel channel)
    {
        if (!channel.Close())
            return;
        var consentChanged = false;
        lock (_remoteGate)
        {
            if (ReferenceEquals(_remotePeer, channel))
            {
                _remotePeer = null;
                _desktopConnection = new(true);
                consentChanged = _consent.SetConnectionState(false);
                _copilot.OnConnectionChanged(false);
                _desktop.InvalidateObservations();
            }
        }
        if (consentChanged)
            _consent.NotifyChanged();
        foreach (var entry in _links.Where(entry => ReferenceEquals(entry.Value.Channel, channel)))
            _links.TryRemove(entry);
        Raise(StatusChanged);
    }

    private void SetTransportError(string? error)
    {
        if (Volatile.Read(ref _running) == 0 && Volatile.Read(ref _started) != 0)
            return;
        Volatile.Write(ref _lastTransportError, error);
        Raise(StatusChanged);
    }

    private static string TransportError(Exception exception) => exception switch
    {
        AuthenticationException => "TLS authentication failed. Check device clocks and certificate validity; older endpoints must already be paired.",
        AgentLinkException { Code: "pairing_required" or "untrusted_peer" } => "Pair the endpoint fingerprints on both devices, then reconnect.",
        AgentLinkException { Code: "invalid_metadata" or "protocol_mismatch" } => "The peer uses unsupported channel metadata. Update both endpoints and reconnect.",
        OperationCanceledException => "The channel closed or timed out. Check the RDP connection and pairing on both devices.",
        _ => "The RDP channel is unavailable. Check the Remote Desktop connection and that Remote Agent Link is installed on both devices."
    };

    private static bool Raise(Action? handlers, BrokerObserver observer = BrokerObserver.StatusChanged)
    {
        if (handlers is null)
            return false;
        var delivered = true;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception exception)
            {
                delivered = false;
                BrokerDiagnostics.ObserverFailed(observer, exception);
            }
        }
        return delivered;
    }

    private void WriteLog(string message)
    {
        if (Log is not { } handlers)
            return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(message); }
            catch (Exception exception) { BrokerDiagnostics.ObserverFailed(BrokerObserver.Log, exception); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_desktopAccessGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
        }
        _identity.TrustChanged -= RevokeConnections;
        Volatile.Write(ref _running, 0);
        await _stop.CancelAsync();
        _consent.SetConnected(false);
        _copilot.OnConnectionChanged(false);
        RevokeConnections();
        try
        {
            try { await Task.WhenAll(_listenerObservers); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { await Task.WhenAll(_sessions.Values).WaitAsync(TimeSpan.FromSeconds(15)); }
        }
        finally
        {
            try { await _copilot.DisposeAsync(); }
            finally
            {
                _desktop.Dispose();
                _stop.Dispose();
            }
        }
    }
}
