using System.ComponentModel;
using System.Diagnostics.Tracing;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace AgentLink.Core;

public enum DiagnosticComponent { Broker, Mcp }
public enum DiagnosticLevel { Debug, Information, Warning, Error, Off }
public enum DiagnosticEvent
{
    ProcessStarted, ProcessStopped, ProcessFailed, ConfigurationInvalid,
    BrokerStarted, BrokerStopped, ListenerFailed, ConnectionFailed,
    ChannelOpened, ChannelAuthenticated, ChannelReady, ChannelClosed, TransportWaiting,
    ClientLifecycle, ClientRemoved, SurfaceChanged, SurfaceLost, SurfaceFailed,
    DeviceAccessChanged, DeviceSettingsFailed, DeviceSettingsRecovered, ControlRevoked,
    ObserverFailed, McpInitialized, McpBound, McpPresenceFailed, McpPresenceRecovered,
    OperationStarted, OperationCompleted, OperationFailed, OperationCancelled, RpcFailed, RpcDeliveryFailed,
    LoggingSummary
}

// Only fixed categories and explicitly allowlisted values cross the logging boundary.
// In particular, neither exception messages nor arbitrary protocol strings are records.
public static class Diagnostics
{
    private static DiagnosticLog? _current;
    private static readonly HashSet<string> Operations = DesktopToolCatalog.All.Concat(AgentToolCatalog.All)
        .Select(tool => tool.Name).Concat([
            "rdp_list_connections", "rdp_bind_connection", "rdp_get_connection", "rdp_acquire_control",
            "rdp_release_control", "desktop.acquire", "desktop.release", "protocol.hello", "protocol.ping",
            "tools.list", "mcp.presence", "list", "get", "describe", "invoke", "ui.show"
        ]).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> Outcomes = new(StringComparer.Ordinal)
    {
        "ok", "failed", "cancelled", "connected", "disconnected", "initialized", "local", "remote",
        "current_session", "discovery", "fixed", "enabled", "disabled", "pending", "service_unavailable",
        "service_error", "unsupported_session", "invalid_geometry", "identity_ambiguous", "unavailable",
        "recovered", "ready", "closed", "both", "unsupported_public_contract"
    };
    private static readonly HashSet<string> ErrorCodes = new(StringComparer.Ordinal)
    {
        "not_bound", "binding_fixed", "unsupported_on_endpoint", "device_access_disabled", "disconnected",
        "connection_unavailable", "desktop_busy", "not_control_owner", "permission_denied", "invalid_actor",
        "invalid_arguments", "unknown_tool", "unknown_operation", "invalid_response", "operation_failed",
        "cancelled", "busy", "protocol_mismatch", "invalid_metadata", "invalid_frame", "duplicate_request",
        "duplicate_connection", "invalid_client_report", "invalid_surface_report", "duplicate_surface_report",
        "too_many_connections", "channel_not_ready", "session_zero", "broker_stopping", "ui_unavailable",
        "desktop_access_settings_unavailable", "desktop_access_settings_invalid", "desktop_access_persistence_failed",
        "observation_required", "stale_observation", "desktop_unavailable", "protected_window", "protected_content",
        "session_busy", "runtime_not_configured", "runtime_unavailable", "outcome_unknown", "status_update_timeout"
    };

    public static string? DirectoryPath => Volatile.Read(ref _current)?.DirectoryPath;
    public static string Status
    {
        get
        {
            var log = Volatile.Read(ref _current);
            return log is null ? "Not started" :
                $"{log.Status}. Level: {log.MinimumLevel}; rate-limited: {log.SuppressedCount}; dropped: {log.DroppedCount}.";
        }
    }

    public static DiagnosticLog Start(DiagnosticComponent component, string directory)
    {
        var setting = Environment.GetEnvironmentVariable("RALINK_LOG_LEVEL");
        var level = setting?.ToLowerInvariant() switch
        {
            "off" => DiagnosticLevel.Off,
            "warning" => DiagnosticLevel.Warning,
            "debug" => DiagnosticLevel.Debug,
            _ => DiagnosticLevel.Information
        };
        var log = new DiagnosticLog(component, directory, level);
        if (Interlocked.CompareExchange(ref _current, log, null) is not null)
        {
            log.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("Process diagnostics are already initialized.");
        }
        if (setting is not (null or "" or "info" or "warning" or "debug" or "off") &&
            setting.ToLowerInvariant() is not ("info" or "warning" or "debug" or "off"))
            Write(DiagnosticEvent.ConfigurationInvalid, DiagnosticLevel.Warning);
        return log;
    }

    public static void Write(DiagnosticEvent eventId, DiagnosticLevel level = DiagnosticLevel.Information,
        string? operation = null, string? outcome = null, Guid? correlation = null,
        long? durationMs = null, Exception? exception = null) =>
        Volatile.Read(ref _current)?.Write(eventId, level, operation, outcome, correlation, durationMs, exception);

    public static string SafeOperation(string? operation) =>
        operation is not null && Operations.Contains(operation) ? operation : "unknown";

    public static string SafeErrorCode(Exception exception) => exception switch
    {
        AgentLinkException fault when ErrorCodes.Contains(fault.Code) => fault.Code,
        AgentLinkException => "other_agent_error",
        OperationCanceledException => "cancelled",
        AuthenticationException => "authentication_failed",
        UnauthorizedAccessException => "access_denied",
        TimeoutException => "timeout",
        IOException => "io_error",
        Win32Exception => "win32_error",
        ArgumentException => "invalid_arguments",
        ObjectDisposedException => "disposed",
        _ => "unexpected_error"
    };

    internal static string? SafeOutcome(string? outcome) =>
        outcome is null ? null : Outcomes.Contains(outcome) ? outcome : "unknown";
}

internal sealed record DiagnosticLogOptions
{
    internal int QueueCapacity { get; init; } = 512;
    internal int MaximumFileBytes { get; init; } = 1024 * 1024;
    internal int RetainedFiles { get; init; } = 3;
    internal int Slots { get; init; } = 4;
    internal int MaximumPerEventPerMinute { get; init; } = 8;
    internal int MaximumPerMinute { get; init; } = 120;
    internal TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    internal Func<string, FileMode, FileStream>? OpenOutput { get; init; }
}

public sealed class DiagnosticLog : IAsyncDisposable
{
    private sealed record Entry(DateTimeOffset Timestamp, Guid Run, int Pid, DiagnosticComponent Component,
        DiagnosticLevel Level, DiagnosticEvent Event, string? Operation = null, string? Outcome = null,
        Guid? Correlation = null, long? DurationMs = null, string? ErrorCode = null, int? Hresult = null,
        int? Win32Code = null, Guid? Build = null, long? Suppressed = null, long? Dropped = null, int Schema = 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly DiagnosticComponent _component;
    private readonly DiagnosticLevel _level;
    private readonly DiagnosticLogOptions _options;
    private readonly Channel<Entry> _queue;
    private readonly Guid _run = Guid.NewGuid();
    private readonly object _rateGate = new();
    private readonly int[] _eventCounts = new int[Enum.GetValues<DiagnosticEvent>().Length];
    private readonly TaskCompletionSource _closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writer;
    private long _windowStarted;
    private int _windowCount;
    private long _suppressed;
    private long _dropped;
    private long _pendingSuppressed;
    private long _pendingDropped;
    private int _disposed;
    private string _status;

    public DiagnosticLog(DiagnosticComponent component, string directory, DiagnosticLevel level = DiagnosticLevel.Information)
        : this(component, directory, level, new()) { }

    internal DiagnosticLog(DiagnosticComponent component, string directory, DiagnosticLevel level, DiagnosticLogOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _component = component;
        _level = level;
        _options = options;
        DirectoryPath = Path.GetFullPath(directory);
        _windowStarted = options.Clock.GetTimestamp();
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
        });
        _status = level == DiagnosticLevel.Off ? "Disabled (RALINK_LOG_LEVEL)" : "Starting";
        _writer = level == DiagnosticLevel.Off ? Task.CompletedTask : Task.Run(WriteLoopAsync);
    }

    public string DirectoryPath { get; }
    public DiagnosticLevel MinimumLevel => _level;
    public string Status => Volatile.Read(ref _status);
    public long SuppressedCount => Interlocked.Read(ref _suppressed);
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Write(DiagnosticEvent eventId, DiagnosticLevel level = DiagnosticLevel.Information,
        string? operation = null, string? outcome = null, Guid? correlation = null,
        long? durationMs = null, Exception? exception = null)
    {
        if (level < _level || _level == DiagnosticLevel.Off || Volatile.Read(ref _disposed) != 0 ||
            !Enum.IsDefined(eventId) || !Enum.IsDefined(level) || level == DiagnosticLevel.Off)
            return;
        lock (_rateGate)
        {
            var now = _options.Clock.GetTimestamp();
            if (_options.Clock.GetElapsedTime(_windowStarted, now) >= TimeSpan.FromMinutes(1))
            {
                _windowStarted = now;
                _windowCount = 0;
                Array.Clear(_eventCounts);
            }
            if (_windowCount >= _options.MaximumPerMinute ||
                _eventCounts[(int)eventId] >= _options.MaximumPerEventPerMinute)
            {
                Interlocked.Increment(ref _suppressed);
                Interlocked.Increment(ref _pendingSuppressed);
                return;
            }
            _windowCount++;
            _eventCounts[(int)eventId]++;
        }
        var entry = new Entry(_options.Clock.GetUtcNow(), _run, Environment.ProcessId, _component, level, eventId,
            operation is null ? null : Diagnostics.SafeOperation(operation), Diagnostics.SafeOutcome(outcome), correlation,
            durationMs is null ? null : Math.Clamp(durationMs.Value, 0, 86_400_000),
            exception is null ? null : Diagnostics.SafeErrorCode(exception), exception?.HResult,
            (exception as Win32Exception)?.NativeErrorCode,
            eventId == DiagnosticEvent.ProcessStarted ? typeof(Diagnostics).Assembly.ManifestModule.ModuleVersionId : null);
        if (!_queue.Writer.TryWrite(entry)) Dropped();
    }

    private void Dropped()
    {
        Interlocked.Increment(ref _dropped);
        Interlocked.Increment(ref _pendingDropped);
    }

    private async Task WriteLoopAsync()
    {
        FileStream? lease = null;
        FileStream? output = null;
        string? prefix = null;
        var failed = false;
        try
        {
            // All filesystem activity, serialization and ETW fallback happens on this worker.
            // Producers only sanitize a fixed-size record and try a bounded queue.
            RejectReparsePoint(DirectoryPath);
            PrivateStorage.EnsureDirectory(DirectoryPath);
            for (var slot = 0; slot < _options.Slots; slot++)
            {
                var candidate = Path.Combine(DirectoryPath, $"slot-{slot}");
                RejectReparsePoint(candidate + ".lock");
                try
                {
                    lease = new FileStream(candidate + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                    prefix = candidate;
                    break;
                }
                catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33) { }
            }
            if (lease is null) throw new IOException("All diagnostic writer slots are occupied.");
            output = OpenOutput(prefix!);
            Volatile.Write(ref _status, "Writing bounded local logs");
            while (true)
            {
                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                await Task.WhenAny(_closing.Task, Task.Delay(_options.FlushInterval, delayCancellation.Token));
                await delayCancellation.CancelAsync();
                _stop.Token.ThrowIfCancellationRequested();
                for (var count = 0; count < _options.QueueCapacity && _queue.Reader.TryRead(out var entry); count++)
                    output = await AppendAsync(output, prefix!, entry);
                var suppressed = Interlocked.Exchange(ref _pendingSuppressed, 0);
                var dropped = Interlocked.Exchange(ref _pendingDropped, 0);
                if (suppressed != 0 || dropped != 0)
                    output = await AppendAsync(output, prefix!, new(_options.Clock.GetUtcNow(), _run, Environment.ProcessId,
                        _component, DiagnosticLevel.Warning, DiagnosticEvent.LoggingSummary, Suppressed: suppressed, Dropped: dropped));
                await output.FlushAsync(_stop.Token);
                if (_closing.Task.IsCompleted && _queue.Reader.Completion.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            failed = true;
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) Dropped();
            Volatile.Write(ref _status, "File logging unavailable; see RemoteAgentLink-Diagnostics ETW");
            DiagnosticEvents.Instance.SinkUnavailable(exception.HResult);
            try { Console.Error.WriteLine($"Remote Agent Link diagnostic file logging unavailable (HRESULT=0x{exception.HResult:X8})."); }
            catch (Exception sinkFailure) when (sinkFailure is IOException or ObjectDisposedException or InvalidOperationException)
            {
                DiagnosticEvents.Instance.SinkUnavailable(sinkFailure.HResult);
            }
            // Disable this sink for the process instead of retrying failing disk I/O on a hot path.
        }
        finally
        {
            try { output?.Dispose(); }
            catch (IOException exception) { DiagnosticEvents.Instance.SinkUnavailable(exception.HResult); }
            lease?.Dispose();
            if (!failed) Volatile.Write(ref _status, "Stopped");
        }
    }

    private FileStream OpenOutput(string prefix)
    {
        for (var index = 0; index < _options.RetainedFiles; index++) RejectReparsePoint($"{prefix}.{index}.jsonl");
        var path = prefix + ".0.jsonl";
        var output = CreateOutput(path, FileMode.OpenOrCreate);
        try
        {
            var partial = false;
            if (output.Length > 0)
            {
                output.Seek(-1, SeekOrigin.End);
                partial = output.ReadByte() != '\n';
            }
            if (output.Length > _options.MaximumFileBytes || partial)
            {
                output.Dispose();
                Rotate(prefix);
                output = CreateOutput(path, FileMode.CreateNew);
            }
            output.Seek(0, SeekOrigin.End);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private FileStream CreateOutput(string path, FileMode mode) => _options.OpenOutput?.Invoke(path, mode) ??
        new FileStream(path, mode, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);

    private async Task<FileStream> AppendAsync(FileStream output, string prefix, Entry entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        if (bytes.Length + 1 > _options.MaximumFileBytes) { Dropped(); return output; }
        FileStream? replacement = null;
        try
        {
            if (output.Length + bytes.Length + 1 > _options.MaximumFileBytes)
            {
                await output.DisposeAsync();
                Rotate(prefix);
                replacement = OpenOutput(prefix);
                output = replacement;
            }
            await output.WriteAsync(bytes, _stop.Token);
            await output.WriteAsync(new byte[] { (byte)'\n' }, _stop.Token);
            return output;
        }
        catch
        {
            // The caller still owns the old stream until this method returns.
            if (replacement is not null) await replacement.DisposeAsync();
            throw;
        }
    }

    private void Rotate(string prefix)
    {
        for (var index = _options.RetainedFiles - 1; index >= 0; index--) RejectReparsePoint($"{prefix}.{index}.jsonl");
        File.Delete($"{prefix}.{_options.RetainedFiles - 1}.jsonl");
        for (var index = _options.RetainedFiles - 2; index >= 0; index--)
            if (File.Exists($"{prefix}.{index}.jsonl"))
                File.Move($"{prefix}.{index}.jsonl", $"{prefix}.{index + 1}.jsonl", overwrite: true);
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Diagnostic paths must not be reparse points.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _closing.TrySetResult();
        try
        {
            await _writer.WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
            _stop.Dispose();
        }
        catch (TimeoutException)
        {
            Volatile.Write(ref _status, "Shutdown flush timed out; records may be lost");
            DiagnosticEvents.Instance.SinkUnavailable(unchecked((int)0x800705B4));
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        // Do not dispose the CTS while a timed-out filesystem operation can still resume.
    }
}

[EventSource(Name = "RemoteAgentLink-Diagnostics")]
internal sealed class DiagnosticEvents : EventSource
{
    internal static readonly DiagnosticEvents Instance = new();
    [Event(1, Level = EventLevel.Warning)]
    public void SinkUnavailable(int hresult) => WriteEvent(1, hresult);
}
