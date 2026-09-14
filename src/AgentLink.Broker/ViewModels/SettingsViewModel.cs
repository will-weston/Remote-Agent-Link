using AgentLink.Broker.Shell;
using AgentLink.Copilot;

namespace AgentLink.Broker.ViewModels;

public sealed class SettingsViewModel : ObservableModel
{
    private string _startupStatus = "Checking Windows startup registration…";
    private string _runtimeStatus = "Choose an installed executable. No runtime is downloaded, started or authenticated by setup.";
    private string _pluginLoadingStatus = "Checking RDP loading settings...";
    public string DataDirectory { get; internal set; } = "";
    public string Role { get; internal set; } = "";
    public string RuntimeRequirement => $"Copilot {OfficialSdkRuntimeFactory.RequiredRuntimeVersion} · SDK {OfficialSdkRuntimeFactory.SdkVersion}";
    public string RuntimeStatus { get => _runtimeStatus; internal set => Set(ref _runtimeStatus, value); }
    public string StartupStatus { get => _startupStatus; internal set => Set(ref _startupStatus, value); }
    public string PluginLoadingStatus { get => _pluginLoadingStatus; internal set => Set(ref _pluginLoadingStatus, value); }
    public bool UseModernPlugins { get; private set; } = true;
    public bool PluginLoadingKnown { get; private set; }
    public bool CanChangePluginLoading { get; private set; }
    public bool NeedsPluginLoadingRepair { get; private set; }
    public string PluginLoadingRepairText { get; private set; } = "Restore Modern";

    internal void BeginPluginLoadingChange()
    {
        CanChangePluginLoading = false;
        NeedsPluginLoadingRepair = false;
        PluginLoadingStatus = "Saving RDP loading settings...";
    }

    internal void UpdatePluginLoading(PluginLoadingState? state, bool canManageLegacy, bool busy, bool packaged,
        string? error = null)
    {
        PluginLoadingKnown = state is not null;
        UseModernPlugins = state?.Mode != PluginLoadingMode.Legacy;
        CanChangePluginLoading = packaged && !busy && PluginLoadingKnown && (canManageLegacy || !UseModernPlugins);
        NeedsPluginLoadingRepair = packaged && !busy && (state is null ||
            state.Registration == LegacyRegistration.Incomplete ||
            state.Mode == PluginLoadingMode.Legacy && state.Registration == LegacyRegistration.Missing ||
            state.Mode == PluginLoadingMode.Modern && state.Registration == LegacyRegistration.Owned);
        PluginLoadingRepairText = state?.Mode == PluginLoadingMode.Legacy
            ? "Repair legacy registration" : "Restore Modern and clean up";
        PluginLoadingStatus = error ?? (busy ? "Saving RDP loading settings..." : state switch
        {
            null => "RDP loading settings are unavailable.",
            { Registration: LegacyRegistration.Conflict } =>
                "The legacy registry entry conflicts with another installation or an external change. It has not been overwritten. Modern loading does not use it.",
            { Mode: PluginLoadingMode.Legacy, Registration: LegacyRegistration.Owned } =>
                "Legacy selected. The per-user RDP COM entry is registered. Restart your RDP client to apply; a registered entry is not a connected channel.",
            { Mode: PluginLoadingMode.Legacy } =>
                "Legacy selected, but registration is missing or incomplete. Repair it before restarting your RDP client.",
            { Registration: LegacyRegistration.Owned or LegacyRegistration.Incomplete } =>
                "Modern selected. An inactive legacy entry remains; use the cleanup action before uninstalling.",
            _ => canManageLegacy
                ? "Modern selected (recommended). No legacy registry entry is needed."
                : "Modern selected. Automatic legacy setup requires Windows 11 and the package's scoped registry integration."
        });
    }
}
