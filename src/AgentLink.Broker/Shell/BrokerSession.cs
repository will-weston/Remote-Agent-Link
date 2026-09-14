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
                throw new AgentLinkException("broker_stopping", "Device access cannot be changed while the broker is stopping.");
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
    private readonly string _directory;
    private readonly string? _legacyDirectory;

    internal DesktopAccessSettings(string directory, string? legacyDirectory = null)
    {
        EndpointIdentity.EnsurePrivateDirectory(directory);
        _path = Path.Combine(directory, FileName);
        _directory = directory;
        _legacyDirectory = legacyDirectory;
    }

    internal bool Read()
    {
        try
        {
            using var settingsLock = AcquireLock();
            var current = ReadFile(_path);
            if (current is not null) return current.Value;
            // The first new-version startup merges previous logon preferences.
            // Any explicit No wins; malformed/unreadable legacy files fail closed.
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_legacyDirectory is not null) directories.Add(_legacyDirectory);
            var sessions = Path.Combine(_directory, "sessions");
            try
            {
                if ((File.GetAttributes(sessions) & FileAttributes.Directory) == 0)
                    throw new IOException("The legacy sessions path is not a directory.");
                foreach (var directory in Directory.EnumerateDirectories(sessions)) directories.Add(directory);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var enabled = true;
            foreach (var directory in directories)
            {
                var legacy = ReadFile(Path.Combine(directory, FileName));
                if (legacy is null) continue;
                enabled &= legacy.Value;
            }
            WriteFile(enabled);
            return enabled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentLinkException("desktop_access_settings_unavailable",
                "Existing device preferences could not be migrated. Agent access remains off. Repair private device storage before restarting.");
        }
    }

    internal bool ReadCurrent()
    {
        try
        {
            using var settingsLock = AcquireLock();
            return ReadFile(_path) ?? throw InvalidSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentLinkException("desktop_access_settings_unavailable",
                "The shared device preference is unavailable. Agent access remains off until the setting can be read.");
        }
    }

    private static bool? ReadFile(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
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
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { throw InvalidSettings(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentLinkException("desktop_access_settings_unavailable",
                "The saved device access setting could not be read. Agent access remains off. Check this user's private device storage before restarting.");
        }
    }

    internal void Save(bool enabled)
    {
        try
        {
            using var settingsLock = AcquireLock();
            WriteFile(enabled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentLinkException("desktop_access_persistence_failed", enabled
                ? "Agent access was not changed because its setting could not be saved. Check private device storage and try again."
                : "Agent access is off now, but this choice could not be saved. Check private device storage and retry before restarting the broker.");
        }
    }

    private void WriteFile(bool enabled)
    {
        var pending = _path + $".{Guid.NewGuid():N}.pending";
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
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    private FileStream AcquireLock()
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) == 32 && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(25);
            }
        }
    }

    private static AgentLinkException InvalidSettings() =>
        new("desktop_access_settings_invalid",
            "The saved device access setting is malformed or unsupported. Agent access remains off. Repair the device setting before restarting the broker.");
}
