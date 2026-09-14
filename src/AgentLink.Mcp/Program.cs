using AgentLink.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

McpLaunchOptions launch;
try
{
    launch = McpLaunchOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

var bridge = new McpBridge(launch.CurrentSession, launch.BrokerPipe, launch.ConnectionId);
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
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
