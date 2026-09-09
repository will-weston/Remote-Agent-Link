using System.Text.Json;
using AgentLink.Core;

namespace AgentLink.Broker.Shell;

internal sealed class BrokerSession(
    EndpointIdentity identity, HumanConsent consent, BrokerHost host, string dataDirectory, string role)
{
    private readonly object _gate = new();
    private Task? _stopping;
    public EndpointIdentity Identity { get; } = identity;
    public HumanConsent Consent { get; } = consent;
    public BrokerHost Host { get; } = host;
    public string DataDirectory { get; } = dataDirectory;
    public string Role { get; } = role;

    public void SetDesktopAccessEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_stopping is not null)
                throw new AgentLinkException("broker_stopping", "Desktop availability cannot be changed while the broker is stopping.");
            Host.SetDesktopAccessEnabled(enabled);
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
            return _stopping ??= Task.Run(async () => await Host.DisposeAsync());
    }
}

internal sealed class DesktopAccessSettings
{
    internal const string FileName = "desktop-access.json";
    internal const int MaximumBytes = 512;
    private readonly string _path;

    internal DesktopAccessSettings(string directory)
    {
        EndpointIdentity.EnsurePrivateDirectory(directory);
        _path = Path.Combine(directory, FileName);
    }

    internal bool Read()
    {
        try
        {
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is <= 0 or > MaximumBytes)
                throw InvalidSettings();
            var bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw InvalidSettings();
            return enabled.GetBoolean();
        }
        catch (FileNotFoundException) { return true; }
        catch (JsonException) { throw InvalidSettings(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentLinkException("desktop_access_settings_unavailable",
                "The saved desktop availability setting could not be read. Desktop access remains off. Check this session's private storage before restarting.");
        }
    }

    internal void Save(bool enabled)
    {
        var pending = _path + $".{Guid.NewGuid():N}.pending";
        try
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, enabled }, Json.Options);
                using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }
                File.Move(pending, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(pending))
                    File.Delete(pending);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentLinkException("desktop_access_persistence_failed", enabled
                ? "Desktop access was not changed because its setting could not be saved. Check this session's private storage and try again."
                : "Desktop access is off now, but this choice could not be saved. Check this session's private storage and retry before restarting the broker.");
        }
    }

    private static AgentLinkException InvalidSettings() =>
        new("desktop_access_settings_invalid",
            "The saved desktop availability setting is malformed or unsupported. Desktop access remains off. Repair the session setting before restarting the broker.");
}
