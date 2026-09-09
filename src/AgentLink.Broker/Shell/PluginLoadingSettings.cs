using System.Security;
using System.Security.Principal;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using AgentLink.Core;

namespace AgentLink.Broker.Shell;

internal enum PluginLoadingMode { Modern = 0, Legacy = 1 }
internal enum LegacyRegistration { Missing, Owned, Incomplete, Conflict }
internal sealed record PluginLoadingState(PluginLoadingMode Mode, LegacyRegistration Registration);

internal interface IPluginLoadingStore
{
    PluginLoadingMode ReadMode();
    void SaveMode(PluginLoadingMode mode);
    LegacyRegistration ReadRegistration();
    void RegisterLegacy();
    void RemoveLegacy();
}

internal sealed class PluginLoadingSettings(IPluginLoadingStore store, bool canManageLegacy)
{
    internal PluginLoadingState Read() => new(store.ReadMode(), store.ReadRegistration());

    internal void Apply(PluginLoadingMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == PluginLoadingMode.Legacy)
        {
            if (!canManageLegacy) throw Unavailable();
            // Registration alone cannot enable the legacy entry point.
            store.RegisterLegacy();
            store.SaveMode(mode);
        }
        else
        {
            // Disable new legacy activations even if registry cleanup subsequently fails.
            store.SaveMode(mode);
            if (store.ReadRegistration() == LegacyRegistration.Missing) return;
            if (!canManageLegacy) throw Unavailable();
            store.RemoveLegacy();
        }
    }

    private static AgentLinkException Unavailable() => new("legacy_registration_unavailable",
        "Automatic legacy registration requires Windows 11 and this package's scoped registry integration. No RDP session or permission was changed.");
}

internal sealed class RegistryPluginLoadingStore : IPluginLoadingStore
{
    internal const string SettingsPath = @"Software\RemoteAgentLink\PluginSettings";
    internal const string ModeValue = "LoadingMode";
    internal const string RegistrationPath = @"Software\Microsoft\Terminal Server Client\Default\AddIns\RemoteAgentLink";
    internal const string LegacyClsid = "{77A190C4-C7EB-4DB2-A317-9CFADA8F2A53}";
    internal const string OwnerValue = "OwnerPackageFamilyName";
    private readonly string _family;
    private readonly string _settingsPath;
    private readonly string _registrationPath;

    internal RegistryPluginLoadingStore(string family, string settingsPath = SettingsPath,
        string registrationPath = RegistrationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        _family = family;
        _settingsPath = settingsPath;
        _registrationPath = registrationPath;
    }

    private static RegistryKey OpenRoot() => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

    public PluginLoadingMode ReadMode()
    {
        using var root = OpenRoot();
        using var key = root.OpenSubKey(_settingsPath);
        if (key is null) return PluginLoadingMode.Modern;
        uint bytes = sizeof(uint);
        var result = RegQueryValueExW(key.Handle, ModeValue, 0, out var type, out var value, ref bytes);
        if (result == 2) return PluginLoadingMode.Modern;
        if (result == 234) throw InvalidMode();
        if (result != 0)
            throw new IOException("The saved RDP loading mode could not be read.", new Win32Exception(result));
        if (type != (uint)RegistryValueKind.DWord || bytes != sizeof(uint) || value > 1) throw InvalidMode();
        return (PluginLoadingMode)value;
    }

    private static AgentLinkException InvalidMode() => new("plugin_loading_settings_invalid",
        "The saved RDP loading mode is invalid. New plugin activations are blocked. Restore Modern below to repair the setting.");

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string name, nint reserved,
        out uint type, out uint data, ref uint bytes);

    public void SaveMode(PluginLoadingMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        using var root = OpenRoot();
        using var key = root.CreateSubKey(_settingsPath, writable: true);
        key.SetValue(ModeValue, (int)mode, RegistryValueKind.DWord);
        key.Flush();
    }

    public LegacyRegistration ReadRegistration()
    {
        using var root = OpenRoot();
        using var key = root.OpenSubKey(_registrationPath);
        return Classify(key);
    }

    private LegacyRegistration Classify(RegistryKey? key)
    {
        if (key is null || key.ValueCount == 0 && key.SubKeyCount == 0) return LegacyRegistration.Missing;
        if (key.GetValue(OwnerValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string owner ||
            key.GetValueKind(OwnerValue) != RegistryValueKind.String || !string.Equals(owner, _family, StringComparison.OrdinalIgnoreCase))
            return LegacyRegistration.Conflict;
        var name = key.GetValue("Name", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (name is null) return LegacyRegistration.Incomplete;
        return key.GetValueKind("Name") == RegistryValueKind.String && name is string text &&
            string.Equals(text, LegacyClsid, StringComparison.OrdinalIgnoreCase)
            ? LegacyRegistration.Owned : LegacyRegistration.Conflict;
    }

    public void RegisterLegacy()
    {
        using var root = OpenRoot();
        using var key = root.CreateSubKey(_registrationPath, writable: true);
        if (Classify(key) == LegacyRegistration.Conflict) throw Conflict();
        key.SetValue(OwnerValue, _family, RegistryValueKind.String);
        key.SetValue("Name", LegacyClsid, RegistryValueKind.String);
        key.Flush();
    }

    public void RemoveLegacy()
    {
        using var root = OpenRoot();
        using (var key = root.OpenSubKey(_registrationPath, writable: true))
        {
            if (key is null) return;
            if (Classify(key) == LegacyRegistration.Conflict) throw Conflict();
            key.DeleteValue("Name", throwOnMissingValue: false);
            key.DeleteValue(OwnerValue, throwOnMissingValue: false);
            key.Flush();
            if (key.ValueCount != 0 || key.SubKeyCount != 0) return;
        }
        root.DeleteSubKey(_registrationPath, throwOnMissingSubKey: false);
    }

    private static AgentLinkException Conflict() => new("legacy_registration_conflict",
        "The Remote Agent Link legacy entry belongs to another package or has been changed outside this app. It was not overwritten or removed. Modern loading does not use that entry.");
}

internal static class PluginLoadingOperation
{
    internal static void Run(Action action)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ??
            throw new SecurityException("The current Windows user could not be identified.");
        using var mutex = new Mutex(false, @"Global\RemoteAgentLink.PluginLoading." + sid);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new AgentLinkException("plugin_loading_busy", "Another app window is changing RDP loading. Wait a moment and try again.");
            // Each operation rereads ownership; no cached registry state survives an abandoned writer.
            action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }
}

internal static class PluginLoadingPackage
{
    internal static bool HasScopedRegistration(string manifestPath)
    {
        using var reader = XmlReader.Create(manifestPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 128 * 1024
        });
        var manifest = XDocument.Load(reader);
        XNamespace appx = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace virtualization = "http://schemas.microsoft.com/appx/manifest/virtualization/windows10";
        XNamespace rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
        var excluded = manifest.Root?.Element(appx + "Properties")?
            .Element(virtualization + "RegistryWriteVirtualization")?
            .Element(virtualization + "ExcludedKeys")?.Elements(virtualization + "ExcludedKey").ToArray() ?? [];
        return excluded.Length == 1 &&
            string.Equals(excluded[0].Value, @"HKEY_CURRENT_USER\" + RegistryPluginLoadingStore.RegistrationPath, StringComparison.OrdinalIgnoreCase) &&
            manifest.Root?.Element(appx + "Capabilities")?.Elements(rescap + "Capability")
                .Any(capability => (string?)capability.Attribute("Name") == "unvirtualizedResources") == true;
    }
}
