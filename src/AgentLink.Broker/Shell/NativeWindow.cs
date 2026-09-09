using System.Runtime.InteropServices;

namespace AgentLink.Broker.Shell;

internal static class NativeWindow
{
    internal static bool IsAppearanceChange(uint message) => message is 0x0015 or 0x001A or 0x031A;
    internal static bool HighContrast
    {
        get
        {
            var settings = new HighContrastSettings { Size = (uint)Marshal.SizeOf<HighContrastSettings>() };
            if (!SystemParametersInfo(0x0042, settings.Size, ref settings, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot read Windows high-contrast settings.");
            return (settings.Flags & 1) != 0;
        }
    }

    internal static void Message(string message, string title) => MessageBox(0, message, title, 0x10);
    internal static bool Confirm(string message, string title) => MessageBox(0, message, title, 0x134) == 6;
    internal static void AllowForeground() => AllowSetForegroundWindow(uint.MaxValue);
    internal static void Show(nint window)
    {
        ShowWindow(window, IsIconic(window) ? 9 : 5);
        SetForegroundWindow(window);
    }
    internal static void Minimize(nint window) => ShowWindow(window, 6);
    internal static void Hide(nint window) => ShowWindow(window, 0);
    internal static bool IsHiddenOrMinimized(nint window) => !IsWindowVisible(window) || IsIconic(window);

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastSettings
    {
        public uint Size, Flags;
        public nint DefaultScheme;
    }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint size, ref HighContrastSettings settings, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
}
