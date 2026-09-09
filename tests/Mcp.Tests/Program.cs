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
try
{
    await using (var client = new StdioTestClient(args[0], "--broker-pipe", pipeName))
    {
        var init = await client.InitializeAsync();
        Check(init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "remote-agent-link", "Wrong server identity.");
        Check(HasTool(await client.RequestAsync("tools/list", new { }), "rdp_bind_connection"), "Binding tool missing.");
        Check(ErrorCode(await client.CallAsync("take_screenshot", new { })) == "not_bound", "Unbound desktop call succeeded.");
        var connections = await client.CallAsync("rdp_list_connections", new { });
        Check(connections.GetProperty("result").GetProperty("structuredContent")[0].GetProperty("connectionId").GetString() == "test-connection", "Connection discovery failed.");
        Check(!IsError(await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" })), "Binding failed.");
        Check(HasTool(await client.RequestAsync("tools/list", new { }), "take_screenshot"), "Remote catalog was not installed.");
        CheckImage(await client.CallAsync("take_screenshot", new { }));
        Check(ErrorCode(await client.CallAsync("rdp_bind_connection", new { connectionId = "other" })) == "binding_fixed", "An MCP process switched its desktop target.");
        Check(client.NotificationSeen, "Missing tools/list_changed notification.");
    }

    await using (var client = new StdioTestClient(args[0], "--broker-pipe", pipeName, "--connection", "test-connection"))
    {
        await client.InitializeAsync();
        Check(HasTool(await client.RequestAsync("tools/list", new { }), "take_screenshot"), "Configured endpoint did not load its catalog.");
        CheckImage(await client.CallAsync("take_screenshot", new { }));
        var selected = await client.CallAsync("rdp_get_connection", new { });
        Check(selected.GetProperty("result").GetProperty("structuredContent").GetProperty("connectionId").GetString() == "test-connection", "Configured target was not preserved.");
        Check(ErrorCode(await client.CallAsync("rdp_bind_connection", new { connectionId = "other" })) == "binding_fixed", "Configured target could be replaced.");
        Check(!IsError(await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" })), "Refreshing the same configured target failed.");
    }

    await using (var client = new StdioTestClient(args[0], "--connection", "offline", "--broker-pipe", pipeName))
    {
        await client.InitializeAsync();
        var tools = await client.RequestAsync("tools/list", new { });
        Check(HasTool(tools, "rdp_list_connections"), "Offline configuration lost connection-management tools.");
        Check(!HasTool(tools, "take_screenshot"), "Offline configuration advertised an unloaded remote tool.");
        Check(ErrorCode(await client.CallAsync("rdp_get_connection", new { })) == "disconnected", "Offline target appeared connected.");
        Check(ErrorCode(await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" })) == "binding_fixed", "Offline configuration silently retargeted an online desktop.");
    }

    await using (var client = new StdioTestClient(args[0], "--current-session", "--broker-pipe", pipeName))
    {
        await client.InitializeAsync();
        Check(HasTool(await client.RequestAsync("tools/list", new { }), "take_screenshot"), "Own-session configuration lost its catalog.");
        CheckImage(await client.CallAsync("take_screenshot", new { }));
        Check(ErrorCode(await client.CallAsync("rdp_bind_connection", new { connectionId = "test-connection" })) == "binding_fixed", "Own-session configuration accepted a remote binding.");
    }

    fixture.Available = false;
    await using (var client = new StdioTestClient(args[0], "--connection", "test-connection", "--broker-pipe", pipeName))
    {
        await client.InitializeAsync();
        Check(!HasTool(await client.RequestAsync("tools/list", new { }), "take_screenshot"), "Unavailable target advertised unloaded tools.");
        var invocations = fixture.Invocations;
        fixture.Available = true;
        Check(HasTool(await client.RequestAsync("tools/list", new { }), "take_screenshot"), "Tool-list refresh did not recover the configured endpoint.");
        Check(fixture.Invocations == invocations, "Connection recovery performed a desktop operation.");
        fixture.Available = false;
        Check(ErrorCode(await client.CallAsync("take_screenshot", new { })) == "disconnected", "A stale catalog bypassed disconnection.");
        fixture.Available = true;
        Check(fixture.Invocations == invocations, "An unsuccessful operation was replayed during reconnect.");
        CheckImage(await client.CallAsync("take_screenshot", new { }));
        Check(fixture.Invocations == invocations + 1, "Explicit retry executed more than once.");
    }

    foreach (var code in new[] { "untrusted_peer", "pairing_required", "unauthorized_pipe", "role_disabled", "invalid_response", "operation_failed" })
    {
        fixture.ReadinessErrorCode = code;
        await using var client = new StdioTestClient(args[0], "--connection", "test-connection", "--broker-pipe", pipeName);
        await client.InitializeAsync();
        var tools = await client.RequestAsync("tools/list", new { });
        if (code is "untrusted_peer" or "pairing_required")
        {
            Check(HasTool(tools, "rdp_list_connections") && !HasTool(tools, "take_screenshot"),
                "Pairing-required endpoint did not retain only connection-management tools.");
        }
        else
        {
            Check(tools.TryGetProperty("error", out _), $"Catalog failure {code} was hidden as ordinary offline state.");
        }
    }
    fixture.ReadinessErrorCode = null;

    foreach (var invalid in new[]
    {
        new[] { "--connection" },
        new[] { "--connection", "" },
        new[] { "--connection", "bad\nid" },
        new[] { "--connection", new string('a', 257) },
        new[] { "--connection", "first", "--connection", "second" },
        new[] { "--current-session", "--connection", "test-connection" },
        new[] { "--connection", "test-connection", "--current-session" },
        new[] { "--connection", "--current-session" },
        new[] { "--broker-pipe", @"\\server\pipe\unsafe" }
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
            "Invalid MCP arguments did not report an error exclusively on stderr.");
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
    if (!condition)
        throw new InvalidOperationException(message);
    checks++;
}

void CheckImage(JsonElement response)
{
    var image = response.GetProperty("result").GetProperty("content").EnumerateArray()
        .Single(block => block.GetProperty("type").GetString() == "image");
    Check(image.GetProperty("mimeType").GetString() == "image/png" &&
        Convert.FromBase64String(image.GetProperty("data").GetString()!).Length > 0,
        "Image was not an actual MCP image block.");
}

static bool HasTool(JsonElement response, string name) =>
    response.GetProperty("result").GetProperty("tools").EnumerateArray()
        .Any(tool => tool.GetProperty("name").GetString() == name);

static bool IsError(JsonElement response) =>
    response.TryGetProperty("error", out _) ||
    (response.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean());

static string? ErrorCode(JsonElement response) =>
    IsError(response) ? response.GetProperty("result").GetProperty("structuredContent").GetProperty("code").GetString() : null;

static async Task FakeBrokerAsync(string pipeName, BrokerFixtureState fixture, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 16,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        await pipe.WaitForConnectionAsync(cancellationToken);
        await using var peer = new RpcPeer(pipe, (request, _) =>
        {
            var connection = new ConnectionInfo("test-connection", new string('A', 64), 99, "synthetic-logon", "test-epoch");
            if (!fixture.Available && request.Method is "get" or "describe" or "invoke")
                throw new AgentLinkException("disconnected", "The synthetic RDP transport is disconnected.");
            if (fixture.ReadinessErrorCode is { } readinessError && request.Method is "get" or "describe" or "invoke")
                throw new AgentLinkException(readinessError, "The synthetic endpoint refused the operation.");
            if (request.Method is "get" or "describe")
            {
                var target = Arguments.Read<TargetRequest>(request.Arguments);
                if (!target.CurrentSession && target.ConnectionId != connection.ConnectionId)
                    throw new AgentLinkException("disconnected", "The selected synthetic endpoint is disconnected.");
            }
            if (request.Method == "invoke")
            {
                var route = Arguments.Read<RoutedOperation>(request.Arguments);
                if (!route.CurrentSession && route.ConnectionId != connection.ConnectionId)
                    throw new AgentLinkException("disconnected", "The operation targeted a different synthetic endpoint.");
                fixture.RecordInvocation();
            }
            OperationResult result = request.Method switch
            {
                "list" => new(Data: Json.Element(fixture.Available ? new[] { connection } : [])),
                "get" => new(Data: Json.Element(connection)),
                "describe" => new(Data: Json.Element(new[]
                {
                    new ToolDefinition("take_screenshot", "Synthetic PNG for protocol testing only.",
                        Json.Element(new { type = "object", properties = new { }, additionalProperties = false }), false, "observe")
                })),
                "invoke" => new(Image: Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jHhQAAAAASUVORK5CYII="), MimeType: "image/png"),
                _ => throw new AgentLinkException("unknown_operation", "Unknown synthetic operation.")
            };
            return Task.FromResult(result);
        });
        await peer.Completion.WaitAsync(cancellationToken);
    }
}

internal sealed class BrokerFixtureState
{
    private int _available = 1;
    private int _invocations;
    private string? _readinessErrorCode;
    public bool Available { get => Volatile.Read(ref _available) != 0; set => Volatile.Write(ref _available, value ? 1 : 0); }
    public int Invocations => Volatile.Read(ref _invocations);
    public string? ReadinessErrorCode { get => Volatile.Read(ref _readinessErrorCode); set => Volatile.Write(ref _readinessErrorCode, value); }
    public void RecordInvocation() => Interlocked.Increment(ref _invocations);
}
