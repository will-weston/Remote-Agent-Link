using System.Diagnostics;
using System.Text.Json;

internal sealed class StdioTestClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _diagnostics;
    private int _requestId;

    public bool NotificationSeen { get; private set; }

    public StdioTestClient(string hostPath, params string[] arguments)
    {
        _process = Process.Start(CreateStartInfo(hostPath, arguments))
            ?? throw new InvalidOperationException("MCP host did not start.");
        _diagnostics = _process.StandardError.ReadToEndAsync();
    }

    internal static ProcessStartInfo CreateStartInfo(string hostPath, string[] arguments)
    {
        var executable = Path.GetExtension(hostPath).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(executable ? Path.GetFullPath(hostPath) : "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!executable)
            start.ArgumentList.Add(Path.GetFullPath(hostPath));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    public async Task<JsonElement> InitializeAsync()
    {
        var result = await RequestAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "AgentLink integration", version = "2" }
        });
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        }));
        await _process.StandardInput.FlushAsync();
        return result;
    }

    public Task<JsonElement> CallAsync(string name, object arguments) =>
        RequestAsync("tools/call", new { name, arguments });

    public async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = ++_requestId;
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        }));
        await _process.StandardInput.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException("MCP stdout closed before its response.");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("method", out var notification))
            {
                NotificationSeen |= notification.GetString() == "notifications/tools/list_changed";
                continue;
            }
            if (root.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                return root.Clone();
            throw new InvalidOperationException("Unexpected MCP response.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
                throw new InvalidOperationException("MCP host did not exit after stdin closed.");
            }
            if (_process.ExitCode != 0)
                throw new InvalidOperationException($"MCP host exited with {_process.ExitCode}: {await _diagnostics}");
        }
        finally { _process.Dispose(); }
    }
}
