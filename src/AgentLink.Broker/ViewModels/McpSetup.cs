using System.Diagnostics;
using System.Text.Json;

namespace AgentLink.Broker.ViewModels;

internal enum McpRegistration { Missing, Saved, Custom, Invalid }

internal static class McpSetup
{
    internal static string ProfileDirectory => Path.GetFullPath(
        Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } directory ? directory :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot"));
    internal static string ConfigurationPath => Path.Combine(ProfileDirectory, "mcp-config.json");
    internal static string AliasPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "ralink-mcp.exe");
    internal static string SetupCommand => $"copilot mcp add remote-agent-link -- \"{AliasPath}\"";

    internal static McpRegistration Inspect(string path, string aliasPath)
    {
        FileStream opened;
        try { opened = File.OpenRead(path); }
        catch (FileNotFoundException) { return McpRegistration.Missing; }
        catch (DirectoryNotFoundException) { return McpRegistration.Missing; }
        using var stream = opened;
        if (stream.Length > 1024 * 1024) return McpRegistration.Invalid;
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return McpRegistration.Invalid;
            if (!root.TryGetProperty("mcpServers", out var servers)) return McpRegistration.Missing;
            if (servers.ValueKind != JsonValueKind.Object) return McpRegistration.Invalid;
            if (!servers.TryGetProperty("remote-agent-link", out var server)) return McpRegistration.Missing;
            if (server.ValueKind != JsonValueKind.Object) return McpRegistration.Invalid;
            if (!server.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String ||
                !server.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return McpRegistration.Custom;
            if (type.GetString() is not ("local" or "stdio") ||
                !(string.Equals(command.GetString(), "ralink-mcp.exe", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(command.GetString(), aliasPath, StringComparison.OrdinalIgnoreCase)))
                return McpRegistration.Custom;
            if (server.TryGetProperty("args", out var args) &&
                (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() != 0))
                return McpRegistration.Custom;
            return McpRegistration.Saved;
        }
        catch (JsonException) { return McpRegistration.Invalid; }
    }

    internal static async Task RegisterAsync(CancellationToken cancellationToken)
    {
        if (Inspect(ConfigurationPath, AliasPath) != McpRegistration.Missing)
            throw new InvalidOperationException("An existing or unreadable configuration was left unchanged. Use Copilot's /mcp settings to review it.");
        if (!File.Exists(AliasPath))
            throw new FileNotFoundException("The installed ralink-mcp.exe alias is unavailable. Enable it in Windows App execution aliases.");
        var start = new ProcessStartInfo("copilot.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.Environment["COPILOT_HOME"] = ProfileDirectory;
        foreach (var argument in new[] { "mcp", "add", "remote-agent-link", "--", AliasPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Copilot CLI did not start.");
        process.StandardInput.Close();
        // Drain, but never retain or surface CLI output that might contain configuration values.
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("Copilot setup was interrupted. Refresh its configuration before retrying; it may already have been saved.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Copilot setup exited with code {process.ExitCode}. Run the displayed command in your terminal for details.");
        if (Inspect(ConfigurationPath, AliasPath) != McpRegistration.Saved)
            throw new InvalidOperationException("Copilot did not save the expected MCP definition. Open /mcp in your agent to review its configuration.");
    }
}
