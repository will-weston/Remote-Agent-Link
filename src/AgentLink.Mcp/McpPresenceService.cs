using AgentLink.Core;
using Microsoft.Extensions.Hosting;

internal sealed class McpPresenceService(McpBridge bridge) : BackgroundService
{
    private bool _reportingFailed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialized = false;
        try
        {
            await bridge.WaitForClientAsync(stoppingToken);
            initialized = true;
            while (!stoppingToken.IsCancellationRequested)
            {
                await PublishAsync(true, stoppingToken);
                await bridge.WaitForPresenceChangeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            if (initialized)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await PublishAsync(false, timeout.Token);
            }
        }
    }

    private async Task PublishAsync(bool connected, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await bridge.UpdatePresenceAsync(connected, deadline.Token);
            if (_reportingFailed)
                Diagnostics.Write(DiagnosticEvent.McpPresenceRecovered, operation: "mcp.presence", outcome: "recovered");
            _reportingFailed = false;
        }
        catch (AgentLinkException exception) { Report(exception); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { Report(exception); }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            if (connected && cancellationToken.IsCancellationRequested) throw;
            Report(exception);
        }
    }

    private void Report(Exception exception)
    {
        // Heartbeats report presence only; a failed status update must not hide the tool catalog.
        if (!_reportingFailed)
            Diagnostics.Write(DiagnosticEvent.McpPresenceFailed, DiagnosticLevel.Warning,
                operation: "mcp.presence", outcome: "unavailable", exception: exception);
        _reportingFailed = true;
    }
}
