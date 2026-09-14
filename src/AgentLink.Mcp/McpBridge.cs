using System.Text.Json;
using System.Threading.Channels;
using AgentLink.Core;
using ModelContextProtocol.Protocol;

internal sealed class McpBridge(bool currentSession, string? brokerPipe = null, string? connectionId = null)
{
    private readonly BrokerClient _broker = new(brokerPipe);
    private readonly SemaphoreSlim _binding = new(1, 1);
    private readonly string _actorId = Guid.NewGuid().ToString("N");
    private string? _connectionId = connectionId;
    private IReadOnlyList<ToolDefinition> _remoteTools = [];
    private bool _catalogLoaded;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte> _presenceChanges = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
    });
    private static readonly IReadOnlyList<ToolDefinition> EndpointTools =
        Array.AsReadOnly(DesktopToolCatalog.All.Concat(AgentToolCatalog.All).ToArray());
    private static readonly HashSet<string> EndpointNames = EndpointTools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

    private static readonly ToolDefinition[] ManagementTools =
    [
        new("rdp_list_connections", "Discover local RDP plugin instances independently of DVC readiness, and authenticated remote Windows endpoints. " +
            "Inspect targetKind, state and localSurfaceStatus: a localRdpClient entry is discovery-only, not a ready remote broker or controllable surface. " +
            "No pairing is required. This is discovery only: listing a session does not bind it. " +
            "Next call rdp_bind_connection with the intended connectionId before using the already listed desktop and agent tools. Does not connect to or provision a machine.",
            Json.Element(new { type = "object", properties = new { }, additionalProperties = false }), false, "discovery"),
        new("rdp_bind_connection", "Bind this MCP process to one explicit connectionId and check its supported desktop and agent tools. " +
            "All tool declarations are stable and already listed; binding does not add or remove tools or require a catalog refresh. " +
            "Success reports endpoint support. The target's device-access switch still applies. " +
            "Binding cannot switch an already selected desktop. Rebinding the same ID refreshes its supported catalog.",
            Json.Element(new { type = "object", properties = new { connectionId = new { type = "string", minLength = 1, maxLength = 256,
                description = "The exact connectionId returned by rdp_list_connections, not a display name or hostname." } },
                required = new[] { "connectionId" }, additionalProperties = false }), true, "discovery"),
        new("rdp_get_connection", "Inspect the selected target's kind, lifecycle state, identity and epoch. This does not bind a discovered connection or grant permissions. " +
            "Local RDP plugin entries remain inspectable while disconnected until their reporter closes or expires. " +
            "Authenticated remote targets fail if disconnected; current-session mode reports the local session's connection state.",
            Json.Element(new { type = "object", properties = new { }, additionalProperties = false }), false, "discovery"),
        new("rdp_acquire_control", "Reserve mouse/keyboard control of the selected desktop without an approval dialog when device access is enabled. " +
            "Bind first unless configured for a fixed target or current session. Does not preempt another agent or enable a blocked device. " +
            "This does not reveal the tool catalog or replace binding.",
            Json.Element(new { type = "object", properties = new { }, additionalProperties = false }), true, "desktop-control"),
        new("rdp_release_control", "Release this MCP instance's desktop control grant. Does not release another agent's lease, " +
            "disconnect RDP or unbind this MCP process; the selected target stays fixed.",
            Json.Element(new { type = "object", properties = new { }, additionalProperties = false }), true, "desktop-control")
    ];

    public static Tool ToProtocolTool(ToolDefinition definition) => new()
    {
        Name = definition.Name,
        Description = definition.Description,
        InputSchema = definition.InputSchema,
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = !definition.Mutating,
            DestructiveHint = definition.Mutating,
            OpenWorldHint = true,
            IdempotentHint = false
        }
    };

    private static readonly IReadOnlyList<ToolDefinition> DeclaredTools = Array.AsReadOnly(ManagementTools
        .Concat(EndpointTools.Select(tool => tool with { Description = tool.Description + " " + McpGuidance.EndpointScope })).ToArray());

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DeclaredTools);
    }

    public void ClientInitialized() => _initialized.TrySetResult();

    public Task WaitForClientAsync(CancellationToken cancellationToken) => _initialized.Task.WaitAsync(cancellationToken);

    public Task<OperationResult> UpdatePresenceAsync(bool connected, CancellationToken cancellationToken) =>
        _broker.CallAsync("mcp.presence", new McpPresenceRequest(_actorId, _connectionId, currentSession, connected), cancellationToken);

    public async Task WaitForPresenceChangeAsync(CancellationToken cancellationToken)
    {
        using var interval = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        interval.CancelAfter(TimeSpan.FromSeconds(15));
        try { await _presenceChanges.Reader.ReadAsync(interval.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && interval.IsCancellationRequested) { }
    }

    private async Task<IReadOnlyList<ToolDefinition>> DescribeAsync(CancellationToken cancellationToken)
    {
        var result = await _broker.CallAsync("describe", new TargetRequest(_connectionId, currentSession), cancellationToken);
        return ReadSupportedTools(result);
    }

    private static IReadOnlyList<ToolDefinition> ReadSupportedTools(OperationResult result)
    {
        var data = result.Data ?? throw new AgentLinkException("invalid_response", "Peer returned no tool catalog.");
        ToolDefinition[]? tools;
        try { tools = data.Deserialize<ToolDefinition[]>(Json.Options); }
        catch (JsonException) { throw new AgentLinkException("invalid_response", "Peer returned an invalid tool catalog."); }
        if (tools is null || tools.Any(tool => tool is null || string.IsNullOrWhiteSpace(tool.Name) ||
            tool.InputSchema.ValueKind != JsonValueKind.Object) ||
            tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Length)
            throw new AgentLinkException("invalid_response", "Peer returned an invalid or duplicate tool definition.");
        // A remote peer cannot inject new model-callable tools into this process.
        return Array.AsReadOnly(tools.Where(tool => EndpointNames.Contains(tool.Name)).ToArray());
    }

    private async Task RequireSupportedToolAsync(string name, CancellationToken cancellationToken)
    {
        if (!EndpointNames.Contains(name))
            throw new AgentLinkException("unknown_tool", "This MCP server does not declare that tool.");
        await _binding.WaitAsync(cancellationToken);
        try
        {
            if (!Volatile.Read(ref _catalogLoaded))
            {
                _remoteTools = await DescribeAsync(cancellationToken);
                Volatile.Write(ref _catalogLoaded, true);
            }
            if (!_remoteTools.Any(tool => tool.Name == name))
                throw new AgentLinkException("unsupported_on_endpoint", "The selected endpoint does not support this declared tool. " +
                    "Do not switch desktops or use a local shell or coding agent as a workaround.");
        }
        finally { _binding.Release(); }
    }

    public async Task<CallToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        try
        {
            OperationResult result;
            string? guidance = null;
            if (name == "rdp_list_connections")
            {
                RequireEmpty(arguments);
                result = await _broker.CallAsync("list", new { }, cancellationToken);
            }
            else if (name == "rdp_bind_connection")
            {
                if (currentSession)
                    throw new AgentLinkException("binding_fixed", "This MCP process is configured for its own Windows session.");
                var bind = Arguments.Read<BindArguments>(arguments);
                var id = Arguments.Required(bind.ConnectionId, "connectionId", 256);
                await _binding.WaitAsync(cancellationToken);
                try
                {
                    if (_connectionId is not null && _connectionId != id)
                        throw new AgentLinkException("binding_fixed", "Start a separate MCP process to target another connection.");
                    result = await _broker.CallAsync("get", new TargetRequest(id), cancellationToken);
                    // Obtain the catalog before committing a binding.
                    var catalog = await _broker.CallAsync("describe", new TargetRequest(id), cancellationToken);
                    var tools = ReadSupportedTools(catalog);
                    var localClient = result.Data is { } identity && identity.TryGetProperty("targetKind", out var kind) &&
                        kind.GetString() == "localRdpClient";
                    guidance = McpGuidance.BindingSummary(tools, localClient);
                    _remoteTools = tools;
                    _connectionId = id;
                    Volatile.Write(ref _catalogLoaded, true);
                    _presenceChanges.Writer.TryWrite(0);
                }
                finally
                {
                    _binding.Release();
                }
            }
            else
            {
                if (_connectionId is null && !currentSession)
                    throw new AgentLinkException("not_bound", McpGuidance.NotBound);
                if (name == "rdp_get_connection")
                {
                    RequireEmpty(arguments);
                    result = await _broker.CallAsync("get", new TargetRequest(_connectionId, currentSession), cancellationToken);
                }
                else if (name is "rdp_acquire_control" or "rdp_release_control")
                {
                    RequireEmpty(arguments);
                    result = await _broker.CallAsync("invoke",
                        new RoutedOperation(_connectionId, currentSession,
                            new(name == "rdp_acquire_control" ? "desktop.acquire" : "desktop.release", arguments, _actorId)), cancellationToken);
                }
                else
                {
                    await RequireSupportedToolAsync(name, cancellationToken);
                    result = await _broker.CallAsync("invoke",
                        new RoutedOperation(_connectionId, currentSession, new(name, arguments, _actorId)), cancellationToken);
                }
            }
            return ToResult(result, guidance);
        }
        catch (AgentLinkException exception)
        {
            if (exception.Code is "disconnected" or "untrusted_peer" or "pairing_required" or "invalid_response")
                Volatile.Write(ref _catalogLoaded, false);
            return Error(exception.Code, exception.Message + " " + McpGuidance.FailureBoundary);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            Volatile.Write(ref _catalogLoaded, false);
            return Error("connection_unavailable",
                "The broker or remote connection is unavailable. Start the broker and reconnect. An unacknowledged mutation may have executed. " +
                McpGuidance.FailureBoundary);
        }
    }

    private static void RequireEmpty(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
            throw new AgentLinkException("invalid_arguments", "This operation accepts no arguments.");
    }

    internal static CallToolResult ToResult(OperationResult result, string? guidance = null)
    {
        List<ContentBlock> content = [];
        if (result.Image is { } image)
            content.Add(ImageContentBlock.FromBytes(image, result.MimeType ?? "image/png"));
        if (result.Text is { } text)
            content.Add(new TextContentBlock { Text = text });
        if (result.Data is { } data)
            content.Add(new TextContentBlock { Text = data.GetRawText() });
        if (guidance is not null)
            content.Add(new TextContentBlock { Text = guidance });
        return new CallToolResult { Content = content, StructuredContent = result.Data };
    }

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"{code}: {message}" }],
        StructuredContent = Json.Element(new { code, message })
    };

    private sealed record BindArguments(string ConnectionId);
}
