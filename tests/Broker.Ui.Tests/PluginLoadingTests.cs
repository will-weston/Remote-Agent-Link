using AgentLink.Broker.Shell;
using AgentLink.Broker.ViewModels;
using AgentLink.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Xml.Linq;

internal static class PluginLoadingTests
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string name, uint reserved,
        uint type, byte[] data, uint bytes);

    internal static void Run(Action<bool, string> check)
    {
        void Denied(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is AgentLinkException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                check(true, message);
                return;
            }
            check(false, message);
        }

        var memory = new Store();
        var settings = new PluginLoadingSettings(memory, true);
        settings.Apply(PluginLoadingMode.Legacy);
        check(memory.Events.SequenceEqual(["register", "save:Legacy"]), "Register before enabling legacy activation.");
        check(settings.Read() == new PluginLoadingState(PluginLoadingMode.Legacy, LegacyRegistration.Owned), "Legacy mode and registration agree.");
        memory.Events.Clear();
        settings.Apply(PluginLoadingMode.Modern);
        check(memory.Events.SequenceEqual(["save:Modern", "remove"]), "Disable legacy activation before removing its registration.");
        check(settings.Read() == new PluginLoadingState(PluginLoadingMode.Modern, LegacyRegistration.Missing), "Modern removes its legacy registration.");

        memory = new Store { FailRegister = true };
        Denied(() => new PluginLoadingSettings(memory, true).Apply(PluginLoadingMode.Legacy), "Registration failure is explicit.");
        check(memory.Mode == PluginLoadingMode.Modern && !memory.Events.Any(value => value.StartsWith("save:")), "Failed registration never enables legacy.");
        memory = new Store { FailSave = true };
        Denied(() => new PluginLoadingSettings(memory, true).Apply(PluginLoadingMode.Legacy), "Mode persistence failure is explicit.");
        check(memory.Mode == PluginLoadingMode.Modern && memory.Registration == LegacyRegistration.Owned, "An incomplete enable remains gated by Modern.");
        memory = new Store { Mode = PluginLoadingMode.Legacy, Registration = LegacyRegistration.Owned, FailRemove = true };
        Denied(() => new PluginLoadingSettings(memory, true).Apply(PluginLoadingMode.Modern), "Cleanup failure is not reported as a fully applied change.");
        check(memory.Mode == PluginLoadingMode.Modern && memory.Registration == LegacyRegistration.Owned, "Cleanup failure still disables new legacy activations.");
        memory = new Store { Mode = PluginLoadingMode.Legacy, Registration = LegacyRegistration.Owned, FailSave = true };
        Denied(() => new PluginLoadingSettings(memory, true).Apply(PluginLoadingMode.Modern), "A failed Modern save is explicit.");
        check(memory.Mode == PluginLoadingMode.Legacy && !memory.Events.Contains("remove"), "Failed Modern save preserves the prior usable registration.");
        memory = new Store();
        Denied(() => new PluginLoadingSettings(memory, false).Apply(PluginLoadingMode.Legacy), "No automatic legacy setup without supported OS/package integration.");
        check(memory.Events.Count == 0, "Unsupported legacy setup has no registry writes.");
        new PluginLoadingSettings(memory, false).Apply(PluginLoadingMode.Modern);
        check(memory.Mode == PluginLoadingMode.Modern, "Modern remains available without legacy integration.");
        Denied(() => new PluginLoadingSettings(memory, true).Apply((PluginLoadingMode)2), "Unknown modes cannot be saved.");

        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var path = @"Software\RemoteAgentLink.Tests\PluginLoading-" + Guid.NewGuid().ToString("N");
        var modePath = path + @"\Settings";
        var registrationPath = path + @"\AddIns\Plugin";
        const string family = "RemoteAgentLink_TestFamily";
        var store = new RegistryPluginLoadingStore(family, modePath, registrationPath);
        try
        {
            check(store.ReadMode() == PluginLoadingMode.Modern && store.ReadRegistration() == LegacyRegistration.Missing,
                "Absent per-user registry values default to Modern without registration.");
            using (var absent = root.OpenSubKey(path)) check(absent is null, "Reading defaults creates no registry keys.");
            foreach (var mode in new[] { PluginLoadingMode.Legacy, PluginLoadingMode.Modern })
            {
                store.SaveMode(mode);
                check(new RegistryPluginLoadingStore(family, modePath, registrationPath).ReadMode() == mode, "Mode survives a new store instance.");
                using var key = root.OpenSubKey(modePath);
                check(key?.GetValueKind(RegistryPluginLoadingStore.ModeValue) == RegistryValueKind.DWord, "Native and managed mode contract uses REG_DWORD.");
            }
            foreach (var (value, kind) in new (object, RegistryValueKind)[]
                { ("1", RegistryValueKind.String), (1L, RegistryValueKind.QWord), (new byte[] { 1, 0, 0, 0 }, RegistryValueKind.Binary),
                  (2, RegistryValueKind.DWord), (-1, RegistryValueKind.DWord) })
            {
                using (var key = root.OpenSubKey(modePath, writable: true)) key!.SetValue(RegistryPluginLoadingStore.ModeValue, value, kind);
                Denied(() => store.ReadMode(), "Malformed mode fails closed instead of choosing a loader.");
            }
            foreach (var length in new uint[] { 0, 1, 3, 8 })
            {
                using var key = root.OpenSubKey(modePath, writable: true) ??
                    throw new InvalidOperationException("The test settings key disappeared.");
                check(RegSetValueExW(key.Handle, RegistryPluginLoadingStore.ModeValue, 0,
                    (uint)RegistryValueKind.DWord, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, length) == 0,
                    "Write only the temporary malformed DWORD fixture.");
                Denied(() => store.ReadMode(), "Managed and native readers reject non-four-byte DWORDs identically.");
            }
            settings = new PluginLoadingSettings(store, true);
            settings.Apply(PluginLoadingMode.Modern);
            check(store.ReadMode() == PluginLoadingMode.Modern, "Explicit Modern restores a malformed preference.");
            settings.Apply(PluginLoadingMode.Legacy);
            using (var key = root.OpenSubKey(registrationPath))
            {
                check((string?)key?.GetValue("Name") == RegistryPluginLoadingStore.LegacyClsid, "Legacy registration uses stable COM identity, not a versioned DLL/EXE path.");
                check((string?)key?.GetValue(RegistryPluginLoadingStore.OwnerValue) == family, "Registration records its package-family owner.");
            }
            settings.Apply(PluginLoadingMode.Legacy);
            check(store.ReadRegistration() == LegacyRegistration.Owned, "Repeated registration is idempotent.");
            var foreign = new RegistryPluginLoadingStore("AnotherPackage_Family", modePath, registrationPath);
            check(foreign.ReadRegistration() == LegacyRegistration.Conflict, "Another family cannot claim existing registration.");
            Denied(foreign.RegisterLegacy, "Foreign-owned registration is not overwritten.");
            Denied(foreign.RemoveLegacy, "Foreign-owned registration is not removed.");
            using (var key = root.OpenSubKey(registrationPath, writable: true))
            {
                key!.SetValue("Name", "{11111111-1111-1111-1111-111111111111}");
            }
            check(store.ReadRegistration() == LegacyRegistration.Conflict, "Externally changed Name is a conflict even with our owner marker.");
            Denied(store.RegisterLegacy, "A changed plugin is not overwritten.");
            Denied(store.RemoveLegacy, "A changed plugin is not deleted.");
            using (var key = root.OpenSubKey(registrationPath, writable: true)) key!.DeleteValue("Name");
            check(store.ReadRegistration() == LegacyRegistration.Incomplete, "Interrupted owned registration is distinguishable from a ready plugin.");
            store.RegisterLegacy();
            check(store.ReadRegistration() == LegacyRegistration.Owned, "Explicit repair finishes an interrupted owned registration.");
            using (var key = root.OpenSubKey(registrationPath, writable: true))
            {
                key!.SetValue("Unrelated", "preserve");
                using var child = key.CreateSubKey("UnrelatedChild");
            }
            store.RemoveLegacy();
            using (var key = root.OpenSubKey(registrationPath))
            {
                if (key is null) throw new InvalidOperationException("Cleanup removed an unrelated registry key.");
                check((string?)key.GetValue("Unrelated") == "preserve" && key.SubKeyCount == 1, "Cleanup preserves unrelated values and subkeys.");
                check(key.GetValue("Name") is null && key.GetValue(RegistryPluginLoadingStore.OwnerValue) is null, "Cleanup removes only its owned registration values.");
            }
        }
        finally { root.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }

        var model = new SettingsViewModel();
        model.UpdatePluginLoading(new(PluginLoadingMode.Modern, LegacyRegistration.Missing), true, false, true);
        check(model.UseModernPlugins && model.CanChangePluginLoading && !model.NeedsPluginLoadingRepair, "Modern is the normal recommended UI state.");
        model.UpdatePluginLoading(new(PluginLoadingMode.Legacy, LegacyRegistration.Owned), true, false, true);
        check(!model.UseModernPlugins && model.PluginLoadingStatus.Contains("not a connected channel"), "Registration is never presented as an active RDP connection.");
        model.BeginPluginLoadingChange();
        check(!model.CanChangePluginLoading && !model.NeedsPluginLoadingRepair, "Saving prevents overlapping UI changes.");
        model.UpdatePluginLoading(new(PluginLoadingMode.Legacy, LegacyRegistration.Missing), true, false, true);
        check(model.NeedsPluginLoadingRepair && model.PluginLoadingRepairText.Contains("legacy"), "Missing legacy entry has an explicit repair action.");
        model.UpdatePluginLoading(null, true, false, true, "Malformed setting");
        check(!model.PluginLoadingKnown && !model.CanChangePluginLoading && model.NeedsPluginLoadingRepair, "Unknown mode is disabled with an explicit Modern recovery action.");
        model.UpdatePluginLoading(null, false, false, false, "No package");
        check(!model.CanChangePluginLoading && !model.NeedsPluginLoadingRepair, "Unpackaged UI cannot mutate installed integration.");
        model.UpdatePluginLoading(new(PluginLoadingMode.Modern, LegacyRegistration.Missing), false, false, true);
        check(!model.CanChangePluginLoading && model.PluginLoadingStatus.Contains("Windows 11"), "Downlevel UI does not promise automatic legacy registration.");

        var manifestPath = Path.GetFullPath(@"packaging\AgentLink.Msix\Package.appxmanifest");
        check(PluginLoadingPackage.HasScopedRegistration(manifestPath), "Source package exposes exactly its own legacy leaf.");
        var manifest = XDocument.Load(manifestPath);
        var scratch = Path.Combine(Path.GetTempPath(), "ral-plugin-manifest-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            XNamespace virtualization = "http://schemas.microsoft.com/appx/manifest/virtualization/windows10";
            manifest.Descendants(virtualization + "ExcludedKey").Single().Value =
                @"HKEY_CURRENT_USER\Software\Microsoft\Terminal Server Client\Default\AddIns";
            manifest.Save(scratch);
            check(!PluginLoadingPackage.HasScopedRegistration(scratch), "Broad AddIns exclusion is not accepted as the expected package integration.");
        }
        finally { File.Delete(scratch); }
    }

    private sealed class Store : IPluginLoadingStore
    {
        internal PluginLoadingMode Mode;
        internal LegacyRegistration Registration;
        internal bool FailSave, FailRegister, FailRemove;
        internal List<string> Events { get; } = [];
        public PluginLoadingMode ReadMode() => Mode;
        public LegacyRegistration ReadRegistration() => Registration;
        public void SaveMode(PluginLoadingMode mode)
        {
            Events.Add("save:" + mode);
            if (FailSave) throw new UnauthorizedAccessException("Mode store denied.");
            Mode = mode;
        }
        public void RegisterLegacy()
        {
            Events.Add("register");
            if (FailRegister) throw new UnauthorizedAccessException("Registration denied.");
            Registration = LegacyRegistration.Owned;
        }
        public void RemoveLegacy()
        {
            Events.Add("remove");
            if (FailRemove) throw new UnauthorizedAccessException("Cleanup denied.");
            Registration = LegacyRegistration.Missing;
        }

    }
}
