using System.Runtime.InteropServices;

namespace AgentLink.Broker.Shell;

internal enum TrayAction { Open = 1, Activity, Stop, Quit }

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 43;
    private const nuint SubclassId = 0x52414C;
    private readonly nint _window;
    private readonly SubclassProc _callback;
    private readonly uint _taskbarCreated;
    private readonly nint _icon;
    private readonly bool _ownsIcon;
    private readonly bool _hotkeyRegistered;
    private readonly bool _sessionRegistered;
    private bool _disposed;
    private string _status = "Remote Agent Link";
    public bool Available { get; private set; }
    public event Action<TrayAction>? Invoked;
    public event Action? Lost;
    public event Action? SessionUnavailable;
    public event Action<bool>? SessionEnding;
    public event Action? AppearanceChanged;
    public event Action<string>? Failed;
    public string? RegistrationWarning { get; }

    public TrayIcon(nint window)
    {
        _window = window;
        _callback = WindowProcedure;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (!SetWindowSubclass(window, _callback, SubclassId, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot register the broker control window.");
        _icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "AgentLink.ico"), 1, 32, 32, 0x10);
        _ownsIcon = _icon != 0;
        if (_icon == 0) _icon = LoadIcon(0, (nint)32512);
        _hotkeyRegistered = RegisterHotKey(window, 1, 0x0001 | 0x0002 | 0x4000, 0x13);
        _sessionRegistered = WTSRegisterSessionNotification(window, 0);
        var warnings = new List<string>();
        if (!_hotkeyRegistered) warnings.Add("Ctrl+Alt+Pause is unavailable. Turn off desktop access on Home or use the tray menu.");
        if (!_sessionRegistered) warnings.Add("Windows session notifications are unavailable. Desktop and heartbeat safety checks remain active.");
        RegistrationWarning = warnings.Count == 0 ? null : string.Join(" ", warnings);
        EnsurePresent();
    }

    public void Update(string status)
    {
        if (_disposed) return;
        _status = status.Length > 120 ? status[..120] : status;
        if (!Available) { EnsurePresent(); return; }
        var data = Data();
        if (!Shell_NotifyIcon(1, ref data)) Recover();
    }

    public bool EnsurePresent()
    {
        if (_disposed || Available) return Available;
        var data = Data();
        Available = Shell_NotifyIcon(0, ref data);
        if (Available)
        {
            data.Version = 4;
            // Version 4 makes keyboard invocation and accessibility activation explicit.
            if (!Shell_NotifyIcon(4, ref data))
            {
                Shell_NotifyIcon(2, ref data);
                Available = false;
            }
        }
        return Available;
    }

    private void Recover()
    {
        Available = false;
        if (!EnsurePresent()) Lost?.Invoke();
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4 | 0x80, Callback = CallbackMessage, Icon = _icon,
        Tip = _status, Info = "", InfoTitle = ""
    };

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try { return DispatchMessage(window, message, wParam, lParam); }
        catch (Exception exception)
        {
            Failed?.Invoke($"A Windows shell action failed: {exception.Message}");
            return message == 0x0011 ? 1 : DefSubclassProc(window, message, wParam, lParam);
        }
    }

    private nint DispatchMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (_disposed) return DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x0024)
        {
            var result = DefSubclassProc(window, message, wParam, lParam);
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var scale = GetDpiForWindow(window) / 96.0;
            limits.MinTrackSize.X = (int)(480 * scale);
            limits.MinTrackSize.Y = (int)(400 * scale);
            Marshal.StructureToPtr(limits, lParam, false);
            return result;
        }
        if (message == _taskbarCreated && _taskbarCreated != 0) Recover();
        else if (message == 0x0312 && wParam == 1) Invoked?.Invoke(TrayAction.Stop);
        else if (message == 0x02B1 && wParam is 2 or 4 or 6 or 7) SessionUnavailable?.Invoke();
        // WinUI desktop windows use HWND notifications, not CoreWindow accessibility events.
        else if (NativeWindow.IsAppearanceChange(message)) AppearanceChanged?.Invoke();
        else if (message == 0x0011)
        {
            SessionEnding?.Invoke(true);
            SessionUnavailable?.Invoke();
            return 1; // Never veto Windows logoff/shutdown to keep an icon alive.
        }
        else if (message == 0x0016) SessionEnding?.Invoke(wParam != 0);
        else if (message == CallbackMessage)
        {
            var notification = (uint)((long)lParam & 0xFFFF);
            if (notification is 0x0400 or 0x0401 or 0x0203)
                Invoked?.Invoke(TrayAction.Open);
            else if (notification == 0x007B)
                ShowMenu();
            return 0;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) { Invoked?.Invoke(TrayAction.Open); return; }
        try
        {
            AppendMenu(menu, 0x0002, 0, _status.Replace("&", "&&", StringComparison.Ordinal));
            AppendMenu(menu, 0x0800, 0, "");
            AppendMenu(menu, 0, (nuint)TrayAction.Open, "&Open Remote Agent Link");
            AppendMenu(menu, 0, (nuint)TrayAction.Activity, "&Activity and tasks");
            AppendMenu(menu, 0, (nuint)TrayAction.Stop, "&Block agent desktop access on this PC\tCtrl+Alt+Pause");
            AppendMenu(menu, 0x0800, 0, "");
            AppendMenu(menu, 0, (nuint)TrayAction.Quit, "&Quit Remote Agent Link");
            SetMenuDefaultItem(menu, (uint)TrayAction.Open, false);
            GetCursorPos(out var position);
            NativeWindow.SetForegroundWindow(_window);
            var action = TrackPopupMenuEx(menu, 0x0100 | 0x0002, position.X, position.Y, _window, 0);
            PostMessage(_window, 0, 0, 0);
            if (action is >= 1 and <= 4) Invoked?.Invoke((TrayAction)action);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data();
        Shell_NotifyIcon(2, ref data);
        Available = false;
        if (_hotkeyRegistered) UnregisterHotKey(_window, 1);
        if (_sessionRegistered) WTSUnRegisterSessionNotification(_window);
        RemoveWindowSubclass(_window, _callback, SubclassId);
        if (_ownsIcon) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint item, string text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(nint menu, uint item, bool byPosition);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
