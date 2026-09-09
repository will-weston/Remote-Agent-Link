using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentLink.Broker;

internal static class InstallationDiagnostics
{
    public static int Run()
    {
        try
        {
            if (!Environment.Is64BitProcess)
                throw new BadImageFormatException("This package requires an x64 process.");
            var root = AppContext.BaseDirectory;
            CheckExports(Path.Combine(root, "AgentLink.Transport.dll"), ["RAL_Open", "RAL_Read", "RAL_Write", "RAL_Close"]);
            var native = Path.GetFullPath(Path.Combine(root, "..", "Native"));
            CheckExports(Path.Combine(native, "AgentLink.ProxyStub.dll"), ["DllGetClassObject", "DllCanUnloadNow"]);
            RequireFile(Path.Combine(native, "AgentLink.RdpPlugin.exe"));
            var worker = Path.Combine(root, "DesktopWorker");
            foreach (var file in new[]
            {
                "AgentLink.Desktop.Worker.exe", "AgentLink.Desktop.Worker.dll",
                "AgentLink.Desktop.Worker.runtimeconfig.json", "AgentLink.Desktop.Worker.deps.json",
                "AgentLink.Core.dll", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll",
                "UIAutomationClient.dll", "UIAutomationTypes.dll"
            })
                RequireFile(Path.Combine(worker, file));
            using var executable = File.OpenRead(Path.Combine(native, "AgentLink.RdpPlugin.exe"));
            using var pe = new PEReader(executable);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                throw new BadImageFormatException("The RDP plugin is not x64.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ok",
                scope = "payload-only",
                transportExports = true,
                proxyExports = true,
                selfContainedUiaWorker = true,
                rdpConnectionOpened = false,
                identityCreated = false
            }));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("A required packaged component is missing.", path);
    }

    private static void CheckExports(string path, IEnumerable<string> exports)
    {
        RequireFile(path);
        var module = NativeLibrary.Load(path);
        try
        {
            foreach (var export in exports)
                _ = NativeLibrary.GetExport(module, export);
        }
        finally { NativeLibrary.Free(module); }
    }
}
