using System.Collections.ObjectModel;
using AgentLink.Core;

namespace AgentLink.Broker.ViewModels;

public sealed class ShellViewModel : ObservableModel
{
    private string _headline = "Getting ready";
    private string _summary = "Starting your broker. Connections will appear automatically.";
    private string _desktopStatus = "Desktop control not granted";
    private string _notice = "";
    private bool _hasNotice;
    private bool _desktopBlockNotice;
    private bool _hasApproval;
    private string _approvalDetails = "";
    private string _connectionCount = "No devices connected";
    private bool _isEmpty = true;
    private bool _isBusy;
    private string _activityLog = "";
    private bool _desktopAccessEnabled = true;
    private string _mcpSetupStatus = "Set up your agent";

    public ObservableCollection<PeerViewModel> Peers { get; } = [];
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
    public bool HasApproval { get => _hasApproval; internal set => Set(ref _hasApproval, value); }
    public string ApprovalDetails { get => _approvalDetails; internal set => Set(ref _approvalDetails, value); }
    public string ConnectionCount { get => _connectionCount; internal set => Set(ref _connectionCount, value); }
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
    public string McpSetupStatus { get => _mcpSetupStatus; internal set => Set(ref _mcpSetupStatus, value); }

    internal void ShowDesktopBlockNotice()
    {
        Notice = "Agent desktop access is blocked on this PC. Turn it back on from Home to allow new approval requests. RDP and coding tasks keep running.";
        _desktopBlockNotice = true;
    }

    internal void Update(IReadOnlyList<PeerConnectionSnapshot> peers, bool running, string? error, bool desktopLease,
        IReadOnlyList<ConnectionInfo>? connections = null, bool desktopAccessEnabled = true)
    {
        var models = peers.Select(peer => new PeerViewModel(peer, connections)).OrderBy(peer => peer.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (!Peers.Select(p => (p.Id, p.Fingerprint, p.Status, p.Pairing, p.Detail, p.DisplayName, p.Direction,
                p.ConnectedSince, p.McpConnectionId, p.CanCopyMcpConfiguration))
            .SequenceEqual(models.Select(p => (p.Id, p.Fingerprint, p.Status, p.Pairing, p.Detail, p.DisplayName, p.Direction,
                p.ConnectedSince, p.McpConnectionId, p.CanCopyMcpConfiguration))))
        {
            Peers.Clear();
            foreach (var model in models) Peers.Add(model);
        }
        IsEmpty = models.Length == 0;
        var ready = models.Count(peer => peer.IsReady);
        ConnectionCount = $"{models.Length} {(models.Length == 1 ? "device" : "devices")} · {ready} paired";
        Headline = !running ? "Broker is not running" : HasApproval ? "Your approval is needed" :
            models.Any(peer => !peer.IsReady) ? "Verify your connection" : ready != 0 ? "Your devices are connected" : "Ready when you are";
        Summary = !running ? "Open Activity for details. Quit and reopen Remote Agent Link to retry." :
            error is not null ? $"Waiting for the channel to recover. {error}" :
            IsEmpty ? "Connect with Windows App. Keep Remote Agent Link open on both devices; we’ll find the channel automatically." :
            "Connections are automatic. Pairing verifies identity; only you can approve desktop access or agent work.";
        DesktopAccessEnabled = desktopAccessEnabled;
        DesktopStatus = !desktopAccessEnabled ? "Blocked on this PC. Screen viewing, mouse and keyboard access are off." :
            desktopLease ? "Mouse and keyboard control is active on this PC." : "Requests allowed. Each agent still needs your approval.";
    }
}
