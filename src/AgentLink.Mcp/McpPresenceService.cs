using AgentLink.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed class McpPresenceService(McpBridge bridge, ILogger<McpPresenceService> logger) : BackgroundService
{
    private string? _lastFailure;

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
            if (_lastFailure is not null)
                logger.LogInformation("Agent connection status reporting recovered.");
            _lastFailure = null;
        }
        catch (AgentLinkException exception) { Report(exception.Code); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { Report("connection_unavailable"); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (connected && cancellationToken.IsCancellationRequested) throw;
            Report("status_update_timeout");
        }
    }

    private void Report(string code)
    {
        // Heartbeats report presence only; a failed status update must not hide the tool catalog.
        if (_lastFailure != code)
            logger.LogWarning("Could not update agent connection status ({Code}). Tool access is unchanged.", code);
        _lastFailure = code;
    }
}
