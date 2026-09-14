using AgentLink.Broker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentLink.Broker.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsViewModel Model { get; }
    public event Action? StartupRequested;
    public event Action? StartupRefreshRequested;
    public event Action? RuntimeRequested;
    public event Action? OpenDataRequested;
    public event Action<int, string>? CopyMcpRequested;
    public event Action<ElementTheme>? ThemeRequested;
    public event Action<bool>? PluginLoadingRequested;
    public event Action? PluginLoadingRefreshRequested;
    private bool _updatingPluginLoading;
    public SettingsView(SettingsViewModel model) { Model = model; InitializeComponent(); }
    internal void UpdatePluginLoading()
    {
        _updatingPluginLoading = true;
        try
        {
            ModernPluginLoading.IsOn = Model.UseModernPlugins;
            ModernPluginLoading.IsEnabled = Model.CanChangePluginLoading;
            RepairPluginLoading.Visibility = Model.NeedsPluginLoadingRepair ? Visibility.Visible : Visibility.Collapsed;
            RepairPluginLoadingLabel.Text = Model.PluginLoadingRepairText;
        }
        finally { _updatingPluginLoading = false; }
    }
    private void PluginLoadingChanged(object sender, RoutedEventArgs args)
    {
        if (_updatingPluginLoading || !IsLoaded || !Model.CanChangePluginLoading) return;
        var modern = ModernPluginLoading.IsOn;
        if (modern == Model.UseModernPlugins) return;
        UpdatePluginLoading();
        PluginLoadingRequested?.Invoke(modern);
    }
    private void RepairPluginLoadingClicked(object sender, RoutedEventArgs args) =>
        PluginLoadingRequested?.Invoke(Model.UseModernPlugins);
    private void RefreshPluginLoadingClicked(object sender, RoutedEventArgs args) => PluginLoadingRefreshRequested?.Invoke();
    internal void SetThemeSelection(ElementTheme theme) => ThemeChoice.SelectedIndex = (int)theme;
    private void StartupClicked(object sender, RoutedEventArgs args) => StartupRequested?.Invoke();
    private void RefreshStartupClicked(object sender, RoutedEventArgs args) => StartupRefreshRequested?.Invoke();
    private void RuntimeClicked(object sender, RoutedEventArgs args) => RuntimeRequested?.Invoke();
    private void OpenDataClicked(object sender, RoutedEventArgs args) => OpenDataRequested?.Invoke();
    private void CopyMcpClicked(object sender, RoutedEventArgs args) => CopyMcpRequested?.Invoke(McpTarget.SelectedIndex, ConnectionId.Text);
    private void ThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } } && Enum.TryParse<ElementTheme>(tag, out var theme))
            ThemeRequested?.Invoke(theme);
    }
}
