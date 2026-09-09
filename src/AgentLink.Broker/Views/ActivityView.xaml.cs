using AgentLink.Broker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentLink.Broker.Views;

public sealed partial class ActivityView : UserControl
{
    public ActivityViewModel Model { get; }
    public ShellViewModel Shell { get; }
    public event Action? RefreshRequested;
    public event Action? DetailsRequested;
    public event Action? CancelRequested;
    public ActivityView(ActivityViewModel model, ShellViewModel shell) { Model = model; Shell = shell; InitializeComponent(); }
    private void RefreshClicked(object sender, RoutedEventArgs args) => RefreshRequested?.Invoke();
    private void NewestClicked(object sender, RoutedEventArgs args) => Model.Newest();
    private void OlderClicked(object sender, RoutedEventArgs args) => Model.Older();
    private void SelectionChanged(object sender, SelectionChangedEventArgs args) => DetailsRequested?.Invoke();
    private void CancelClicked(object sender, RoutedEventArgs args) => CancelRequested?.Invoke();
}
