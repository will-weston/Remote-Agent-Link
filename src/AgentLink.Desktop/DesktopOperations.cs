using AgentLink.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentLink.Desktop;

/// <summary>Operations on the broker's own Windows session, never on a local RDP viewer window.</summary>
public sealed class DesktopOperations : IOperationHandler, IDisposable
{
    private static readonly SemaphoreSlim DesktopGate = new(1, 1);
    private readonly IConsentService consent;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object epochGate = new();
    private CancellationTokenSource authorizationEpoch = new();
    private readonly ConcurrentDictionary<string, LayoutObservation> observations = new(StringComparer.Ordinal);
    private int disposed;

    public DesktopOperations(IConsentService consent) => this.consent = consent ?? throw new ArgumentNullException(nameof(consent));
    public IReadOnlyList<ToolDefinition> Tools => ToolCatalog.All;

    public Task<OperationResult> InvokeAsync(OperationRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(request, cancellationToken, cancellationToken);

    public async Task<OperationResult> InvokeAsync(OperationRequest request, CancellationToken cancellationToken, CancellationToken connectionLifetime)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CreateOperationLifetime(cancellationToken, connectionLifetime, out var authorizationToken);
        CancellationToken token = linked.Token;
        string actor = request.ActorId ?? OperationContext.ActorId.Value ?? "bound-link";
        try
        {
            Rules.Permission(consent, false, token);
            object args = ToolCatalog.Validate(request);
            ToolDefinition tool = Tools.First(t => t.Name == request.Method);
            if (!await consent.RequestAsync(new ConsentRequest(tool.Capability, $"Remote desktop operation: {tool.Name}"), token).ConfigureAwait(false))
                throw new AgentLinkException("permission_denied", $"The {tool.Capability} operation was not approved.");
            Rules.Permission(consent, tool.Mutating, token, true);
            await DesktopGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Rules.Permission(consent, tool.Mutating, token, true);
                return await Task.Run(() => Execute(request, args, tool.Mutating, actor, token, connectionLifetime, authorizationToken), token).ConfigureAwait(false);
            }
            finally { DesktopGate.Release(); }
        }
        catch (OperationCanceledException) { observations.Clear(); throw; }
        catch (AgentLinkException exception) when (exception.Code is "desktop_unavailable" or "display_layout_changed" or "input_release_failed")
        {
            observations.Clear();
            if (exception.Code == "input_release_failed") consent.RevokeDesktop();
            throw;
        }
        catch (ExternalException exception)
        {
            throw new AgentLinkException("desktop_operation_failed", $"Windows rejected the desktop operation (HRESULT 0x{exception.HResult:X8}); no fallback or elevation was attempted.");
        }
    }

    private CancellationTokenSource CreateOperationLifetime(CancellationToken request, CancellationToken connection, out CancellationToken authorization)
    {
        lock (epochGate)
        {
            authorization = authorizationEpoch.Token;
            return CancellationTokenSource.CreateLinkedTokenSource(request, connection, lifetime.Token, authorization);
        }
    }

    public void InvalidateObservations()
    {
        lock (epochGate)
        {
            var previous = authorizationEpoch;
            authorizationEpoch = new();
            previous.Cancel();
            previous.Dispose();
            observations.Clear();
        }
    }

    private OperationResult Execute(OperationRequest request, object args, bool mutating, string actor, CancellationToken token, CancellationToken linkToken, CancellationToken authorizationToken)
    {
        using var dpi = new Native.DpiScope();
        Native.EnsureInteractive();
        DisplayLayout layout = Native.Layout();
        if (mutating) Rules.RequireLayout(observations.GetValueOrDefault(actor)?.CurrentId, layout);
        void Guard()
        {
            Rules.Permission(consent, mutating, token, true);
            Native.EnsureInteractive();
            if (Native.Layout().LayoutId != layout.LayoutId)
                throw new AgentLinkException("display_layout_changed", "Display configuration changed during the operation; observe again before retrying.");
            Rules.Permission(consent, mutating, token, true);
        }
        Guard();
        OperationResult result = request.Method switch
        {
            "get_screen_size" => Data(new { layout.Width, layout.Height }),
            "rdp_get_display_layout" => Data(layout),
            "get_cursor_position" => Cursor(layout),
            "take_screenshot" or "zoom_region" => Capture((CaptureArgs)args, layout, request.Method == "zoom_region", Guard),
            "move_mouse" => Move((PointArgs)args, layout, Guard, token),
            "click" => Click((ClickArgs)args, layout, Guard, token),
            "drag_mouse" => Drag((DragArgs)args, layout, Guard, token),
            "scroll" => Scroll((ScrollArgs)args, layout, Guard, token),
            "type_text" => Type((TextArgs)args, Guard),
            "press_keys" => Keys((KeysArgs)args, Guard),
            "list_windows" => Data(Native.Windows(layout)),
            "activate_window" => Activate((TitleArgs)args, layout, Guard, token),
            "resize_window" => Resize((ResizeArgs)args, layout, Guard, token),
            "close_window" => Close((TitleArgs)args, layout, Guard, token),
            "get_accessibility_tree" or "find_ui_element" => Accessibility(request, args, layout, Guard, token),
            "wait_milliseconds" => Wait((WaitArgs)args, Guard, token),
            _ => throw new AgentLinkException("unknown_tool", "Unknown desktop operation.")
        };
        Guard();
        if (!mutating && request.Method != "wait_milliseconds")
        {
            if (observations.Count >= 128 && !observations.ContainsKey(actor)) observations.Clear();
            observations[actor] = new(layout.LayoutId, linkToken, authorizationToken);
        }
        return result;
    }

    private static OperationResult Data<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        if (json.Length > Rules.MaxUiaBytes) throw new AgentLinkException("result_too_large", "Desktop JSON result exceeds 192 KiB.");
        return new(JsonSerializer.Deserialize<JsonElement>(json, Json.Options));
    }
    private static OperationResult Cursor(DisplayLayout layout)
    {
        PixelPoint point = Native.Cursor(layout);
        return Data(new { cursorX = point.X, cursorY = point.Y });
    }
    private static void GuardCapture(PixelRect rect, DisplayLayout layout)
    {
        Exception? error = null;
        bool enumerated = Native.EnumWindows((window, data) =>
        {
            if (!Native.IsWindowVisible(window) || Native.IsIconic(window)) return true;
            try
            {
                if (!Native.GetWindowRect(window, out Native.Rect nativeBounds)) throw Native.Failure("GetWindowRect");
                if (!rect.Intersects(nativeBounds.Normalized(layout))) return true;
                if (Native.GetWindowThreadProcessId(window, out uint process) == 0) throw Native.Failure("GetWindowThreadProcessId");
                (string name, _) = Native.ProcessIdentity(process);
                if (name.Equals("AgentLink.Broker", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("AgentLink.Control", StringComparison.OrdinalIgnoreCase) || process == Environment.ProcessId)
                    throw new AgentLinkException("protected_window", "The requested region intersects Agent Link consent/control UI. Move or hide that UI before continuing.");
                return true;
            }
            catch (Exception exception) { error = exception; return false; }
        }, 0);
        if (error is not null) throw error;
        if (!enumerated) throw Native.Failure("EnumWindows");
    }
    private static OperationResult Capture(CaptureArgs args, DisplayLayout layout, bool zoom, Action guard)
    {
        PixelRect rect = Rules.Capture(args, layout, zoom);
        GuardCapture(rect, layout);
        guard();
        using var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            nint screen = Native.GetDC(0);
            if (screen == 0) throw Native.Failure("GetDC (remote desktop capture)");
            try
            {
                nint destination = graphics.GetHdc();
                try
                {
                    if (!Native.BitBlt(destination, 0, 0, rect.Width, rect.Height, screen,
                        checked(rect.X + layout.NativeOriginX), checked(rect.Y + layout.NativeOriginY), 0x40CC0020))
                        throw Native.Failure("BitBlt (remote desktop capture)");
                }
                finally { graphics.ReleaseHdc(destination); }
            }
            finally { Native.ReleaseDC(0, screen); }
        }
        guard();
        GuardCapture(rect, layout);
        return new(Json.Element(new { rect.X, rect.Y, rect.Width, rect.Height, layout.LayoutId }), DesktopImage.EncodePng(bitmap), "image/png");
    }
    private static void NoHeldButtons()
    {
        if (new[] { 1, 2, 4, 5, 6 }.Any(InputLease.Held))
            throw new AgentLinkException("input_busy", "A mouse button is already held; human input must finish before automation.");
    }
    private static void MoveAndVerify(InputLease input, PixelPoint point, DisplayLayout layout, Action guard, CancellationToken token)
    {
        guard();
        Native.VerifyPoint(point, layout);
        GuardCapture(new(point.X, point.Y, 1, 1), layout);
        input.Move(point, layout);
        var timer = Stopwatch.StartNew();
        while (Native.Cursor(layout) != point && timer.ElapsedMilliseconds < 250)
        {
            Pause(5, token);
            guard();
        }
        if (Native.Cursor(layout) != point) throw new AgentLinkException("input_not_applied", "The physical cursor did not reach the requested pixel; no click was attempted.");
        Native.VerifyPoint(point, layout);
    }
    private static OperationResult Move(PointArgs args, DisplayLayout layout, Action guard, CancellationToken token)
    {
        PixelPoint target = Rules.Point(args.X, args.Y);
        layout.ValidatePoint(target);
        NoHeldButtons();
        using var input = new InputLease(() => { Native.VerifyPoint(target, layout); guard(); });
        MoveAndVerify(input, target, layout, guard, token);
        return Data(new { x = target.X, y = target.Y, moved = true });
    }
    private static OperationResult Click(ClickArgs args, DisplayLayout layout, Action guard, CancellationToken token)
    {
        PixelPoint target = args.X is null ? Native.Cursor(layout) : Rules.Point(args.X, args.Y);
        layout.ValidatePoint(target);
        NoHeldButtons();
        using var input = new InputLease(() =>
        {
            Native.VerifyPoint(target, layout);
            Native.VerifyPoint(Native.Cursor(layout), layout);
            guard();
        });
        MoveAndVerify(input, target, layout, guard, token);
        for (int i = 0; i < args.ClickCount; i++)
        {
            if (Native.Cursor(layout) != target) throw new AgentLinkException("input_interrupted", "The cursor moved before the click.");
            input.ButtonDown(args.Button);
            input.ReleaseLast();
            if (i + 1 < args.ClickCount) Pause(40, token);
        }
        return Data(new { clicked = true, x = target.X, y = target.Y, args.Button, args.ClickCount });
    }
    private static OperationResult Drag(DragArgs args, DisplayLayout layout, Action guard, CancellationToken token)
    {
        PixelPoint start = new(Rules.Required(args.StartX, "startX"), Rules.Required(args.StartY, "startY"));
        PixelPoint end = new(Rules.Required(args.EndX, "endX"), Rules.Required(args.EndY, "endY"));
        layout.ValidatePoint(start);
        layout.ValidatePoint(end);
        const int steps = 32;
        PixelPoint[] path = Enumerable.Range(0, steps + 1).Select(i => new PixelPoint(
            (int)((long)start.X + ((long)end.X - start.X) * i / steps),
            (int)((long)start.Y + ((long)end.Y - start.Y) * i / steps))).ToArray();
        foreach (PixelPoint point in path) layout.ValidatePoint(point);
        NoHeldButtons();
        PixelPoint target = start;
        using var input = new InputLease(() => { Native.VerifyPoint(target, layout); Native.VerifyPoint(Native.Cursor(layout), layout); guard(); });
        MoveAndVerify(input, start, layout, guard, token);
        input.ButtonDown(args.Button);
        foreach (PixelPoint point in path.Skip(1))
        {
            target = point;
            MoveAndVerify(input, point, layout, guard, token);
            Pause(8, token);
        }
        input.ReleaseLast();
        return Data(new { dragged = true, startX = start.X, startY = start.Y, endX = end.X, endY = end.Y, args.Button });
    }
    private static OperationResult Scroll(ScrollArgs args, DisplayLayout layout, Action guard, CancellationToken token)
    {
        PixelPoint point = Rules.Point(args.X, args.Y);
        layout.ValidatePoint(point);
        NoHeldButtons();
        using var input = new InputLease(() =>
        {
            Native.VerifyPoint(point, layout);
            Native.VerifyPoint(Native.Cursor(layout), layout);
            Native.VerifyWindow(Native.GetForegroundWindow());
            guard();
        });
        MoveAndVerify(input, point, layout, guard, token);
        if (Native.Cursor(layout) != point) throw new AgentLinkException("input_interrupted", "The cursor moved before scrolling.");
        input.Scroll(args.DeltaX, args.DeltaY);
        return Data(new { scrolled = true, deltaX = Math.Clamp(args.DeltaX, -20, 20), deltaY = Math.Clamp(args.DeltaY, -20, 20) });
    }
    private static Action KeyboardGuard(Action guard)
    {
        nint target = Native.GetForegroundWindow();
        uint process = Native.VerifyWindow(target);
        return () =>
        {
            if (Native.GetForegroundWindow() != target || Native.VerifyWindow(target) != process)
                throw new AgentLinkException("input_interrupted", "The foreground window changed during keyboard input; observe the new target.");
            Native.VerifyRouting(keyboard: true);
            guard();
        };
    }
    private static OperationResult Type(TextArgs args, Action guard)
    {
        using var input = new InputLease(KeyboardGuard(guard));
        input.Type(args.Text!);
        return Data(new { typed = true, utf16Units = args.Text!.Length });
    }
    private static OperationResult Keys(KeysArgs args, Action guard)
    {
        using var input = new InputLease(KeyboardGuard(guard));
        input.Press(KeyMap.Parse(args.Keys));
        return Data(new { pressed = true, keys = args.Keys });
    }
    private static WindowInfo Match(string title, DisplayLayout layout, bool close = false)
    {
        WindowInfo window = Rules.Match(Native.Windows(layout, includeProtected: true), title);
        if (Native.VerifyWindow((nint)window.Handle, close) != window.ProcessId)
            throw new AgentLinkException("window_changed", "The matched window changed ownership; observe again.");
        return window;
    }
    private static void VerifyTarget(WindowInfo window, Action guard, bool close = false)
    {
        if (Native.VerifyWindow((nint)window.Handle, close) != window.ProcessId)
            throw new AgentLinkException("window_changed", "The target window changed ownership.");
        guard();
    }
    private static OperationResult Activate(TitleArgs args, DisplayLayout layout, Action guard, CancellationToken token)
    {
        WindowInfo window = Match(args.Title!, layout);
        VerifyTarget(window, guard);
        if (!Native.SetForegroundWindow((nint)window.Handle))
            throw new AgentLinkException("focus_denied", "Windows declined the foreground request; no focus workaround was attempted.");
        WaitUntil(() => Native.GetForegroundWindow() == (nint)window.Handle, guard, token);
        if (Native.GetForegroundWindow() != (nint)window.Handle)
            throw new AgentLinkException("focus_denied", "The requested window did not become foreground.");
        return Data(new { matchedTitle = window.Title, window.Handle, activated = true });
    }
    private static OperationResult Resize(ResizeArgs args, DisplayLayout layout, Action guard, CancellationToken token)
    {
        WindowInfo window = Match(args.Title!, layout);
        nint handle = (nint)window.Handle;
        VerifyTarget(window, guard);
        if (args.Action is "Resize" or "Move")
        {
            var rect = new PixelRect(args.X ?? window.X, args.Y ?? window.Y,
                args.Width ?? window.Width, args.Height ?? window.Height);
            layout.ValidateRect(rect);
            if (Native.IsIconic(handle)) throw new AgentLinkException("window_state", "Restore a minimized window before moving or resizing it.");
            PixelPoint native = layout.ToNative(new(rect.X, rect.Y));
            VerifyTarget(window, guard);
            if (!Native.SetWindowPos(handle, 0, native.X, native.Y, rect.Width, rect.Height, 0x0004 | 0x0010 | 0x4000))
                throw Native.Failure("SetWindowPos");
            bool Applied() => Native.GetWindowRect(handle, out Native.Rect bounds) && bounds.Normalized(layout) == rect;
            WaitUntil(Applied, guard, token);
            if (!Applied()) throw new AgentLinkException("window_geometry_not_applied", "The window did not accept the exact requested geometry; re-observe its actual position.");
        }
        else
        {
            int command = args.Action switch { "Maximize" => 3, "Minimize" => 6, _ => 9 };
            VerifyTarget(window, guard);
            if (!Native.ShowWindowAsync(handle, command)) throw Native.Failure("ShowWindowAsync");
            bool Applied()
            {
                var placement = new Native.Placement { Length = (uint)Marshal.SizeOf<Native.Placement>() };
                return Native.GetWindowPlacement(handle, ref placement) && (args.Action switch
                {
                    "Maximize" => placement.ShowCommand == 3,
                    "Minimize" => placement.ShowCommand is 2 or 6 or 7,
                    _ => placement.ShowCommand == 1
                });
            }
            WaitUntil(Applied, guard, token);
            if (!Applied()) throw new AgentLinkException("window_state_not_applied", "The window did not enter the requested state.");
        }
        return Data(new { matchedTitle = window.Title, window.Handle, args.Action, applied = true });
    }
    private static OperationResult Close(TitleArgs args, DisplayLayout layout, Action guard, CancellationToken token)
    {
        WindowInfo window = Match(args.Title!, layout, close: true);
        VerifyTarget(window, guard, close: true);
        if (!Native.PostMessage((nint)window.Handle, 0x0010, 0, 0)) throw Native.Failure("WM_CLOSE");
        WaitUntil(() => !Native.IsWindow((nint)window.Handle), guard, token);
        return Data(new { matchedTitle = window.Title, window.ProcessName, closed = !Native.IsWindow((nint)window.Handle), closeRequested = true });
    }
    private static OperationResult Accessibility(OperationRequest request, object args, DisplayLayout layout, Action guard, CancellationToken token)
    {
        nint handle = args is FindArgs { WindowHandle: { } explicitHandle } ? checked((nint)explicitHandle) : Native.GetForegroundWindow();
        uint process = Native.VerifyWindow(handle);
        guard();
        var query = new UiaRequest(request.Method, request.Arguments, layout, handle, process, Native.SessionId);
        JsonElement result = UiaWorkerHost.RunAsync(query, token).GetAwaiter().GetResult();
        guard();
        if (Native.VerifyWindow(handle) != process) throw new AgentLinkException("window_changed", "The UI Automation target changed ownership.");
        return new(result);
    }
    private static OperationResult Wait(WaitArgs args, Action guard, CancellationToken token)
    {
        int ms = Rules.WaitDuration(args.Ms);
        Pause(ms, token);
        guard();
        return Data(new { waitedMs = ms });
    }
    private static void Pause(int ms, CancellationToken token)
    {
        if (token.WaitHandle.WaitOne(ms)) token.ThrowIfCancellationRequested();
    }
    private static void WaitUntil(Func<bool> condition, Action guard, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 500) { guard(); Pause(10, token); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        observations.Clear();
        // An active invocation may still be registering with this source during cancellation.
    }
}
