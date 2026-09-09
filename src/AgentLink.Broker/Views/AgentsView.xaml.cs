using AgentLink.Broker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentLink.Broker.Views;

public sealed partial class AgentsView : UserControl
{
    public AgentSetupViewModel Model { get; }
    public event Action? RegisterRequested;
    public event Action? RefreshRequested;
    public event Action? CopyCommandRequested;
    public event Action? CopyJsonRequested;
    public event Action? SettingsRequested;
    public AgentsView(AgentSetupViewModel model) { Model = model; InitializeComponent(); }
    private void RegisterClicked(object sender, RoutedEventArgs args) => RegisterRequested?.Invoke();
    private void RefreshClicked(object sender, RoutedEventArgs args) => RefreshRequested?.Invoke();
    private void CopyCommandClicked(object sender, RoutedEventArgs args) => CopyCommandRequested?.Invoke();
    private void CopyJsonClicked(object sender, RoutedEventArgs args) => CopyJsonRequested?.Invoke();
    private void SettingsClicked(object sender, RoutedEventArgs args) => SettingsRequested?.Invoke();
}
