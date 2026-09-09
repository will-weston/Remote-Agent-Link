using AgentLink.Broker.Shell;
using Microsoft.UI.Xaml;

namespace AgentLink.Broker;

public partial class App : Application
{
    private readonly BrokerSession _session;
    private readonly UiStartupDiagnostics _startup;
    private MainWindow? _window;
    private int _faultReported;

    internal App(BrokerSession session)
    {
        _session = session;
        _startup = new UiStartupDiagnostics(session.DataDirectory);
        UnhandledException += OnUnhandledException;
        _startup.Run(UiStartupPhase.AppInitializeComponent, InitializeComponent);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = _startup.Run(UiStartupPhase.MainWindowConstruct, () => new MainWindow(_session, _startup));
        _startup.Run(UiStartupPhase.MainWindowActivate, _window.Activate);
        _startup.Checkpoint(UiStartupPhase.MainWindowActivated);
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        _startup.Failure(_startup.CurrentPhase, args.Exception);
        if (Interlocked.Exchange(ref _faultReported, 1) != 0) return;
        try { _session.Host.StopDesktopControl(); }
        catch (Exception exception) { _startup.Failure(UiStartupPhase.StopDesktopAfterFailure, exception); }
        _ = StopAfterFailureAsync();
        var diagnostics = _startup.LogAvailable
            ? "Details: ui-startup.log in this session's private broker data directory."
            : "The diagnostic file could not be written. ETW provider: RemoteAgentLink-Broker-UI.";
        NativeWindow.Message(
            $"The control window encountered an error and the broker will stop.\n\n" +
            $"Phase: {_startup.FailurePhase ?? _startup.CurrentPhase}\n{_startup.FailureSummary}\n\n{diagnostics}",
            "Remote Agent Link");
        // Leave the exception unhandled: diagnostics must not mask an unusable control surface.
    }

    private async Task StopAfterFailureAsync()
    {
        try { await _session.StopAsync().ConfigureAwait(false); }
        catch (Exception exception) { _startup.Failure(UiStartupPhase.StopBrokerAfterFailure, exception); }
    }
}
