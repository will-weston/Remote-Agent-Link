using AgentLink.Core;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentLink.Desktop;

internal static class Native
{
    internal const uint MouseMove = 0x0001, MouseAbsolute = 0x8000, MouseVirtualDesk = 0x4000;
    internal const uint KeyUp = 0x0002, Unicode = 0x0004, ExtendedKey = 0x0001;
    internal static readonly int SessionId = Process.GetCurrentProcess().SessionId;
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        internal readonly PixelRect Normalized(DisplayLayout layout) => new(checked(Left - layout.NativeOriginX),
            checked(Top - layout.NativeOriginY), checked(Right - Left), checked(Bottom - Top));
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Input
    {
        public uint Type;
        public InputUnion Data;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Placement
    {
        public uint Length, Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectFlags
    {
        public int Inherit, Reserved;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SessionInfoLevel1
    {
        public uint Session;
        public int State, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string Station;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string User;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string Domain;
        public long LogonTime, ConnectTime, DisconnectTime, LastInputTime, CurrentTime;
        public uint IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SessionInfoEx { public uint Level; public SessionInfoLevel1 Data; }
    internal delegate bool EnumWindowCallback(nint window, nint data);
    private delegate bool EnumMonitorCallback(nint monitor, nint dc, ref Rect rect, nint data);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, uint length, out uint needed);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    private static extern bool GetUserObjectFlags(nint handle, int index, out ObjectFlags info, uint length, out uint needed);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetProcessWindowStation();
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSQuerySessionInformation(nint server, int session, int infoClass, out nint buffer, out uint bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, EnumMonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(nint monitor, out int scale);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] internal static extern nint GetShellWindow();
    [DllImport("user32.dll")] internal static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ProcessIdToSessionId(uint process, out uint session);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool EnumWindows(EnumWindowCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    internal static AgentLinkException Failure(string operation) =>
        new("desktop_operation_failed", $"{operation} failed (Win32 error {Marshal.GetLastWin32Error()}). Windows did not establish the cause; no elevation or alternate target was attempted.");
    internal static void EnsureInteractive()
    {
        if (!Environment.UserInteractive || SessionId == 0)
            throw Unavailable("This process is not in an interactive user session.");
        if (!WTSQuerySessionInformation(0, SessionId, 8, out nint state, out uint bytes))
            throw Unavailable("Cannot verify the bound Windows session state.");
        try
        {
            if (bytes < 4 || Marshal.ReadInt32(state) != 0) throw Unavailable("The owning Windows session is not active.");
        }
        finally { WTSFreeMemory(state); }
        if (!WTSQuerySessionInformation(0, SessionId, 25, out nint extended, out bytes))
            throw Unavailable("Cannot verify whether the Windows session is locked.");
        try
        {
            if (bytes < Marshal.SizeOf<SessionInfoEx>()) throw Unavailable("Incomplete Windows session lock information.");
            SessionInfoEx info = Marshal.PtrToStructure<SessionInfoEx>(extended);
            if (info.Level != 1 || info.Data.Session != SessionId || info.Data.State != 0 || info.Data.Flags != 1)
                throw Unavailable("The owning Windows session is locked, disconnected, or cannot be verified as unlocked.");
        }
        finally { WTSFreeMemory(extended); }
        if (!GetUserObjectFlags(GetProcessWindowStation(), 1, out ObjectFlags flags, (uint)Marshal.SizeOf<ObjectFlags>(), out _) || (flags.Flags & 1) == 0)
            throw Unavailable("The process window station is not visible.");
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) throw Unavailable("The input desktop is unavailable, locked, or secure.");
        try
        {
            if (!string.Equals(DesktopName(desktop), "Default", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(DesktopName(GetThreadDesktop(GetCurrentThreadId())), "Default", StringComparison.OrdinalIgnoreCase))
                throw Unavailable("Only the active Default input desktop is supported; lock and secure desktops are not accessible.");
        }
        finally { CloseDesktop(desktop); }
    }
    private static string DesktopName(nint desktop)
    {
        var name = new StringBuilder(256);
        if (desktop == 0 || !GetUserObjectInformation(desktop, 2, name, 512, out _))
            throw Unavailable("Cannot verify the active desktop.");
        return name.ToString();
    }
    internal static AgentLinkException Unavailable(string reason) => new("desktop_unavailable", reason);
    internal sealed class DpiScope : IDisposable
    {
        private readonly nint previous;
        internal DpiScope()
        {
            previous = SetThreadDpiAwarenessContext(-4);
            if (previous == 0) throw Unavailable("Per-monitor-v2 physical coordinate awareness is unavailable.");
        }
        public void Dispose() { SetThreadDpiAwarenessContext(previous); }
    }
    internal static DisplayLayout Layout()
    {
        var monitors = new List<(Rect Bounds, MonitorInfo Info, uint X, uint Y, int Scale)>();
        Exception? error = null;
        if (!EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect rect, nint data) =>
        {
            try
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (!GetMonitorInfo(monitor, ref info)) throw Failure("GetMonitorInfo");
                uint dpi = MonitorDpi(info.Monitor);
                if (GetScaleFactorForMonitor(monitor, out int scale) < 0 || scale is < 100 or > 500)
                    throw Unavailable("Cannot establish the monitor's effective display scaling.");
                if (monitors.Count >= 64) throw Unavailable("More than 64 monitors are unsupported.");
                monitors.Add((info.Monitor, info, dpi, dpi, scale));
                return true;
            }
            catch (Exception exception) { error = exception; return false; }
        }, 0))
            throw error ?? Failure("EnumDisplayMonitors");
        if (monitors.Count == 0) throw Unavailable("No remote-session monitors are available.");
        int left = monitors.Min(m => m.Bounds.Left), top = monitors.Min(m => m.Bounds.Top);
        int width = checked(monitors.Max(m => m.Bounds.Right) - left), height = checked(monitors.Max(m => m.Bounds.Bottom) - top);
        if (width <= 0 || height <= 0 || width > 131072 || height > 131072)
            throw Unavailable("Remote virtual-desktop dimensions are unsupported.");
        return new(left, top, width, height, monitors.Select(m => new DisplayMonitor(m.Info.Device,
            m.Bounds.Left - left, m.Bounds.Top - top, m.Bounds.Right - m.Bounds.Left, m.Bounds.Bottom - m.Bounds.Top,
            m.X, m.Y, (m.Info.Flags & 1) != 0, m.Scale)).ToArray());
    }
    private static uint MonitorDpi(Rect bounds)
    {
        // GetDpiForMonitor depends on the host process awareness. A never-shown PMv2
        // window gives effective monitor DPI without changing the broker's global mode.
        nint window = CreateWindowEx(0x08000080, "STATIC", "", 0x88000000, bounds.Left, bounds.Top, 1, 1, 0, 0, 0, 0);
        if (window == 0) throw Unavailable("Cannot create a noninteractive DPI probe.");
        try
        {
            uint dpi = GetDpiForWindow(window);
            if (dpi == 0) throw Unavailable("Cannot establish physical per-monitor DPI.");
            return dpi;
        }
        finally { if (!DestroyWindow(window)) throw Failure("DestroyWindow (DPI probe)"); }
    }
    internal static PixelPoint Cursor(DisplayLayout layout)
    {
        if (!GetPhysicalCursorPos(out Point point)) throw Failure("GetPhysicalCursorPos");
        var result = layout.FromNative(new(point.X, point.Y));
        layout.ValidatePoint(result);
        return result;
    }
    internal static (string Name, bool Critical) ProcessIdentity(uint process)
    {
        if (!ProcessIdToSessionId(process, out uint session) || session != SessionId)
            throw new AgentLinkException("protected_window", "The window is outside the owning Windows session or its owner cannot be verified.");
        using SafeProcessHandle handle = OpenProcess(0x1000, false, process);
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        if (handle.IsInvalid || !QueryFullProcessImageName(handle, 0, path, ref length) || !IsProcessCritical(handle, out bool critical))
            throw new AgentLinkException("protected_window", "Cannot verify the target process identity and protection status.");
        return (Path.GetFileNameWithoutExtension(path.ToString()), critical);
    }
    internal static uint VerifyWindow(nint window, bool forClose = false)
    {
        if (window == 0 || !IsWindow(window) || !IsWindowVisible(window))
            throw new AgentLinkException("window_not_found", "The target is not a visible window on the owning desktop.");
        bool found = false;
        EnumWindows((candidate, _) => { if (candidate == window) found = true; return true; }, 0);
        if (!found) throw new AgentLinkException("protected_window", "The target does not belong to the current desktop.");
        uint ownerProcess = 0;
        int depth = 0;
        for (nint current = window; current != 0; current = GetWindow(current, 4))
        {
            if (++depth > 32) throw new AgentLinkException("protected_window", "Window ownership chain exceeds the safety limit.");
            if (GetWindowThreadProcessId(current, out uint process) == 0) throw Failure("GetWindowThreadProcessId");
            if (ownerProcess == 0) ownerProcess = process;
            (string name, bool critical) = ProcessIdentity(process);
            if (process == Environment.ProcessId || critical || Rules.ProtectedProcess(name) ||
                current == GetDesktopWindow() || current == GetShellWindow() ||
                (forClose && name.Equals("explorer", StringComparison.OrdinalIgnoreCase)))
                throw new AgentLinkException("protected_window", "System and Agent Link consent/control windows cannot be targeted.");
            if (current == GetWindow(current, 4)) throw new AgentLinkException("protected_window", "Invalid window ownership chain.");
        }
        return ownerProcess;
    }
    internal static nint VerifyPoint(PixelPoint normalized, DisplayLayout layout)
    {
        PixelPoint point = layout.ToNative(normalized);
        nint target = GetAncestor(WindowFromPoint(new Point { X = point.X, Y = point.Y }), 2);
        VerifyWindow(target);
        VerifyRouting(keyboard: false);
        return target;
    }
    internal static void VerifyRouting(bool keyboard)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info)) throw Failure("GetGUIThreadInfo");
        foreach (nint recipient in keyboard ? new[] { info.Focus, info.MenuOwner } : new[] { info.Capture, info.MenuOwner, info.MoveSize })
        {
            if (recipient == 0) continue;
            VerifyWindow(GetAncestor(recipient, 2));
            if (GetWindowThreadProcessId(recipient, out uint process) == 0) throw Failure("GetWindowThreadProcessId");
            (string name, bool critical) = ProcessIdentity(process);
            if (process == Environment.ProcessId || critical || Rules.ProtectedProcess(name))
                throw new AgentLinkException("protected_window", "Input is routed to a protected focus/capture/control window.");
        }
    }
    internal static IReadOnlyList<WindowInfo> Windows(DisplayLayout layout, bool includeProtected = false)
    {
        var windows = new List<WindowInfo>();
        bool overflow = false;
        int visited = 0;
        if (!EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            if (++visited > 2000) { overflow = true; return false; }
            if (GetWindowThreadProcessId(window, out uint process) == 0) return true;
            // Avoid synchronous WM_GETTEXT to a possibly busy UI thread in this process.
            if (process == Environment.ProcessId) return true;
            try
            {
                (string name, bool critical) = ProcessIdentity(process);
                if (!includeProtected && (critical || Rules.ProtectedProcess(name) || process == Environment.ProcessId)) return true;
                if (!GetWindowRect(window, out Rect rect)) return true;
                var title = new StringBuilder(1025);
                GetWindowText(window, title, title.Capacity);
                PixelRect bounds = rect.Normalized(layout);
                windows.Add(new(title.ToString(), name, window, bounds.X, bounds.Y, bounds.Width, bounds.Height, process));
            }
            catch (AgentLinkException) { /* Unverifiable windows are never offered as targets. */ }
            return true;
        }, 0))
        {
            if (overflow) throw new AgentLinkException("result_too_large", "More than 2000 visible windows.");
            throw Failure("EnumWindows");
        }
        return windows;
    }
}
