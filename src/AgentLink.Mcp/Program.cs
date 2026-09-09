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
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation { Name = "remote-agent-link", Version = "0.2.0" };
    options.Capabilities = new ServerCapabilities
    {
        Tools = new ToolsCapability { ListChanged = true }
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
        var result = await bridge.CallAsync(parameters.Name,
            Json.Element(parameters.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>()), ct);
        if (parameters.Name == "rdp_bind_connection" && result.IsError != true)
            await context.Server.SendNotificationAsync("notifications/tools/list_changed", cancellationToken: ct);
        return result;
    });
await builder.Build().RunAsync();
return 0;
