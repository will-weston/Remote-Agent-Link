using System.Collections.ObjectModel;
using AgentLink.Core;

namespace AgentLink.Broker.ViewModels;

public sealed class ShellViewModel : ObservableModel
{
    private string _headline = "Getting ready";
    private string _summary = "Starting your broker. Connections will appear automatically.";
    private string _desktopStatus = "Agent access is allowed on this device.";
    private string _notice = "";
    private bool _hasNotice;
    private bool _desktopBlockNotice;
    private bool _isEmpty = true;
    private bool _isBusy;
    private string _activityLog = "";
    private bool _desktopAccessEnabled = true;
    private string _agentCount = "No agents connected";
    private bool _noAgents = true;

    public ObservableCollection<PeerViewModel> Peers { get; } = [];
    public ObservableCollection<AgentConnectionViewModel> Agents { get; } = [];
    public string DeviceName { get; } = Environment.MachineName;
    public string Headline { get => _headline; internal set => Set(ref _headline, value); }
    public string Summary { get => _summary; internal set => Set(ref _summary, value); }
    public string DesktopStatus { get => _desktopStatus; internal set => Set(ref _desktopStatus, value); }
    public string Notice
    {
        get => _notice;
        internal set
        {
            _desktopBlockNotice = false;
            Set(ref _notice, value);
            HasNotice = value.Length != 0;
        }
    }
    public bool HasNotice { get => _hasNotice; internal set => Set(ref _hasNotice, value); }
    public string AgentCount { get => _agentCount; private set => Set(ref _agentCount, value); }
    public bool NoAgents { get => _noAgents; private set => Set(ref _noAgents, value); }
    public bool IsEmpty { get => _isEmpty; internal set => Set(ref _isEmpty, value); }
    public bool IsBusy { get => _isBusy; internal set => Set(ref _isBusy, value); }
    public string ActivityLog { get => _activityLog; internal set => Set(ref _activityLog, value); }
    public bool DesktopAccessEnabled
    {
        get => _desktopAccessEnabled;
        internal set
        {
            Set(ref _desktopAccessEnabled, value);
            if (value && _desktopBlockNotice) Notice = "";
        }
    }

    internal void ShowDesktopBlockNotice()
    {
        Notice = "Agent access is blocked on this device. Broker work is being cancelled; RDP stays connected. Turn access back on from Home.";
        _desktopBlockNotice = true;
    }

    internal void Update(IReadOnlyList<PeerConnectionSnapshot> peers, bool running, string? error, bool desktopLease,
        IReadOnlyList<ConnectionInfo>? connections = null, bool desktopAccessEnabled = true,
        IReadOnlyList<AgentConnectionSnapshot>? agents = null)
    {
        var models = peers.Select(peer => new PeerViewModel(peer, connections)).OrderBy(peer => peer.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (!Peers.Select(p => (p.Id, p.Fingerprint, p.Status, p.Detail, p.DisplayName, p.Direction,
                p.ConnectedSince, p.McpConnectionId, p.CanCopyMcpConfiguration))
            .SequenceEqual(models.Select(p => (p.Id, p.Fingerprint, p.Status, p.Detail, p.DisplayName, p.Direction,
                p.ConnectedSince, p.McpConnectionId, p.CanCopyMcpConfiguration))))
        {
            Peers.Clear();
            foreach (var model in models) Peers.Add(model);
        }
        IsEmpty = models.Length == 0;
        var ready = models.Count(peer => peer.IsReady);
        Headline = !running ? "Broker is not running" :
            models.Any(peer => !peer.IsReady) ? "Connecting computers" : ready != 0 ? "Your computers are connected" : "Ready when you are";
        Summary = !running ? "Open Activity for details. Quit and reopen Remote Agent Link to retry." :
            error is not null ? $"Waiting for the channel to recover. {error}" :
            IsEmpty ? "Connect with Windows App or Remote Desktop. Open Remote Agent Link on both computers." :
            "Computers connect automatically over your signed-in RDP session. Connected agents are listed separately.";
        DesktopAccessEnabled = desktopAccessEnabled;
        DesktopStatus = !desktopAccessEnabled ? "Agent access is blocked on this device. RDP stays connected." :
            desktopLease ? "An agent holds mouse and keyboard control on this device." : "Agent access is allowed on this device.";
        var agentModels = (running ? agents ?? [] : []).Select(agent => new AgentConnectionViewModel(agent))
            .OrderBy(agent => agent.ComputerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(agent => agent.Id, StringComparer.Ordinal).ToArray();
        if (!Agents.SequenceEqual(agentModels))
        {
            Agents.Clear();
            foreach (var agent in agentModels) Agents.Add(agent);
        }
        NoAgents = Agents.Count == 0;
        AgentCount = NoAgents ? "No agents connected" : $"{Agents.Count} {(Agents.Count == 1 ? "agent" : "agents")} connected";
    }
}
