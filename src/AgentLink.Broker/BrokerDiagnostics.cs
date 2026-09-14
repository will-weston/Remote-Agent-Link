using System.Diagnostics.Tracing;
using AgentLink.Core;

namespace AgentLink.Broker;

internal enum BrokerObserver
{
    StatusChanged,
    ShowRequested,
    Log,
    TrustChanged,
    ConsentChanged
}

internal static class BrokerDiagnostics
{
    internal static void ObserverFailed(BrokerObserver observer, Exception exception)
    {
        if (Diagnostics.DirectoryPath is not null)
        {
            Diagnostics.Write(DiagnosticEvent.ObserverFailed, DiagnosticLevel.Warning, exception: exception);
            return;
        }
        var source = observer.ToString();
        var failure = FailureType(exception);
        BrokerDiagnosticEvents.Instance.ObserverFailure(source, failure);
        try { Console.Error.WriteLine($"Remote Agent Link observer {source} failed: {failure}."); }
        catch (Exception sinkFailure)
        {
            // ETW remains available to a windowed broker with no usable stderr.
            // Never call the failed presentation/log observer to report itself.
            BrokerDiagnosticEvents.Instance.DiagnosticSinkFailure(FailureType(sinkFailure));
        }
    }

    private static string FailureType(Exception exception) =>
        new(exception.GetType().Name.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_').Take(64).ToArray());
}

[EventSource(Name = "RemoteAgentLink-Broker")]
internal sealed class BrokerDiagnosticEvents : EventSource
{
    internal static readonly BrokerDiagnosticEvents Instance = new();
    private BrokerDiagnosticEvents() { }

    [Event(1, Level = EventLevel.Error, Message = "Observer {0} failed: {1}.")]
    public void ObserverFailure(string observer, string failureType) => WriteEvent(1, observer, failureType);

    [Event(2, Level = EventLevel.Error, Message = "Diagnostic stderr failed: {0}.")]
    public void DiagnosticSinkFailure(string failureType) => WriteEvent(2, failureType);
}
