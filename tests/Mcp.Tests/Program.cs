using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using AgentLink.Core;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass the built AgentLink.Mcp.dll or .exe path.");
using var stop = new CancellationTokenSource();
var pipeName = $"RemoteAgentLink.Test.{Guid.NewGuid():N}";
var fixture = new BrokerFixtureState();
var broker = FakeBrokerAsync(pipeName, fixture, stop.Token);
var checks = 0;
string? snapshot = null;
string? firstActor = null;
try
{
    await using (var client = new StdioTestClient(args[0], "--broker-pipe", pipeName))
    {
        var calls = fixture.Calls;
        var init = (await client.InitializeAsync()).GetProperty("result");
        Check(init.GetProperty("serverInfo").GetProperty("name").GetString() == "remote-agent-link", "Wrong server identity.");
        var instructions = init.GetProperty("instructions").GetString()!;
        Check(instructions.Contains("stable tool catalog from startup") && instructions.Contains("rdp_bind_connection"),
            "Initialization must explain stable declarations and explicit binding.");
        Check(instructions.Contains("not proof that RDP disconnected") && instructions.Contains("run a local shell") &&
            instructions.Contains("no pairing or per-action broker approval"), "Instructions must distinguish host cache errors, wrong-target fallback and device access.");
        Check(instructions.Contains("window title alone does not establish readiness") &&
            instructions.Contains("not that the app consumed it"), "Instructions must require application readiness and observed input results.");
        var capability = init.GetProperty("capabilities").GetProperty("tools");
        Check(!capability.TryGetProperty("listChanged", out var changed) || !changed.GetBoolean(), "A stable catalog must not advertise list-change churn.");
        var initial = await client.RequestAsync("tools/list", new { });
        snapshot = Tools(initial).GetRawText();
        Check(Tools(initial).GetArrayLength() == 32, "All 32 declarations must be present before binding.");
        Check(fixture.Calls == calls, "Initialization and listing must not depend on broker or endpoint availability.");
        await UntilAsync(() => fixture.Presence.Any(presence => presence.Connected));
        var registration = fixture.Presence.First(presence => presence.Connected);
        firstActor = registration.ActorId;
        Check(Guid.TryParseExact(firstActor, "N", out _) && registration.ConnectionId is null && !registration.CurrentSession,
            "Initialization must report the local MCP connection without binding a remote target.");
        var expected = DesktopToolCatalog.All.Concat(AgentToolCatalog.All).ToArray();
        Check(expected.Length == 27 && expected.Select(tool => tool.Name).Distinct().Count() == 27, "Shared endpoint catalog must be complete and unique.");
        foreach (var definition in expected)
        {
            var declared = Tools(initial).EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == definition.Name);
            Check(JsonElement.DeepEquals(declared.GetProperty("inputSchema"), definition.InputSchema), $"Schema drift for {definition.Name}.");
            Check(declared.GetProperty("description").GetString()!.Contains("selected endpoint"), $"Missing target boundary for {definition.Name}.");
        }
        Check(Description(initial, "agent_start_session").Contains("Never use agent sessions as a workaround"), "Coding sessions are not an input fallback.");
        Check(Description(initial, "rdp_bind_connection").Contains("does not add or remove tools"), "Binding description must not require rediscovery.");
        Check(ErrorCode(await client.CallAsync("press_keys", new { keys = new[] { "WIN", "R" } })) == "not_bound", "Startup declarations must not auto-bind input.");
        Check(ErrorCode(await client.CallAsync("rdp_acquire_control", new { })) == "not_bound", "Control must not auto-bind.");
        var connections = await client.CallAsync("rdp_list_connections", new { });
        Check(connections.GetProperty("result").GetProperty("structuredContent")[0].GetProperty("connectionId").GetString() == "test-connection", "Discovery failed.");
        Check(ErrorCode(await client.CallAsync("rdp_get_connection", new { })) == "not_bound", "Discovery must not bind.");
        var invocations = fixture.Invocations;
        var bound = await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" });
        Check(!IsError(bound) && Text(bound).Contains("endpoint supports 8 declared endpoint tools"), "Binding must report actual endpoint support.");
        Check(Text(bound).Contains("Observe the actual result after input"), "Binding must explain input acknowledgement versus application outcome.");
        Check(!Text(bound).Contains("peer_only_tool"), "Binding must not promote remote-only tools into the advertised surface.");
        var selected = await client.CallAsync("rdp_get_connection", new { });
        var identity = selected.GetProperty("result").GetProperty("structuredContent").GetRawText();
        Check(bound.GetProperty("result").GetProperty("structuredContent").GetRawText() == identity &&
            bound.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() == identity,
            "Bind guidance must preserve structured identity and the original JSON text block.");
        Check(fixture.Invocations == invocations, "Binding must not grant control or execute operations.");
        await UntilAsync(() => fixture.Presence.Any(presence => presence.Connected && presence.ConnectionId == "test-connection"));
        Check(fixture.Presence.Where(presence => presence.Connected).All(presence => presence.ActorId == firstActor),
            "Presence must keep the same actor through binding.");
        await Stable(client);
        Check(ErrorCode(await client.CallAsync("press_keys", new { keys = new[] { "WIN", "R" } })) == "permission_denied", "Visibility and binding must not grant input.");
        fixture.DeviceAccessEnabled = false;
        Check(ErrorCode(await client.CallAsync("rdp_acquire_control", new { })) == "device_access_disabled", "A disabled device must remain blocked.");
        Check(ErrorCode(await client.CallAsync("take_screenshot", new { })) == "device_access_disabled", "Off must block observation.");
        Check(ErrorCode(await client.CallAsync("agent_start_session", new { })) == "device_access_disabled", "Off must block coding work.");
        await Stable(client);
        fixture.DeviceAccessEnabled = true;
        Check(!IsError(await client.CallAsync("rdp_acquire_control", new { })), "Enabled device did not grant control without a prompt.");
        foreach (var (name, arguments) in new (string, object)[]
        {
            ("press_keys", new { keys = new[] { "WIN", "R" } }),
            ("type_text", new { text = "synthetic fixture only" }),
            ("click", new { x = 10, y = 10 }),
            ("drag_mouse", new { startX = 10, startY = 10, endX = 20, endY = 20 })
        })
        {
            Check(HasTool(initial, name), $"Cached startup catalog must already contain {name}.");
            Check(!IsError(await client.CallAsync(name, arguments)), $"Cached {name} could not be invoked after reserving control.");
            await Stable(client);
        }
        CheckImage(await client.CallAsync("take_screenshot", new { }));
        var pathError = await client.CallAsync("agent_start_session", new { workingDirectory = @"C:\Users\fixture" });
        Check(ErrorCode(pathError) == "invalid_path" && Text(pathError).Contains("run a local shell"), "Workspace denial must not authorize a different execution path.");
        await Stable(client);
        Check(!IsError(await client.CallAsync("rdp_release_control", new { })), "Release failed.");
        Check(ErrorCode(await client.CallAsync("click", new { x = 1, y = 1 })) == "permission_denied", "Release must revoke input, not remove declarations.");
        Check(!IsError(await client.CallAsync("rdp_get_connection", new { })), "Releasing control must retain binding.");
        await Stable(client);
        Check(ErrorCode(await client.CallAsync("rdp_bind_connection", new { connectionId = "other" })) == "binding_fixed", "Target changed.");
        invocations = fixture.Invocations;
        Check(ErrorCode(await client.CallAsync("peer_only_tool", new { })) == "unknown_tool" && fixture.Invocations == invocations,
            "A peer must not inject a new callable operation.");
        fixture.Available = false;
        invocations = fixture.Invocations;
        Check(ErrorCode(await client.CallAsync("press_keys", new { keys = new[] { "A" } })) == "disconnected", "Disconnect was hidden.");
        await Stable(client);
        fixture.EmptyCatalog = true;
        fixture.Available = true;
        Check(ErrorCode(await client.CallAsync("press_keys", new { keys = new[] { "A" } })) == "unsupported_on_endpoint",
            "Reconnect must refresh endpoint support without changing declarations.");
        Check(fixture.Invocations == invocations, "Disconnected or unsupported input must not be replayed.");
        var empty = await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" });
        Check(Text(empty).Contains("supports 0 declared endpoint tools: []"), "Empty support must not be represented as lost declarations.");
        await Stable(client);
        fixture.EmptyCatalog = false;
        Check(!IsError(await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" })), "Same-target rebind failed.");
        Check(ErrorCode(await client.CallAsync("press_keys", new { keys = new[] { "A" } })) == "permission_denied", "Reconnect must not restore control.");
        CheckImage(await client.CallAsync("take_screenshot", new { }));
        await Stable(client);
    }
    Check(fixture.Presence.Last(presence => presence.ActorId == firstActor).Connected == false, "Clean MCP exit must unregister presence.");

    await using (var client = new StdioTestClient(args[0], "--broker-pipe", pipeName))
    {
        await client.InitializeAsync();
        await Stable(client);
        var invocations = fixture.Invocations;
        var bound = await client.CallAsync("rdp_bind_connection", new { connectionId = "rdp-client:synthetic-instance" });
        Check(!IsError(bound) && Text(bound).Contains("supports 0 declared endpoint tools") &&
            Text(bound).Contains("local RDP plugin lifecycle entry"), "Client-only binding must succeed with explicit unsupported surface status.");
        var surfaceResult = await client.CallAsync("rdp_get_connection", new { });
        var surface = surfaceResult.GetProperty("result").GetProperty("structuredContent").GetProperty("localSurface");
        Check(surface.GetProperty("status").GetString() == "identity_ambiguous" &&
            !surface.GetProperty("connectionIdentityVerified").GetBoolean() &&
            !surface.GetProperty("backgroundInputSupported").GetBoolean() &&
            !surface.GetProperty("freshMinimizedCaptureSupported").GetBoolean(),
            "Read-only service geometry was promoted to an operation capability.");
        Check(surface.GetProperty("backgroundInputUnsupportedReason").GetString()!.Contains("existing connection") &&
            surface.GetProperty("freshMinimizedCaptureUnsupportedReason").GetString()!.Contains("output suppression"),
            "rdp_get_connection dropped explicit background-input/minimized-capture blockers.");
        foreach (var name in new[] { "take_screenshot", "press_keys", "agent_list_sessions", "rdp_acquire_control", "rdp_release_control" })
            Check(ErrorCode(await client.CallAsync(name, new { })) == "unsupported_on_endpoint", "Client-only binding executed a desktop or remote operation.");
        Check(fixture.Invocations == invocations, "Client-only binding fell back to the remote endpoint.");
        Check(ErrorCode(await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" })) == "binding_fixed",
            "Arrival of a remote broker silently retargeted the selected client instance.");
        await Stable(client);
    }

    // A host reconnecting/restarting its MCP process must see the same declarations, not shrink to five.
    await using (var client = new StdioTestClient(args[0], "--broker-pipe", pipeName))
    {
        await client.InitializeAsync();
        await Stable(client);
        Check(ErrorCode(await client.CallAsync("click", new { x = 1, y = 1 })) == "not_bound", "Process restart must not inherit another binding.");
    }
    foreach (var mode in new[] { new[] { "--connection", "test-connection" }, new[] { "--current-session" } })
    {
        await using var client = new StdioTestClient(args[0], new[] { "--broker-pipe", pipeName }.Concat(mode).ToArray());
        var init = await client.InitializeAsync();
        var instructions = init.GetProperty("result").GetProperty("instructions").GetString()!;
        Check(instructions.Contains(mode[0] == "--current-session" ? "own Windows session" : "fixed to connectionId"), "Wrong startup targeting guidance.");
        await Stable(client);
        // Fixed targets hydrate support at invocation even if the host never refreshes tools/list.
        CheckImage(await client.CallAsync("take_screenshot", new { }));
        Check(ErrorCode(await client.CallAsync("rdp_bind_connection", new { connectionId = "other" })) == "binding_fixed", "Fixed mode changed target.");
    }
    await using (var client = new StdioTestClient(args[0], "--connection", "offline", "--broker-pipe", pipeName))
    {
        await client.InitializeAsync();
        await Stable(client);
        Check(ErrorCode(await client.CallAsync("take_screenshot", new { })) == "disconnected", "Offline fixed target executed.");
        Check(ErrorCode(await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" })) == "binding_fixed", "Offline configuration retargeted.");
    }
    await using (var client = new StdioTestClient(args[0], "--broker-pipe", $"RemoteAgentLink.Absent.{Guid.NewGuid():N}"))
    {
        await client.InitializeAsync();
        await Stable(client);
        Check(ErrorCode(await client.CallAsync("take_screenshot", new { })) == "not_bound", "Missing broker must not cause automatic local binding.");
    }
    foreach (var code in new[] { "untrusted_peer", "pairing_required", "unauthorized_pipe", "role_disabled", "invalid_response", "operation_failed" })
    {
        fixture.ReadinessErrorCode = code;
        await using var client = new StdioTestClient(args[0], "--connection", "test-connection", "--broker-pipe", pipeName);
        await client.InitializeAsync();
        await Stable(client);
        Check(ErrorCode(await client.CallAsync("take_screenshot", new { })) == code, $"Readiness failure {code} must be surfaced at invocation.");
        await Stable(client);
    }
    fixture.ReadinessErrorCode = null;
    foreach (var payload in new[]
    {
        "null", "{}", "[null]",
        """[{"name":"","description":"bad","inputSchema":{},"mutating":false,"capability":"observe"}]""",
        """[{"name":"click","description":"bad","inputSchema":[],"mutating":true,"capability":"desktop-control"}]""",
        """[{"name":"click","description":"one","inputSchema":{},"mutating":true,"capability":"desktop-control"},{"name":"click","description":"two","inputSchema":{},"mutating":true,"capability":"desktop-control"}]"""
    })
    {
        fixture.CatalogJson = payload;
        await using var client = new StdioTestClient(args[0], "--broker-pipe", pipeName);
        await client.InitializeAsync();
        Check(ErrorCode(await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" })) == "invalid_response",
            "Malformed or duplicate endpoint catalog must fail explicitly.");
        Check(ErrorCode(await client.CallAsync("rdp_get_connection", new { })) == "not_bound", "Invalid catalog committed a binding.");
        await Stable(client);
    }
    fixture.CatalogJson = null;
    fixture.CatalogErrorCode = "pairing_required";
    await using (var client = new StdioTestClient(args[0], "--broker-pipe", pipeName))
    {
        await client.InitializeAsync();
        var failed = await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" });
        Check(ErrorCode(failed) == "pairing_required", "Failed describe must fail binding.");
        Check(ErrorCode(await client.CallAsync("rdp_get_connection", new { })) == "not_bound", "Failed catalog load committed binding.");
        await Stable(client);
        fixture.CatalogErrorCode = null;
        Check(!IsError(await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" })), "Binding retry failed.");
        await Stable(client);
    }
    foreach (var invalid in new[]
    {
        new[] { "--connection" }, new[] { "--connection", "" }, new[] { "--connection", "bad\nid" },
        new[] { "--connection", new string('a', 257) }, new[] { "--connection", "first", "--connection", "second" },
        new[] { "--current-session", "--connection", "test-connection" }, new[] { "--connection", "test-connection", "--current-session" },
        new[] { "--connection", "--current-session" }, new[] { "--broker-pipe", @"\\server\pipe\unsafe" }
    })
    {
        using var process = Process.Start(StdioTestClient.CreateStartInfo(args[0], invalid))
            ?? throw new InvalidOperationException("MCP argument-validation process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("Invalid MCP arguments did not fail promptly.");
        }
        Check(process.ExitCode == 2 && (await output).Length == 0 && !string.IsNullOrWhiteSpace(await error),
            "Invalid arguments must fail exclusively on stderr.");
    }
    Console.WriteLine($"All {checks} MCP stdio integration assertions passed (synthetic broker; no desktop capture).");
}
finally
{
    await stop.CancelAsync();
    try { await broker; }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
}
return 0;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
static async Task UntilAsync(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(10, timeout.Token);
}
async Task Stable(StdioTestClient client)
{
    Check(Tools(await client.RequestAsync("tools/list", new { })).GetRawText() == snapshot, "The complete declaration snapshot changed.");
    Check(!client.NotificationSeen, "A state transition emitted tools/list_changed and invalidated host caches.");
}
void CheckImage(JsonElement response)
{
    var image = response.GetProperty("result").GetProperty("content").EnumerateArray()
        .Single(block => block.GetProperty("type").GetString() == "image");
    Check(image.GetProperty("mimeType").GetString() == "image/png" &&
        Convert.FromBase64String(image.GetProperty("data").GetString()!).Length > 0, "Image must remain an MCP image block.");
}
static JsonElement Tools(JsonElement response) => response.GetProperty("result").GetProperty("tools");
static bool HasTool(JsonElement response, string name) => Tools(response).EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == name);
static string Description(JsonElement response, string name) => Tools(response).EnumerateArray()
    .Single(tool => tool.GetProperty("name").GetString() == name).GetProperty("description").GetString()!;
static string Text(JsonElement response) => string.Join("\n", response.GetProperty("result").GetProperty("content").EnumerateArray()
    .Where(block => block.GetProperty("type").GetString() == "text").Select(block => block.GetProperty("text").GetString()));
static bool IsError(JsonElement response) => response.TryGetProperty("error", out _) ||
    response.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();
static string? ErrorCode(JsonElement response) => IsError(response) ? response.GetProperty("result").GetProperty("structuredContent").GetProperty("code").GetString() : null;

static async Task FakeBrokerAsync(string pipeName, BrokerFixtureState fixture, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 16,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        await pipe.WaitForConnectionAsync(cancellationToken);
        await using var peer = new RpcPeer(pipe, (request, _) =>
        {
            if (request.Method == "mcp.presence")
            {
                var presence = Arguments.Read<McpPresenceRequest>(request.Arguments);
                fixture.Presence.Enqueue(presence);
                return Task.FromResult(new OperationResult(Data: Json.Element(new { recorded = true })));
            }
            fixture.RecordCall();
            var connection = new ConnectionInfo("test-connection", new string('A', 64), 99, "synthetic-logon", "test-epoch");
            if (!fixture.Available && request.Method is "get" or "describe" or "invoke")
                throw new AgentLinkException("disconnected", "The synthetic RDP transport is disconnected.");
            if (fixture.ReadinessErrorCode is { } error && request.Method is "get" or "describe" or "invoke")
                throw new AgentLinkException(error, "The synthetic endpoint refused the operation.");
            if (fixture.CatalogErrorCode is { } catalogError && request.Method == "describe")
                throw new AgentLinkException(catalogError, "The synthetic endpoint refused discovery.");
            if (request.Method is "get" or "describe")
            {
                var target = Arguments.Read<TargetRequest>(request.Arguments);
                if (!target.CurrentSession && target.ConnectionId == "rdp-client:synthetic-instance")
                    return Task.FromResult(request.Method == "describe"
                        ? new OperationResult(Data: Json.Element(Array.Empty<ToolDefinition>()))
                        : new OperationResult(Data: Json.Element(new ConnectionInfo(target.ConnectionId, null, null, null,
                            "synthetic-epoch", TargetKind: "localRdpClient", State: "connected",
                            LocalSurfaceStatus: "identity_ambiguous",
                            LocalSurface: new(1, "identity_ambiguous", 0, true, "0x1234", 42, 123456, 1,
                                1920, 1080, 960, 540, 0, 0, 0.5f)))));
                if (!target.CurrentSession && target.ConnectionId != connection.ConnectionId)
                    throw new AgentLinkException("disconnected", "The selected synthetic endpoint is disconnected.");
            }
            if (request.Method == "invoke")
            {
                var route = Arguments.Read<RoutedOperation>(request.Arguments);
                if (!route.CurrentSession && route.ConnectionId == "rdp-client:synthetic-instance")
                    throw new AgentLinkException("unsupported_on_endpoint", "No connection-bound local surface contract.");
                if (!route.CurrentSession && route.ConnectionId != connection.ConnectionId)
                    throw new AgentLinkException("disconnected", "Operation targeted another endpoint.");
                fixture.RecordInvocation();
                if (!fixture.DeviceAccessEnabled)
                    throw new AgentLinkException("device_access_disabled", "Agent access is turned off on this device.");
                var method = route.Request.Method;
                if (method == "desktop.acquire")
                {
                    fixture.ControlOwner = route.Request.ActorId;
                }
                else if (method == "desktop.release") fixture.ControlOwner = null;
                else if (method is "press_keys" or "type_text" or "click" or "drag_mouse")
                {
                    if (fixture.ControlOwner is null || fixture.ControlOwner != route.Request.ActorId)
                        throw new AgentLinkException("permission_denied", "Synthetic desktop control not granted.");
                }
                else if (method == "agent_start_session")
                    throw new AgentLinkException("invalid_path", "The workspace must not overlap broker private data.");
                return Task.FromResult(method == "take_screenshot"
                    ? new OperationResult(Image: Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jHhQAAAAASUVORK5CYII="), MimeType: "image/png")
                    : new OperationResult(Data: Json.Element(new { method, connection.ConnectionId })));
            }
            var supported = new HashSet<string> { "take_screenshot", "press_keys", "type_text", "click", "drag_mouse", "list_windows", "get_screen_size", "agent_start_session" };
            var catalog = DesktopToolCatalog.All.Concat(AgentToolCatalog.All).Where(tool => supported.Contains(tool.Name)).Append(
                new ToolDefinition("peer_only_tool", "Not part of the MCP contract.", Json.Element(new { type = "object" }), true, "unsupported")).ToArray();
            JsonElement Catalog()
            {
                if (fixture.CatalogJson is not { } text) return Json.Element(fixture.EmptyCatalog ? [] : catalog);
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
            OperationResult result = request.Method switch
            {
                "list" => new(Data: Json.Element(fixture.Available ? new[] { connection } : [])),
                "get" => new(Data: Json.Element(connection)),
                "describe" => new(Data: Catalog()),
                _ => throw new AgentLinkException("unknown_operation", "Unknown synthetic operation.")
            };
            return Task.FromResult(result);
        });
        await peer.Completion.WaitAsync(cancellationToken);
    }
}
internal sealed class BrokerFixtureState
{
    private int _available = 1, _invocations, _calls, _emptyCatalog, _deviceAccessEnabled = 1;
    private string? _readinessErrorCode, _catalogErrorCode, _controlOwner, _catalogJson;
    public bool Available { get => Volatile.Read(ref _available) != 0; set { Volatile.Write(ref _available, value ? 1 : 0); if (!value) ControlOwner = null; } }
    public int Invocations => Volatile.Read(ref _invocations);
    public int Calls => Volatile.Read(ref _calls);
    public bool EmptyCatalog { get => Volatile.Read(ref _emptyCatalog) != 0; set => Volatile.Write(ref _emptyCatalog, value ? 1 : 0); }
    public bool DeviceAccessEnabled { get => Volatile.Read(ref _deviceAccessEnabled) != 0; set { Volatile.Write(ref _deviceAccessEnabled, value ? 1 : 0); if (!value) ControlOwner = null; } }
    public System.Collections.Concurrent.ConcurrentQueue<McpPresenceRequest> Presence { get; } = new();
    public string? ControlOwner { get => Volatile.Read(ref _controlOwner); set => Volatile.Write(ref _controlOwner, value); }
    public string? ReadinessErrorCode { get => Volatile.Read(ref _readinessErrorCode); set => Volatile.Write(ref _readinessErrorCode, value); }
    public string? CatalogErrorCode { get => Volatile.Read(ref _catalogErrorCode); set => Volatile.Write(ref _catalogErrorCode, value); }
    public string? CatalogJson { get => Volatile.Read(ref _catalogJson); set => Volatile.Write(ref _catalogJson, value); }
    public void RecordInvocation() => Interlocked.Increment(ref _invocations);
    public void RecordCall() => Interlocked.Increment(ref _calls);
}
