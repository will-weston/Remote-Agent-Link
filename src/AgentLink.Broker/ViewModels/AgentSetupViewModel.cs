namespace AgentLink.Broker.ViewModels;

public sealed class AgentSetupViewModel : ObservableModel
{
    private string _status = "Checking Copilot setup";
    private string _detail = "";
    private bool _canRegister;
    private bool _busy;
    public string Status { get => _status; internal set => Set(ref _status, value); }
    public string Detail { get => _detail; internal set => Set(ref _detail, value); }
    public bool CanRegister { get => _canRegister; internal set => Set(ref _canRegister, value); }
    public bool Busy { get => _busy; internal set => Set(ref _busy, value); }
    public string Command { get; } = McpSetup.SetupCommand;
    public string ConfigurationPath { get; } = McpSetup.ConfigurationPath;
}
