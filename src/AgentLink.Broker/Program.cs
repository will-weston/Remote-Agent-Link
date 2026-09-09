using System.Diagnostics;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using AgentLink.Broker;
using AgentLink.Broker.Shell;
using AgentLink.Core;
using AgentLink.Copilot;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--check-installation" }))
            return InstallationDiagnostics.Run();
        var reconcile = args.Length == 2 && args[0] == "--reconcile-session" ? args[1] : null;
        var role = args.Length == 0 || reconcile is not null ? "both" :
            args.Length == 2 && args[0] == "--role" ? args[1] : "";
        if (role is not ("local" or "remote" or "both"))
        {
            NativeWindow.Message("Usage: AgentLink.Broker.exe [--role local|remote|both]\n" +
                "Recovery: AgentLink.Broker.exe --reconcile-session <managed-session-id>", "Remote Agent Link");
            return 2;
        }
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows identity is required.");
        using var mutex = new Mutex(true, $"Local\\RemoteAgentLink.Broker.{sid}", out var created);
        if (!created)
        {
            if (reconcile is not null)
            {
                NativeWindow.Message("Quit the running broker before reconciling a stopped managed session.", "Remote Agent Link");
                return 1;
            }
            try
            {
                NativeWindow.AllowForeground();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                new BrokerClient().CallAsync("ui.show", new { }, timeout.Token).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception exception)
            {
                NativeWindow.Message($"The broker is running but could not show its window. Use its notification-area icon to open it.\n\n{exception.Message}",
                    "Remote Agent Link");
                return 1;
            }
        }
        try
        {
            if (Process.GetCurrentProcess().SessionId == 0)
                throw new InvalidOperationException("The broker must run in an interactive user session, not Session 0.");
            var logon = WindowsLogon.Current;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAgentLink");
            EndpointIdentity.EnsurePrivateDirectory(root);
            var data = Path.Combine(root, "sessions", $"{Process.GetCurrentProcess().SessionId}-{logon}");
            EndpointIdentity.EnsurePrivateDirectory(data);
            if (reconcile is not null)
            {
                if (!Guid.TryParseExact(reconcile, "N", out _))
                    throw new ArgumentException("Use the exact broker-created managed session ID.");
                if (!NativeWindow.Confirm($"Confirm that the prior managed runtime/turn for {reconcile} has stopped.\n\n" +
                        "Do not continue if it might still be executing. This acknowledges recovery safety; it does not change prior outcomes or replay prompts.",
                        "Reconcile stopped managed session"))
                    return 1;
                LocalSetup.ReconcileStoppedManagedSession(data, reconcile);
                NativeWindow.Message("Session reconciled. Restart the broker, reconnect, and explicitly resume the session.", "Remote Agent Link");
                return 0;
            }
            using var identity = new EndpointIdentity(root);
            var consent = new HumanConsent();
            var session = new BrokerSession(identity, consent, new BrokerHost(identity, consent, data, role), data, role);
            try
            {
                RunApplication(session);
            }
            finally
            {
                session.StopAsync().GetAwaiter().GetResult();
            }
            return 0;
        }
        catch (Exception exception)
        {
            // The executable boundary reports startup/shutdown failures instead of
            // leaving a success-shaped background process.
            NativeWindow.Message($"{exception.GetType().Name}: {exception.Message}", "Remote Agent Link failed");
            return 1;
        }
        finally { mutex.ReleaseMutex(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApplication(BrokerSession session)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new App(session);
        });
    }
}
