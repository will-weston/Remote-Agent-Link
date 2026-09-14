using AgentLink.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

await using var diagnostics = Diagnostics.Start(DiagnosticComponent.Mcp,
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteAgentLink", "diagnostics", "mcp"));
Diagnostics.Write(DiagnosticEvent.ProcessStarted);
try
{
    return await RunAsync(args);
}
catch (Exception exception)
{
    Diagnostics.Write(DiagnosticEvent.ProcessFailed, DiagnosticLevel.Error, outcome: "failed", exception: exception);
    Console.Error.WriteLine("Remote Agent Link MCP host failed. See private diagnostics for the failure category.");
    return 1;
}
finally
{
    Diagnostics.Write(DiagnosticEvent.ProcessStopped, outcome: "closed");
}

static async Task<int> RunAsync(string[] args)
{
    McpLaunchOptions launch;
    try
    {
        launch = McpLaunchOptions.Parse(args);
    }
    catch (ArgumentException exception)
    {
        Diagnostics.Write(DiagnosticEvent.ConfigurationInvalid, DiagnosticLevel.Warning, outcome: "failed", exception: exception);
        Console.Error.WriteLine(exception.Message);
        return 2;
    }

    var bridge = new McpBridge(launch.CurrentSession, launch.BrokerPipe, launch.ConnectionId);
    var builder = Host.CreateApplicationBuilder();
    // SDK/host messages can contain client payloads, so only explicit structured events are persisted.
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton(bridge);
    builder.Services.AddHostedService<McpPresenceService>();
    builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "remote-agent-link", Version = "0.5.0" };
        options.ServerInstructions = McpGuidance.ServerInstructions(launch);
        options.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability { ListChanged = false }
        };
        options.Handlers = new()
        {
            NotificationHandlers =
            [
                new(NotificationMethods.InitializedNotification, (_, _) =>
                {
                    bridge.ClientInitialized();
                    return ValueTask.CompletedTask;
                })
            ]
        };
    })
        .WithStdioServerTransport()
        .WithListToolsHandler(async (_, ct) => new ListToolsResult
        {
            Tools = (await bridge.ListToolsAsync(ct)).Select(McpBridge.ToProtocolTool).ToList()
        })
        .WithCallToolHandler(async (context, ct) =>
        {
            var parameters = context.Params ?? throw new ArgumentException("Missing tool parameters.");
            return await bridge.CallAsync(parameters.Name,
                Json.Element(parameters.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>()), ct);
        });
    await builder.Build().RunAsync();
    return 0;
}
