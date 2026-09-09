using System.Text.Json;
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
    private string? _catalogError;

    private static readonly ToolDefinition[] ManagementTools =
    [
        new("rdp_list_connections", "List paired, connected remote Windows sessions. Does not connect to or provision a machine.",
            Json.Element(new { type = "object", properties = new { }, additionalProperties = false }), false, "discovery"),
        new("rdp_bind_connection", "Bind this MCP process to one explicit connection ID. Binding cannot silently switch desktops.",
            Json.Element(new { type = "object", properties = new { connectionId = new { type = "string" } }, required = new[] { "connectionId" }, additionalProperties = false }), true, "discovery"),
        new("rdp_get_connection", "Get the bound connection's identity and epoch. Fails if disconnected.",
            Json.Element(new { type = "object", properties = new { }, additionalProperties = false }), false, "discovery"),
        new("rdp_acquire_control", "Request human approval to control the bound remote desktop. The human can revoke control at any time.",
            Json.Element(new { type = "object", properties = new { }, additionalProperties = false }), true, "desktop-control"),
        new("rdp_release_control", "Release this MCP instance's desktop control grant. Does not release another agent's lease.",
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

    public async Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        if (currentSession || _connectionId is not null)
        {
            await _binding.WaitAsync(cancellationToken);
            try
            {
                if (!_catalogLoaded)
                {
                    try
                    {
                        _remoteTools = await DescribeAsync(cancellationToken);
                        _catalogLoaded = true;
                        if (_catalogError is not null)
                            Console.Error.WriteLine("The selected endpoint is available; its tool catalog is loaded.");
                        _catalogError = null;
                    }
                    catch (Exception exception) when (exception is IOException or TimeoutException ||
                        exception is AgentLinkException { Code: "disconnected" })
                    {
                        ReportCatalogError("The selected endpoint is unavailable. Connection management tools remain available; " +
                            "reconnect and bind the same connection, or refresh the tool list. No other desktop will be selected.");
                    }
                    catch (AgentLinkException exception) when (exception.Code is "untrusted_peer" or "pairing_required")
                    {
                        ReportCatalogError("The selected endpoint requires pairing. Open Remote Agent Link on both devices and " +
                            "compare their fingerprints before approving the connection. Connection management tools remain available; " +
                            "no desktop or agent permission has been granted.");
                    }
                }
            }
            finally { _binding.Release(); }
        }
        return ManagementTools.Concat(_remoteTools).ToArray();
    }

    private void ReportCatalogError(string message)
    {
        if (_catalogError != message)
            Console.Error.WriteLine(message);
        _catalogError = message;
    }

    private async Task<IReadOnlyList<ToolDefinition>> DescribeAsync(CancellationToken cancellationToken)
    {
        var result = await _broker.CallAsync("describe", new TargetRequest(_connectionId, currentSession), cancellationToken);
        return Arguments.Read<ToolDefinition[]>(result.Data
            ?? throw new AgentLinkException("invalid_response", "Peer returned no tool catalog."));
    }

    public async Task<CallToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        try
        {
            OperationResult result;
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
                    _remoteTools = Arguments.Read<ToolDefinition[]>(catalog.Data
                        ?? throw new AgentLinkException("invalid_response", "Peer returned no tool catalog."));
                    _connectionId = id;
                    _catalogLoaded = true;
                    _catalogError = null;
                }
                finally
                {
                    _binding.Release();
                }
            }
            else
            {
                if (_connectionId is null && !currentSession)
                    throw new AgentLinkException("not_bound", "Call rdp_list_connections then rdp_bind_connection first.");
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
                    if (!_remoteTools.Any(tool => tool.Name == name))
                        throw new AgentLinkException("unknown_tool", "This tool is not available on the bound endpoint.");
                    result = await _broker.CallAsync("invoke",
                        new RoutedOperation(_connectionId, currentSession, new(name, arguments, _actorId)), cancellationToken);
                }
            }
            return ToResult(result);
        }
        catch (AgentLinkException exception)
        {
            return Error(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Error("connection_unavailable",
                "The broker or remote connection is unavailable. Start the broker and reconnect. An unacknowledged mutation may have executed.");
        }
    }

    private static void RequireEmpty(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
            throw new AgentLinkException("invalid_arguments", "This operation accepts no arguments.");
    }

    internal static CallToolResult ToResult(OperationResult result)
    {
        List<ContentBlock> content = [];
        if (result.Image is { } image)
            content.Add(ImageContentBlock.FromBytes(image, result.MimeType ?? "image/png"));
        if (result.Text is { } text)
            content.Add(new TextContentBlock { Text = text });
        if (result.Data is { } data)
            content.Add(new TextContentBlock { Text = data.GetRawText() });
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
