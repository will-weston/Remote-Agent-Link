using AgentLink.Broker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentLink.Broker.Views;

public sealed partial class ConnectionsView : UserControl
{
    public ShellViewModel Model { get; }
    public event Action<PeerViewModel>? VerifyRequested;
    public event Action<PeerViewModel>? CopyMcpRequested;
    public event Action? SetupRequested;
    public event Action? AgentsRequested;
    public event Action<bool>? DesktopAccessRequested;
    public ConnectionsView(ShellViewModel model)
    {
        Model = model;
        InitializeComponent();
        SizeChanged += (_, _) => ArrangeCards();
        Loaded += (_, _) => ArrangeCards();
    }
    private void ArrangeCards()
    {
        var sideBySide = ActualWidth >= 860;
        SideColumn.Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        FeatureGrid.ColumnSpacing = sideBySide ? 16 : 0;
        Grid.SetColumn(AgentCard, sideBySide ? 1 : 0);
        Grid.SetRow(AgentCard, sideBySide ? 0 : 1);
        var inlineSwitch = ActualWidth >= 760;
        Grid.SetColumn(DesktopAccessSwitch, inlineSwitch ? 1 : 0);
        Grid.SetRow(DesktopAccessSwitch, inlineSwitch ? 0 : 1);
        Grid.SetRowSpan(DesktopAccessSwitch, inlineSwitch ? 4 : 1);
        DesktopAccessSwitch.Margin = inlineSwitch ? new Thickness(24, 0, 0, 0) : new Thickness(0, 16, 0, 0);
    }
    private void VerifyClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: PeerViewModel peer }) VerifyRequested?.Invoke(peer);
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
