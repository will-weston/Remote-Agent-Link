using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentLink.Broker.Shell;

internal enum UiStartupPhase
{
    AppCreated,
    AppInitializeComponent,
    MainWindowConstruct,
    MainWindowInitializeComponent,
    MainWindowTitle,
    MainWindowApplicationIcon,
    MainWindowTitlebarImage,
    MainWindowExtendTitlebar,
    MainWindowAttachTitlebar,
    MainWindowMica,
    MainWindowHandle,
    MainWindowResize,
    MainWindowEvents,
    MainWindowCaptionTheme,
    ConnectionsViewConstruct,
    ActivityViewConstruct,
    SettingsViewConstruct,
    AgentsViewConstruct,
    MainWindowBindActions,
    MainWindowLoadTheme,
    MainWindowSelectPage,
    MainWindowTray,
    MainWindowSubscribeHost,
    MainWindowRefreshTimer,
    MainWindowConstructed,
    MainWindowActivate,
    MainWindowActivated,
    RootLoaded,
    ApprovalConsumer,
    BrokerStart,
    InitialStatus,
    InitialTrustedPeers,
    InitialStartupSetting,
    RefreshTimerStart,
    Ready,
    StopDesktopAfterFailure,
    StopBrokerAfterFailure
}

internal sealed class UiStartupDiagnostics
{
    internal const int MaxLogBytes = 64 * 1024;
    internal const int MaxExceptionCharacters = 4096;
    private readonly object _gate = new();
    private readonly string _run = Guid.NewGuid().ToString("N");
    private readonly string _logPath;
    private UiStartupPhase _currentPhase;
    private UiStartupPhase? _failurePhase;
    private string? _failureSummary;
    private bool _logAvailable = true;

    internal UiStartupDiagnostics(string dataDirectory)
    {
        _logPath = Path.Combine(dataDirectory, "ui-startup.log");
        Write("run", UiStartupPhase.AppCreated,
            $"pid={Environment.ProcessId} os={Environment.OSVersion.Version} arch={RuntimeInformation.ProcessArchitecture} " +
            $"mvid={typeof(UiStartupDiagnostics).Assembly.ManifestModule.ModuleVersionId:D}");
        RecordPayloadPresence();
    }

    internal UiStartupPhase CurrentPhase { get { lock (_gate) return _currentPhase; } }
    internal UiStartupPhase? FailurePhase { get { lock (_gate) return _failurePhase; } }
    internal string FailureSummary { get { lock (_gate) return _failureSummary ?? "No managed exception recorded."; } }
    internal bool LogAvailable { get { lock (_gate) return _logAvailable; } }

    internal void Checkpoint(UiStartupPhase phase)
    {
        lock (_gate) _currentPhase = phase;
        Write("checkpoint", phase, "");
    }

    internal void Run(UiStartupPhase phase, Action action) => Run<object?>(phase, () => { action(); return null; });

    internal T Run<T>(UiStartupPhase phase, Func<T> action)
    {
        Begin(phase);
        try
        {
            var result = action();
            Write("complete", phase, "");
            return result;
        }
        catch (Exception exception)
        {
            Failure(phase, exception);
            throw;
        }
    }

    internal async Task RunAsync(UiStartupPhase phase, Func<Task> action)
    {
        Begin(phase);
        try
        {
            await action();
            Write("complete", phase, "");
        }
        catch (Exception exception)
        {
            Failure(phase, exception);
            throw;
        }
    }

    private void Begin(UiStartupPhase phase)
    {
        lock (_gate) _currentPhase = phase;
        Write("begin", phase, "");
    }

    internal void Failure(UiStartupPhase phase, Exception exception)
    {
        var summary = ExceptionSummary(exception);
        lock (_gate)
        {
            _failurePhase ??= phase;
            _failureSummary ??= summary;
        }
        Write("failure", phase, DescribeException(exception));
    }

    internal static string ExceptionSummary(Exception exception) =>
        $"{Symbol(exception.GetType().FullName ?? exception.GetType().Name, 160)} HRESULT=0x{unchecked((uint)exception.HResult):X8}";

    internal static string DescribeException(Exception exception)
    {
        var result = new StringBuilder();
        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 4; depth++, current = current.InnerException)
        {
            result.Append("exception[").Append(depth).Append("]=").AppendLine(ExceptionSummary(current));
            foreach (var frame in new StackTrace(current, fNeedFileInfo: false).GetFrames().Take(12))
            {
                var method = frame.GetMethod();
                if (method is null) continue;
                result.Append("at ").Append(Symbol(method.DeclaringType?.FullName ?? "", 160))
                    .Append('.').AppendLine(Symbol(method.Name, 96));
            }
        }
        if (current is not null) result.AppendLine("inner-exceptions=truncated");
        // Never serialize Exception.Message, ToString(), Data, file paths, arguments or local variables.
        var report = result.ToString();
        return report.Length <= MaxExceptionCharacters ? report : report[..MaxExceptionCharacters];
    }

    private static string Symbol(string value, int limit) => new(value.Where(character =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '<' or '>' or '`').Take(limit).ToArray());

    private void RecordPayloadPresence()
    {
        var root = AppContext.BaseDirectory;
        var files = new[]
        {
            ("exe.AppXbf", "App.xbf"),
            ("exe.MainWindowXbf", "MainWindow.xbf"),
            ("exe.ConnectionsXbf", @"Views\ConnectionsView.xbf"),
            ("exe.ActivityXbf", @"Views\ActivityView.xbf"),
            ("exe.SettingsXbf", @"Views\SettingsView.xbf"),
            ("exe.AssemblyPri", "AgentLink.Broker.pri"),
            ("exe.ResourcesPri", "resources.pri"),
            ("exe.Icon", @"Assets\AgentLink.ico"),
            ("exe.TitlebarImage", @"Assets\Logo44.png"),
            ("parent.ResourcesPri", @"..\resources.pri"),
            ("parent.AppXbf", @"..\App.xbf"),
            ("parent.MainWindowXbf", @"..\MainWindow.xbf")
        };
        Write("payloads", UiStartupPhase.AppCreated,
            string.Join(" ", files.Select(file => $"{file.Item1}={File.Exists(Path.Combine(root, file.Item2))}")));
    }

    private void Write(string kind, UiStartupPhase phase, string details)
    {
        var entry = $"{DateTimeOffset.UtcNow:O} run={_run} kind={kind} phase={phase}\n{details}\n";
        UiStartupEvents.Instance.Entry(kind, phase.ToString(), details);
        lock (_gate)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(entry);
                using var stream = new FileStream(_logPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                if (stream.Length + bytes.Length > MaxLogBytes) stream.SetLength(0);
                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception sinkFailure)
            {
                _logAvailable = false;
                UiStartupEvents.Instance.SinkFailure(ExceptionSummary(sinkFailure));
            }
        }
        if (kind != "failure") return;
        try { Console.Error.WriteLine(entry); }
        catch (Exception sinkFailure) { UiStartupEvents.Instance.SinkFailure(ExceptionSummary(sinkFailure)); }
    }
}

[EventSource(Name = "RemoteAgentLink-Broker-UI")]
internal sealed class UiStartupEvents : EventSource
{
    internal static readonly UiStartupEvents Instance = new();
    private UiStartupEvents() { }

    [Event(1, Level = EventLevel.Informational)]
    public void Entry(string kind, string phase, string details) => WriteEvent(1, kind, phase, details);

    [Event(2, Level = EventLevel.Error)]
    public void SinkFailure(string failure) => WriteEvent(2, failure);
}
