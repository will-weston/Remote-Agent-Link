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
    private sealed class ClientConnection(ConnectionInfo info, string reporterIdentity)
    {
        internal ConnectionInfo Info = info;
        internal readonly string ReporterIdentity = reporterIdentity;
        internal object? SurfaceOwner;
    }
    private readonly EndpointIdentity _identity;
    private readonly HumanConsent _consent;
    private readonly DesktopAccessSettings _desktopAccessSettings;
    private readonly object _desktopAccessGate = new();
    private readonly DesktopOperations _desktop;
    private readonly CopilotOperations _copilot;
    private readonly Dictionary<string, IOperationHandler> _operations;
    private readonly ConcurrentDictionary<string, Link> _links = new();
    private readonly ConcurrentDictionary<string, ClientConnection> _clientConnections = new();
    private readonly ConcurrentDictionary<string, PeerChannel> _channels = new();
    private readonly AgentPresenceRegistry _agents = new();
    private readonly ConcurrentDictionary<Guid, Task> _sessions = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connectionSlots = new(16, 16);
    private readonly SemaphoreSlim _clientConnectionSlots = new(16, 16);
    private readonly SemaphoreSlim _clientSurfaceSlots = new(16, 16);
    private readonly SemaphoreSlim _channelSlots = new(17, 17);
    private readonly bool _localRole;
    private readonly bool _remoteRole;
    private readonly BrokerTransportOptions _transport;
    private Task[] _listeners = [];
    private Task[] _listenerObservers = [];
    private readonly object _remoteGate = new();
    private PeerChannel? _remotePeer;
    private int _started;
    private int _running;
    private int _disposed;
    private string? _lastTransportError;
    private string? _deviceAccessFailure;
    private bool _unsavedDeviceBlock;

    public BrokerHost(EndpointIdentity identity, HumanConsent consent, string dataDirectory, string role,
        BrokerTransportOptions? transport = null, ICopilotRuntimeFactory? runtimeFactory = null)
    {
        if (role is not ("local" or "remote" or "both"))
            throw new ArgumentException("The broker role must be local, remote or both.", nameof(role));
        _identity = identity;
        _consent = consent;
        _consent.SetDesktopAccessState(false);
        _desktopAccessSettings = new DesktopAccessSettings(identity.StorageDirectory, dataDirectory);
        _consent.SetDesktopAccessState(_desktopAccessSettings.Read());
        _consent.SetConnectionState(true);
        _localRole = role is "local" or "both";
        _remoteRole = role is "remote" or "both";
        _transport = transport ?? new(PipeNames.Control, PipeNames.Transport, () => new WtsStream());
        _desktop = new DesktopOperations(consent);
        _copilot = runtimeFactory is null ? new CopilotOperations(consent, dataDirectory) :
            new CopilotOperations(consent, dataDirectory, runtimeFactory);
        _operations = new[] { (IOperationHandler)_desktop, _copilot }
            .SelectMany(handler => handler.Tools.Select(tool => (tool.Name, handler)))
            .ToDictionary(entry => entry.Name, entry => entry.handler, StringComparer.Ordinal);
    }

    public event Action<string>? Log;
    public event Action? StatusChanged;
    public event Action? ShowRequested;
    public bool IsRunning => Volatile.Read(ref _running) != 0 && !_stop.IsCancellationRequested;
    public string? LastTransportError => Volatile.Read(ref _lastTransportError);
    public IReadOnlyList<PeerConnectionSnapshot> PeerConnections => _channels.Values
        .Where(channel => !channel.Closed).Select(channel => channel.Snapshot).OrderBy(peer => peer.ConnectedAt).ToArray();
    public IReadOnlyList<ConnectionInfo> Connections => _links.Values.Where(link => link.Channel.Ready).Select(link => link.Info)
        .Concat(_clientConnections.Values.Select(client => Volatile.Read(ref client.Info))).ToArray();
    public IReadOnlyList<AgentConnectionSnapshot> GetAgentConnections() => _agents.Snapshot();
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
        List<Task> listeners = [PipeLoopAsync(_transport.ControlPipe, HandleLocalPipeAsync), MonitorDeviceAccessAsync()];
        if (_localRole)
        {
            listeners.Add(PipeLoopAsync(_transport.TransportPipe, HandleTransportPipeAsync));
            listeners.Add(PipeLoopAsync(_transport.TransportPipe + ".Clients", HandleClientConnectionPipeAsync, _clientConnectionSlots));
            listeners.Add(ListenClientSurfacesAsync());
        }
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
            _consent.SetConnected(false);
            _copilot.OnConnectionChanged(false);
            RevokeConnections();
        }
    }

    private async Task PipeLoopAsync(string name, Func<NamedPipeServerStream, Task> handler, SemaphoreSlim? slots = null)
    {
        slots ??= _connectionSlots;
        var firstInstance = true;
        while (!_stop.IsCancellationRequested)
        {
            await slots.WaitAsync(_stop.Token);
            NamedPipeServerStream? pipe = null;
            var handedOff = false;
            try
            {
                pipe = PipeSecurity.CreateServer(name, firstInstance);
                firstInstance = false;
                await pipe.WaitForConnectionAsync(_stop.Token);
                var id = Guid.NewGuid();
                var accepted = pipe;
                var task = RunPipeAsync(accepted, handler, slots);
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
                    slots.Release();
                }
            }
        }
    }

    private async Task RunPipeAsync(NamedPipeServerStream pipe, Func<NamedPipeServerStream, Task> handler, SemaphoreSlim slots)
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
            slots.Release();
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

    private async Task HandleClientConnectionPipeAsync(NamedPipeServerStream pipe)
    {
        try { await HandleClientConnectionAsync(pipe, ReporterIdentity(pipe)); }
        catch (Exception exception) when (exception is IOException or AgentLinkException or OperationCanceledException)
        {
            WriteLog($"Local RDP lifecycle reporter closed: {exception.GetType().Name}");
        }
    }

    private static string ReporterIdentity(NamedPipeServerStream pipe)
    {
        var processId = PipeSecurity.VerifyLocalSession(pipe);
        using var process = Process.GetProcessById((int)processId);
        return $"{processId}:{process.StartTime.ToUniversalTime().Ticks}";
    }

    internal async Task HandleClientConnectionAsync(Stream transport, string reporterIdentity = "test-stream")
    {
        ClientConnection? client = null;
        string? id = null;
        try
        {
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var report = await ClientConnectionReport.ReadAsync(transport, deadline.Token);
                if (report is null)
                    return;
                var info = report.ToConnection();
                if (client is null)
                {
                    id = info.ConnectionId;
                    client = new(info, reporterIdentity);
                    if (!_clientConnections.TryAdd(id, client))
                        throw new AgentLinkException("duplicate_connection", "Another reporter owns this RDP client instance.");
                }
                else
                {
                    if (info.ConnectionId != id)
                        throw new AgentLinkException("invalid_client_report", "A lifecycle stream cannot change its selected instance.");
                    lock (client)
                    {
                        var previous = client.Info;
                        if (previous.Epoch == info.Epoch && info.State == "connected")
                            info = info with { LocalSurface = previous.LocalSurface, LocalSurfaceStatus = previous.LocalSurfaceStatus };
                        Volatile.Write(ref client.Info, info);
                        if (previous == info)
                            continue;
                    }
                }
                Raise(StatusChanged);
            }
        }
        finally
        {
            if (client is not null && id is not null &&
                _clientConnections.TryRemove(new KeyValuePair<string, ClientConnection>(id, client)))
                Raise(StatusChanged);
        }
    }

    private async Task HandleClientSurfacePipeAsync(NamedPipeServerStream pipe)
    {
        try { await HandleClientSurfaceAsync(pipe, ReporterIdentity(pipe)); }
        catch (Exception exception) when (exception is IOException or AgentLinkException or OperationCanceledException)
        {
            WriteLog($"Local RDP surface reporter closed: {exception.GetType().Name}");
        }
    }

    private async Task ListenClientSurfacesAsync()
    {
        try
        {
            await PipeLoopAsync(_transport.TransportPipe + ".Clients.Surfaces", HandleClientSurfacePipeAsync, _clientSurfaceSlots);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // The diagnostic plane is optional; never take lifecycle discovery
            // or authenticated desktop transport down with it.
            WriteLog($"Local RDP surface listener unavailable: {exception.GetType().Name}");
        }
    }

    internal async Task HandleClientSurfaceAsync(Stream transport, string reporterIdentity = "test-stream")
    {
        var owner = new object();
        ClientConnection? bound = null;
        Guid? instance = null;
        ClientSurfaceReport? previous = null;
        try
        {
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var report = await ClientSurfaceReport.ReadAsync(transport, deadline.Token);
                if (report is null)
                    return;
                if ((instance is not null && instance != report.Instance) ||
                    (previous is not null && previous.Epoch == report.Epoch &&
                     (previous.Surface.Generation > report.Surface.Generation ||
                      (previous.Surface.Generation == report.Surface.Generation && previous != report))))
                    throw new AgentLinkException("invalid_surface_report", "A surface stream cannot retarget or regress its generation.");
                instance = report.Instance;
                previous = report;
                if (!_clientConnections.TryGetValue($"rdp-client:{report.Instance:N}", out var client))
                    continue; // Lifecycle publication can race this independent optional stream.
                if (client.ReporterIdentity != reporterIdentity || (bound is not null && bound != client))
                    throw new AgentLinkException("invalid_surface_report", "Surface metadata must belong to the same lifecycle reporter.");
                lock (client)
                {
                    if (client.Info.Epoch != report.Epoch.ToString("N") || client.Info.State != "connected")
                        continue;
                    if (client.SurfaceOwner is not null && client.SurfaceOwner != owner)
                        throw new AgentLinkException("duplicate_surface_report", "Another stream owns this surface metadata.");
                    bound = client;
                    client.SurfaceOwner = owner;
                    Volatile.Write(ref client.Info, client.Info with
                    {
                        LocalSurface = report.Surface,
                        LocalSurfaceStatus = report.Surface.Status
                    });
                }
                Raise(StatusChanged);
            }
        }
        finally
        {
            if (bound is not null)
            {
                lock (bound)
                {
                    if (bound.SurfaceOwner == owner)
                    {
                        bound.SurfaceOwner = null;
                        Volatile.Write(ref bound.Info, bound.Info with
                        {
                            LocalSurface = null,
                            LocalSurfaceStatus = "unsupported_public_contract"
                        });
                    }
                }
                Raise(StatusChanged);
            }
        }
    }

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
                var fingerprint = channel.Snapshot.Fingerprint;
                channel.RequireReady();
                operation.Token.ThrowIfCancellationRequested();
                if (request.Method != "tools.list" && request.ActorId is not { Length: > 0 and <= 256 })
                    throw new AgentLinkException("invalid_actor", "A bounded agent identity is required.");
                Interlocked.Exchange(ref lastRequest, Stopwatch.GetTimestamp());
                var actor = $"remote:{channel.Id}:{fingerprint}:{request.ActorId}";
                if (request.Method == "mcp.presence")
                {
                    var presence = Arguments.Read<McpPresenceRequest>(request.Arguments);
                    if (presence.ActorId != request.ActorId || presence.ConnectionId is not null || presence.CurrentSession)
                        throw new AgentLinkException("invalid_actor", "Presence must match the authenticated channel actor and cannot be forwarded again.");
                    channel.WithReady(() => _agents.Update(actor, channel.Snapshot.DisplayName, channel.Id, presence.Connected));
                    Raise(StatusChanged);
                    return new(Text: "Agent presence updated.");
                }
                return await DispatchAsync(request with { ActorId = actor },
                    operation.Token, channel.Lifetime, RemotePrincipal(channel));
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

    internal async Task<OperationResult> HandleLocalRequestAsync(OperationRequest request, string clientIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method == "mcp.presence")
        {
            var presence = Arguments.Read<McpPresenceRequest>(request.Arguments);
            var actor = StampLocalActor(clientIdentity, presence.ActorId);
            if (presence.CurrentSession && presence.ConnectionId is not null)
                throw new AgentLinkException("invalid_arguments", "Current-session presence cannot select a remote connection.");
            _agents.Update($"local:{actor}", Environment.MachineName, null, presence.Connected);
            Raise(StatusChanged);
            if (presence.ConnectionId is not null && !_clientConnections.ContainsKey(presence.ConnectionId))
                await ForwardAsync(GetLink(presence.ConnectionId), new("mcp.presence",
                    Json.Element(new McpPresenceRequest(actor, null, false, presence.Connected)), actor), cancellationToken);
            return new(Text: "Agent presence updated.");
        }
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
            var ownedRequest = route.Request with { ActorId = StampLocalActor(clientIdentity, route.Request.ActorId) };
            if (route.CurrentSession)
            {
                if (route.ConnectionId is not null)
                    throw new AgentLinkException("invalid_arguments", "Current-session operations cannot select a remote connection.");
                return await InvokeCurrentAsync(ownedRequest with { ActorId = $"local:{ownedRequest.ActorId}" }, cancellationToken);
            }
            if (route.ConnectionId is not null && _clientConnections.ContainsKey(route.ConnectionId))
            {
                RefreshDeviceAccess();
                if (!_consent.DeviceAccessEnabled)
                    throw new AgentLinkException("device_access_disabled", "Agents are not allowed to use this device.");
                throw new AgentLinkException("unsupported_on_endpoint",
                    "This local RDP client instance has no supported connection-bound capture/input contract. " +
                    "Remote broker tools belong to a separately selected authenticated endpoint; no local desktop fallback is allowed.");
            }
            return await ForwardAsync(GetLink(route.ConnectionId), ownedRequest, cancellationToken);
        }
        var target = Arguments.Read<TargetRequest>(request.Arguments);
        if (request.Method == "get")
        {
            if (target.CurrentSession)
                return new(Data: Json.Element(new { currentSession = true, connected = _consent.IsConnected, sessionId = Process.GetCurrentProcess().SessionId }));
            if (target.ConnectionId is not null && _clientConnections.TryGetValue(target.ConnectionId, out var client))
                return new(Data: Json.Element(Volatile.Read(ref client.Info)));
            return new(Data: Json.Element(GetLink(target.ConnectionId).Info));
        }
        if (request.Method == "describe")
        {
            if (target.CurrentSession)
            {
                return await InvokeCurrentAsync(new("tools.list", Json.Element(new { })), cancellationToken);
            }
            if (target.ConnectionId is not null && _clientConnections.ContainsKey(target.ConnectionId))
                return new(Data: Json.Element(Array.Empty<ToolDefinition>()));
            return await ForwardAsync(GetLink(target.ConnectionId), new("tools.list", Json.Element(new { }),
                $"catalog:{clientIdentity}"), cancellationToken);
        }
        throw new AgentLinkException("unknown_operation", "Unknown local broker operation.");
    }

    private Link GetLink(string? id)
    {
        if (id is null || !_links.TryGetValue(id, out var link))
            throw new AgentLinkException("disconnected", "The selected authenticated remote logon is not connected.");
        link.Channel.RequireReady();
        return link;
    }

    private async Task<OperationResult> ForwardAsync(Link link, OperationRequest request, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, link.Channel.Lifetime);
        link.Channel.RequireReady();
        operation.Token.ThrowIfCancellationRequested();
        return await link.Peer.CallAsync(request, operation.Token);
    }

    private Task<OperationResult> InvokeCurrentAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        // Local same-logon IPC does not inherit a different computer's RDP lifetime.
        return DispatchAsync(request, cancellationToken, _stop.Token, $"local:{LogonId}");
    }

    internal static string StampLocalActor(string clientIdentity, string? actor)
    {
        if (!Guid.TryParseExact(actor, "N", out var instance))
            throw new AgentLinkException("invalid_actor", "A valid MCP instance identity is required.");
        return $"{clientIdentity}:{instance:N}";
    }

    private static string RemotePrincipal(PeerChannel channel) =>
        $"remote:{channel.Snapshot.Fingerprint}";

    private OperationResult Catalog() => new(Data: Json.Element(_desktop.Tools.Concat(_copilot.Tools).ToArray()));

    private async Task<OperationResult> DispatchAsync(OperationRequest request, CancellationToken cancellationToken,
        CancellationToken desktopConnection, string agentPrincipal)
    {
        OperationContext.ActorId.Value = request.ActorId;
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method == "tools.list")
            return Catalog();
        RefreshDeviceAccess();
        using var access = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _consent.DeviceAccessLifetime);
        cancellationToken = access.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method == "desktop.release")
        {
            _consent.ReleaseDesktop();
            _desktop.InvalidateObservations();
            return new(Text: "Desktop control revoked.");
        }
        if (request.Method == "desktop.acquire")
        {
            using var availability = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _consent.DesktopAccessLifetime);
            if (!await _consent.RequestAsync(new("desktop-control", "Acquire exclusive desktop input."), availability.Token))
                throw new AgentLinkException("permission_denied", "Desktop input is not currently available.");
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
        Raise(StatusChanged);
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
                    throw new AgentLinkException("broker_stopping", "Device access cannot be changed while the broker is stopping.");
                if (!enabled)
                {
                    _unsavedDeviceBlock = true;
                    changed = ApplyDeviceAccessState(false);
                }
                _desktopAccessSettings.Save(enabled);
                _unsavedDeviceBlock = false;
                if (enabled)
                {
                    changed = ApplyDeviceAccessState(true);
                }
            }
        }
        finally
        {
            if (changed)
                _consent.NotifyChanged();
        }
    }

    private bool ApplyDeviceAccessState(bool enabled)
    {
        var changed = _consent.SetDesktopAccessState(enabled);
        if (!enabled) _desktop.InvalidateObservations();
        _copilot.OnDeviceAccessChanged(enabled);
        return changed;
    }

    private void RefreshDeviceAccess()
    {
        var changed = false;
        lock (_desktopAccessGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            bool enabled;
            try
            {
                enabled = _desktopAccessSettings.ReadCurrent();
                if (!enabled) _unsavedDeviceBlock = false;
                if (_unsavedDeviceBlock) enabled = false;
                if (_deviceAccessFailure is not null) WriteLog("Shared device access settings are available again.");
                _deviceAccessFailure = null;
            }
            catch (AgentLinkException exception)
            {
                enabled = false;
                if (_deviceAccessFailure != exception.Code)
                    WriteLog($"Agent access blocked: {exception.Code}. Repair the shared device preference.");
                _deviceAccessFailure = exception.Code;
            }
            if (_consent.DeviceAccessEnabled != enabled)
                changed = ApplyDeviceAccessState(enabled);
        }
        if (changed) _consent.NotifyChanged();
    }

    private async Task MonitorDeviceAccessAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(_stop.Token))
            RefreshDeviceAccess();
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
            _copilot.OnPrincipalConnected(RemotePrincipal(channel));
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
        _agents.Disconnect(channel.Id);
        var consentChanged = false;
        lock (_remoteGate)
        {
            if (ReferenceEquals(_remotePeer, channel))
            {
                _remotePeer = null;
                _copilot.OnPrincipalDisconnected(RemotePrincipal(channel));
                _consent.RevokeDesktop();
                consentChanged = true;
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
        AuthenticationException => "TLS authentication failed. Check device clocks, certificate validity and that both endpoints are up to date.",
        AgentLinkException { Code: "channel_not_ready" } => "The authenticated channel is not ready. Update both endpoints and reconnect.",
        AgentLinkException { Code: "invalid_metadata" or "protocol_mismatch" } => "The peer uses unsupported channel metadata. Update both endpoints and reconnect.",
        OperationCanceledException => "The channel closed or timed out. Check the RDP connection on both devices.",
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
