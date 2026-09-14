using AgentLink.Broker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentLink.Broker.Views;

public sealed partial class ConnectionsView : UserControl
{
    public ShellViewModel Model { get; }
    public event Action<PeerViewModel>? CopyMcpRequested;
    public event Action? SetupRequested;
    public event Action? AgentsRequested;
    public event Action<bool>? DesktopAccessRequested;
    public ConnectionsView(ShellViewModel model)
    {
        Model = model;
        InitializeComponent();
    }
    private void SetupClicked(object sender, RoutedEventArgs args) => SetupRequested?.Invoke();
    private void AgentsClicked(object sender, RoutedEventArgs args) => AgentsRequested?.Invoke();
    private void DesktopAccessToggled(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleSwitch toggle && toggle.IsOn != Model.DesktopAccessEnabled)
            DesktopAccessRequested?.Invoke(toggle.IsOn);
    }
    internal void SynchronizeDesktopAccess() => DesktopAccessSwitch.IsOn = Model.DesktopAccessEnabled;
    private void CopyMcpClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: PeerViewModel peer }) CopyMcpRequested?.Invoke(peer);
    }
}
