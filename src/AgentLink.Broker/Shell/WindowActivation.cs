namespace AgentLink.Broker.Shell;

internal static class WindowActivation
{
    internal static void Request(bool controlAvailable, Func<bool> enqueue)
    {
        if (!controlAvailable || !enqueue())
            throw new InvalidOperationException("The broker control window is unavailable.");
    }
}
