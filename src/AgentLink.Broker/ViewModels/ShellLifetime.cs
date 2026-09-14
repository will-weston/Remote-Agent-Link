namespace AgentLink.Broker.ViewModels;

internal enum CloseAction { Hide, Minimize, Close }

internal sealed class ShellLifetime
{
    public bool TrayAvailable { get; set; }
    public bool Hidden { get; private set; }
    public bool Quitting { get; set; }
    public bool SessionEnding { get; set; }
    public int DialogDepth { get; private set; }
    public bool IsPrompting => DialogDepth != 0;

    public CloseAction RequestClose()
    {
        if (Quitting || SessionEnding) return CloseAction.Close;
        Hidden = TrayAvailable;
        return Hidden ? CloseAction.Hide : CloseAction.Minimize;
    }
    public bool LoseTray()
    {
        TrayAvailable = false;
        var mustShow = Hidden;
        Hidden = false;
        return mustShow;
    }
    public void Show() => Hidden = false;
    public void EnterDialog() => DialogDepth++;
    public void ExitDialog()
    {
        if (DialogDepth == 0) throw new InvalidOperationException("No protected dialog is open.");
        DialogDepth--;
    }
}
