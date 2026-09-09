using AgentLink.Core;
using AgentLink.Desktop;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Reflection;

int passed = 0;
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Test(string name, Action body)
{
    try { body(); passed++; }
    catch (Exception exception) { throw new Exception($"FAIL {name}: {exception.Message}", exception); }
}
void Error(string code, Action body)
{
    try { body(); }
    catch (AgentLinkException exception) when (exception.Code == code) { return; }
    throw new Exception($"Expected AgentLinkException({code}).");
}
void Canceled(Action body)
{
    try { body(); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Expected cancellation.");
}
OperationRequest Request(string method, string json = "{}") => new(method, JsonDocument.Parse(json).RootElement.Clone());
DisplayLayout layout = new(-1920, -200, 3840, 1280,
    [new("left", 0, 0, 1920, 1080, 120, 120, false), new("right", 1920, 200, 1920, 1080, 144, 144, true)]);

Test("catalog has 18 real tools", () => Check(ToolCatalog.All.Count == 18 && ToolCatalog.All.Select(t => t.Name).Distinct().Count() == 18));
foreach (ToolDefinition tool in ToolCatalog.All)
{
    Test($"schema and capability {tool.Name}", () =>
    {
        Check(tool.InputSchema.GetProperty("type").GetString() == "object");
        Check(!tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Check(tool.InputSchema.GetProperty("required").ValueKind == JsonValueKind.Array);
        Check(tool.Capability == (tool.Mutating ? "desktop-control" : "observe"));
        Error("invalid_arguments", () => ToolCatalog.Validate(Request(tool.Name, """{"unexpected":true}""")));
    });
}
Test("crop dependent schema", () => Check(ToolCatalog.All.Single(t => t.Name == "take_screenshot").InputSchema.GetProperty("dependentRequired").GetProperty("width").GetArrayLength() == 3));
Test("click paired schema", () => Check(ToolCatalog.All.Single(t => t.Name == "click").InputSchema.GetProperty("dependentRequired").GetProperty("x")[0].GetString() == "y"));
Test("find selector schema", () => Check(ToolCatalog.All.Single(t => t.Name == "find_ui_element").InputSchema.GetProperty("anyOf").GetArrayLength() == 4));
Test("zoom maximum schema", () => Check(ToolCatalog.All.Single(t => t.Name == "zoom_region").InputSchema.GetProperty("properties").GetProperty("width").GetProperty("maximum").GetInt32() == 1920));
Test("unknown tool", () => Error("unknown_tool", () => ToolCatalog.Validate(Request("execute_shell_command"))));
Test("required coordinate", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("move_mouse", """{"x":2}"""))));
Test("strict property casing", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("move_mouse", """{"X":2,"y":2}"""))));
Test("duplicate arguments", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("click", """{"x":1,"x":2,"y":2}"""))));
Test("reject numeric strings", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("move_mouse", """{"x":"2","y":2}"""))));
Test("reject fractional coordinate", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("move_mouse", """{"x":2.5,"y":2}"""))));
Test("reject null optional arguments", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("click", """{"x":null,"y":null}"""))));
Test("reject primitive argument", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("click", "1"))));
Test("reject overflow", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("move_mouse", """{"x":9223372036854775807,"y":2}"""))));
Test("click defaults", () => Check(ToolCatalog.Validate(Request("click")) is ClickArgs { Button: "Left", ClickCount: 1, X: null, Y: null }));
Test("click pair guard", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("click", """{"x":1}"""))));
Test("click count cap", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("click", """{"clickCount":3}"""))));
Test("click Forward", () => Check(ToolCatalog.Validate(Request("click", """{"button":"Forward"}""")) is ClickArgs { Button: "Forward" }));
Test("drag rejects Forward", () => Error("invalid_arguments", () => ToolCatalog.Validate(Request("drag_mouse", """{"startX":1,"startY":1,"endX":2,"endY":2,"button":"Forward"}"""))));
Test("invalid button", () => Error("invalid_arguments", () => Rules.Button("unknown")));
Test("negative native origin mapping", () => Check(layout.ToNative(new(0, 0)) == new PixelPoint(-1920, -200)));
Test("right monitor origin mapping", () => Check(layout.ToNative(new(1920, 200)) == new PixelPoint(0, 0)));
Test("native cursor normalization", () => Check(layout.FromNative(new(-1800, -100)) == new PixelPoint(120, 100)));
Test("negative normalized coordinate", () => Error("invalid_arguments", () => layout.ValidatePoint(new(-1, 0))));
Test("right boundary exclusive", () => Error("invalid_arguments", () => layout.ValidatePoint(new(3840, 300))));
Test("bottom boundary exclusive", () => Error("invalid_arguments", () => layout.ValidatePoint(new(3000, 1280))));
Test("monitor gap rejected", () => Error("invalid_arguments", () => layout.ValidatePoint(new(2000, 50))));
Test("bottom left gap rejected", () => Error("invalid_arguments", () => layout.ValidatePoint(new(500, 1200))));
Test("valid last monitor pixel", () => layout.ValidatePoint(new(3839, 1279)));
Test("absolute mapping retains every pixel", () =>
{
    foreach (int width in new[] { 1, 1920, 3840, 8192 })
    {
        var display = new DisplayLayout(-width, 0, width, 10, [new("m", 0, 0, width, 10, 96, 96, true)]);
        for (int x = 0; x < width; x++)
        {
            PixelPoint absolute = display.ToAbsolute(new(x, 5));
            Check((long)absolute.X * width / 65536 == x, $"Pixel {x}/{width} did not round-trip");
        }
    }
});
Test("layout identity stable under enumeration order", () => Check(layout.LayoutId == (layout with { Monitors = layout.Monitors.Reverse().ToArray() }).LayoutId));
Test("layout detects origin change", () => Check(layout.LayoutId != (layout with { NativeOriginX = -1900 }).LayoutId));
Test("layout detects DPI change", () => Check(layout.LayoutId != (layout with { Monitors = [layout.Monitors[0] with { DpiX = 144 }, layout.Monitors[1]] }).LayoutId));
Test("layout detects primary change", () => Check(layout.LayoutId != (layout with { Monitors = [layout.Monitors[0] with { Primary = true }, layout.Monitors[1]] }).LayoutId));
Test("fresh observation required", () => Error("observation_required", () => Rules.RequireLayout(null, layout)));
Test("stale observation rejected", () => Error("display_layout_changed", () => Rules.RequireLayout("old", layout)));
Test("observed layout accepted", () => Rules.RequireLayout(layout.LayoutId, layout));
Test("idle disconnect invalidates retained observation", () =>
{
    using var link = new CancellationTokenSource();
    var observation = new LayoutObservation(layout.LayoutId, link.Token);
    Check(observation.CurrentId == layout.LayoutId);
    link.Cancel();
    Error("observation_required", () => Rules.RequireLayout(observation.CurrentId, layout));
});
Test("request completion does not end a connection observation", () =>
{
    using var connection = new CancellationTokenSource();
    using var authorization = new CancellationTokenSource();
    var observation = new LayoutObservation(layout.LayoutId, connection.Token, authorization.Token);
    using (var request = CancellationTokenSource.CreateLinkedTokenSource(connection.Token)) { request.Cancel(); }
    Check(observation.CurrentId == layout.LayoutId);
    authorization.Cancel();
    Error("observation_required", () => Rules.RequireLayout(observation.CurrentId, layout));
});
Test("layout detects effective scaling", () => Check(layout.LayoutId != (layout with { Monitors = [layout.Monitors[0] with { ScalePercent = 150 }, layout.Monitors[1]] }).LayoutId));
Test("unaddressable absolute input range rejected", () =>
{
    var wide = new DisplayLayout(0, 0, 65537, 10, [new("wide", 0, 0, 65537, 10, 96, 96, true)]);
    Error("desktop_unavailable", () => wide.ToAbsolute(new(5, 5)));
});
Test("full capture native resolution", () => Check(Rules.Capture(new(), layout, false) == new PixelRect(0, 0, 3840, 1280)));
Test("partial crop rejected", () => Error("invalid_arguments", () => Rules.Capture(new(X: 1), layout, false)));
Test("missing zoom crop rejected", () => Error("invalid_arguments", () => Rules.Capture(new(), layout, true)));
Test("zero capture rejected", () => Error("invalid_arguments", () => Rules.Capture(new(0, 0, 0, 1), layout, false)));
Test("crop bounds", () => Error("invalid_arguments", () => Rules.Capture(new(3839, 0, 2, 1), layout, false)));
Test("crop integer overflow", () => Error("invalid_arguments", () => Rules.Capture(new(10, 0, int.MaxValue, 1), layout, false)));
Test("zoom max accepted", () => Check(Rules.Capture(new(0, 0, 1920, 1080), layout, true).Width == 1920));
Test("zoom width cap", () => Error("invalid_arguments", () => Rules.Capture(new(0, 0, 1921, 1080), layout, true)));
Test("zoom height cap", () => Error("invalid_arguments", () => Rules.Capture(new(0, 0, 1920, 1081), layout, true)));
var huge = new DisplayLayout(0, 0, 32768, 32768, [new("huge", 0, 0, 32768, 32768, 96, 96, true)]);
Test("capture dimension cap", () => Error("capture_too_large", () => Rules.Capture(new(0, 0, 16385, 1), huge, false)));
Test("capture pixel cap", () => Error("capture_too_large", () => Rules.Capture(new(0, 0, 8192, 8192), huge, false)));
Test("explicit small crop on huge desktop", () => Check(Rules.Capture(new(0, 0, 100, 100), huge, false).Width == 100));
Test("PNG limit constant", () => Check(Rules.MaxImageBytes == 16777216));
Test("bounded PNG write exact limit", () => { using var stream = new CappedMemoryStream(4); stream.Write(new byte[4]); Check(stream.Length == 4); });
Test("bounded PNG byte overflow", () => { using var stream = new CappedMemoryStream(4); stream.Write(new byte[4]); Error("capture_too_large", () => stream.WriteByte(0)); Check(stream.LimitExceeded); });
Test("bounded PNG span overflow", () => { using var stream = new CappedMemoryStream(4); Error("capture_too_large", () => stream.Write(new byte[5].AsSpan())); Check(stream.Length == 0); });
Test("bounded PNG SetLength overflow", () => { using var stream = new CappedMemoryStream(4); Error("capture_too_large", () => stream.SetLength(5)); });
Test("bounded PNG seek then write", () => { using var stream = new CappedMemoryStream(4); stream.Position = 5; Error("capture_too_large", () => stream.WriteByte(0)); });
Test("synthetic bitmap encoded as native-size PNG", () =>
{
    using var bitmap = new System.Drawing.Bitmap(4, 3);
    bitmap.SetPixel(0, 0, System.Drawing.Color.Red);
    byte[] png = DesktopImage.EncodePng(bitmap);
    Check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
    Check(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) == 4);
    Check(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) == 3);
});
Test("synthetic PNG encoder preserves explicit cap error", () =>
{
    using var bitmap = new System.Drawing.Bitmap(4, 3);
    Error("capture_too_large", () => DesktopImage.EncodePng(bitmap, 8));
});
Test("paste explicitly unsupported", () => Error("unsupported", () => Rules.Text(new("abc", true))));
Test("empty text accepted", () => Rules.Text(new("")));
Test("Unicode surrogate pairs accepted", () => Rules.Text(new("A😀é")));
Test("unpaired surrogate rejected", () => Error("invalid_arguments", () => Rules.Text(new("\uD800"))));
Test("text cap", () => Error("invalid_arguments", () => Rules.Text(new(new string('a', 32769)))));
Test("keys parsed and modifiers ordered", () => Check(KeyMap.Parse(["c", "ctrl"])[0] == new Key(0x11, true)));
Test("duplicate key aliases rejected", () => Error("invalid_arguments", () => KeyMap.Parse(["ctrl", "control"])));
Test("unsupported key rejected", () => Error("invalid_arguments", () => KeyMap.Parse(["f25"])));
Test("null keys rejected", () => Error("invalid_arguments", () => KeyMap.Parse(null)));
Test("empty chord rejected", () => Error("invalid_arguments", () => KeyMap.Parse([])));
Test("key count cap", () => Error("invalid_arguments", () => KeyMap.Parse(Enumerable.Repeat("a", 17).ToArray())));
Test("F24 valid", () => Check(KeyMap.Parse(["F24"])[0].Code == 0x87));
Test("oversized key names rejected", () => Error("invalid_arguments", () => KeyMap.Parse([new string('a', 33)])));
Test("numpad keys supported", () => Check(KeyMap.Parse(["numpad9"])[0].Code == 0x69));
Test("standard punctuation key supported", () => Check(KeyMap.Parse([";"])[0].Code == 0xBA));
Test("right control honors generic ctrl", () => Check(!KeyMap.ShouldPress(new(0x11, true), k => k == 0xA3)));
Test("right windows honors generic win", () => Check(!KeyMap.ShouldPress(new(0x5B, true), k => k == 0x5C)));
Test("preheld regular key rejected", () => Error("input_busy", () => KeyMap.ShouldPress(new(0x43), k => k == 0x43)));
Test("owned chord release order", () =>
{
    var sink = new FakeInput();
    using (var input = new InputLease(() => { }, sink)) input.Press(KeyMap.Parse(["ctrl", "c"]));
    Check(sink.Events.Count == 4);
    Check(sink.Events[2].Data.Keyboard.VirtualKey == 0x43 && sink.Events[3].Data.Keyboard.VirtualKey == 0x11);
    Check(sink.Events.Skip(2).All(e => (e.Data.Keyboard.Flags & Native.KeyUp) != 0));
});
Test("never release user-preheld modifier", () =>
{
    var sink = new FakeInput { Held = [0xA3] };
    using (var input = new InputLease(() => { }, sink)) input.Press(KeyMap.Parse(["ctrl", "c"]));
    Check(sink.Events.Count == 2 && sink.Events.All(e => e.Data.Keyboard.VirtualKey == 0x43));
});
Test("cancellation releases only synthesized chord prefix", () =>
{
    var sink = new FakeInput();
    int checks = 0;
    Canceled(() => { using var input = new InputLease(() => { if (++checks == 2) throw new OperationCanceledException(); }, sink); input.Press(KeyMap.Parse(["ctrl", "c"])); });
    Check(sink.Events.Count == 2 && sink.Events.All(e => e.Data.Keyboard.VirtualKey == 0x11));
    Check((sink.Events[1].Data.Keyboard.Flags & Native.KeyUp) != 0);
});
Test("cancellation releases synthesized mouse button", () =>
{
    var sink = new FakeInput();
    Canceled(() => { using var input = new InputLease(() => { }, sink); input.ButtonDown("Left"); throw new OperationCanceledException(); });
    Check(sink.Events.Count == 2 && sink.Events[0].Data.Mouse.Flags == 2 && sink.Events[1].Data.Mouse.Flags == 4);
});
Test("preheld button never released", () =>
{
    var sink = new FakeInput { Held = [1] };
    Error("input_busy", () => { using var input = new InputLease(() => { }, sink); input.ButtonDown("Left"); });
    Check(sink.Events.Count == 0);
});
Test("Forward XBUTTON2", () =>
{
    var sink = new FakeInput();
    using (var input = new InputLease(() => { }, sink)) input.ButtonDown("Forward");
    Check(sink.Events.Count == 2 && sink.Events.All(e => e.Data.Mouse.MouseData == 2));
});
Test("scroll clamped notch units and sign", () =>
{
    var sink = new FakeInput();
    using (var input = new InputLease(() => { }, sink)) input.Scroll(int.MaxValue, int.MaxValue);
    Check(unchecked((int)sink.Events[0].Data.Mouse.MouseData) == -2400);
    Check(unchecked((int)sink.Events[1].Data.Mouse.MouseData) == 2400);
});
Test("scroll negative clamp no overflow", () =>
{
    var sink = new FakeInput();
    using (var input = new InputLease(() => { }, sink)) input.Scroll(int.MinValue, int.MinValue);
    Check(unchecked((int)sink.Events[0].Data.Mouse.MouseData) == 2400);
    Check(unchecked((int)sink.Events[1].Data.Mouse.MouseData) == -2400);
});
Test("Unicode input paired per UTF16 unit", () =>
{
    var sink = new FakeInput();
    using (var input = new InputLease(() => { }, sink)) input.Type("😀");
    Check(sink.Events.Count == 4 && sink.Events.All(e => (e.Data.Keyboard.Flags & Native.Unicode) != 0));
    Check(sink.Events[0].Data.Keyboard.Scan == '\uD83D' && sink.Events[2].Data.Keyboard.Scan == '\uDE00');
});
Test("typing never clears human modifiers", () =>
{
    var sink = new FakeInput { Held = [0x11] };
    Error("input_busy", () => { using var input = new InputLease(() => { }, sink); input.Type("x"); });
    Check(sink.Events.Count == 0);
});
Test("guard rejection injects nothing", () =>
{
    var sink = new FakeInput();
    Error("permission_denied", () => { using var input = new InputLease(() => throw new AgentLinkException("permission_denied", "revoked"), sink); input.ButtonDown("Left"); });
    Check(sink.Events.Count == 0);
});
Test("send failure does not release unsynthesized input", () =>
{
    var sink = new FakeInput { Accept = false };
    Error("desktop_operation_failed", () => { using var input = new InputLease(() => { }, sink); input.ButtonDown("Left"); });
    Check(sink.Events.Count == 1);
});
WindowInfo[] windows = [new("Editor - one", "Editor", 1, 0, 0, 100, 100, 1), new("Editor - two", "Editor", 2, 0, 0, 100, 100, 2)];
Test("title substring case insensitive", () => Check(Rules.Match(windows, "ONE").Handle == 1));
Test("ambiguous window rejected", () => Error("ambiguous_window", () => Rules.Match(windows, "Editor")));
Test("no title match rejected", () => Error("window_not_found", () => Rules.Match(windows, "Missing")));
Test("empty title rejected", () => Error("invalid_arguments", () => Rules.Title(" ")));
foreach (string process in new[] { "AgentLink.Broker", "AGENTLINK.BROKER.exe", "AgentLink.Control.exe", "winlogon", "lsass", "csrss" })
    Test($"protected process {process}", () => Check(Rules.ProtectedProcess(process)));
Test("ordinary process not protected by name", () => Check(!Rules.ProtectedProcess("notepad")));
Test("Resize needs dimensions", () => Error("invalid_arguments", () => Rules.Resize(new("one", "Resize"))));
Test("Move needs coordinate pair", () => Error("invalid_arguments", () => Rules.Resize(new("one", "Move", X: 1))));
Test("Move forbids dimensions", () => Error("invalid_arguments", () => Rules.Resize(new("one", "Move", 1, 1, 100, 100))));
Test("Maximize forbids geometry", () => Error("invalid_arguments", () => Rules.Resize(new("one", "Maximize", Width: 100))));
Test("Resize valid", () => Rules.Resize(new("one", "Resize", Width: 100, Height: 100)));
Test("Restore valid", () => Rules.Resize(new("one", "Restore")));
Test("invalid resize action rejected", () => Error("invalid_arguments", () => Rules.Resize(new("one", "Kill"))));
Test("tree lower depth bound", () => Error("invalid_arguments", () => Rules.Tree(new(0, 10))));
Test("tree upper depth bound", () => Error("invalid_arguments", () => Rules.Tree(new(11, 10))));
Test("tree lower element bound", () => Error("invalid_arguments", () => Rules.Tree(new(1, 0))));
Test("tree upper element bound", () => Error("invalid_arguments", () => Rules.Tree(new(1, 2001))));
Test("tree maximum bounds accepted", () => Rules.Tree(new(10, 2000)));
Test("find filter required", () => Error("invalid_arguments", () => Rules.Find(new())));
Test("find rejects blank filter", () => Error("invalid_arguments", () => Rules.Find(new(Text: " "))));
Test("find handle only accepted", () => Rules.Find(new(WindowHandle: 1)));
Test("find invalid handle rejected", () => Error("invalid_arguments", () => Rules.Find(new(WindowHandle: -1))));
UiaNode node = new(0, null, 1, "Save document", "Button", null, null, false, true, false);
Test("UIA name precedence", () => Check(Rules.UiaMatch(node, new(Text: "wrong", Name: "SAVE"))));
Test("UIA text and role", () => Check(Rules.UiaMatch(node, new(Text: "document", Role: "button"))));
Test("UIA mismatched role", () => Check(!Rules.UiaMatch(node, new(Text: "document", Role: "TextBox"))));
Test("UIA password never matches", () => Check(!Rules.UiaMatch(node with { IsPassword = true }, new(Name: "Save"))));
Test("password name getter is never invoked", () => Check(Rules.AccessibleName(true, () => throw new Exception("Read secret")) == "[redacted]"));
Test("nonpassword accessible name returned", () => Check(Rules.AccessibleName(false, () => "Save") == "Save"));
Test("wait lower clamp", () => Check(Rules.WaitDuration(int.MinValue) == 0));
Test("wait upper clamp", () => Check(Rules.WaitDuration(int.MaxValue) == 5000));
Test("wait normal duration", () => Check(Rules.WaitDuration(123) == 123));
Test("wait requires milliseconds", () => Error("invalid_arguments", () => Rules.WaitDuration(null)));
Test("Win32 INPUT native ABI", () => Check(Marshal.SizeOf<Native.Input>() == (IntPtr.Size == 8 ? 40 : 28)));
Test("Win32 WINDOWPLACEMENT native ABI", () => Check(Marshal.SizeOf<Native.Placement>() == 44));
Test("Win32 GUITHREADINFO native ABI", () => Check(Marshal.SizeOf<Native.GuiThreadInfo>() == (IntPtr.Size == 8 ? 72 : 48)));
Test("SDK WTSINFOEXW native ABI", () => Check(Marshal.SizeOf(typeof(Native).GetNestedType("SessionInfoEx", BindingFlags.NonPublic)!) == 232));
Test("WTS session structure uses Unicode entrypoint", () => Check(typeof(Native).GetMethod("WTSQuerySessionInformation", BindingFlags.NonPublic | BindingFlags.Static)!.GetCustomAttribute<DllImportAttribute>()!.CharSet == CharSet.Unicode));
Test("bounded UIA output accepted", () =>
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abcd"));
    Check(UiaWorkerHost.ReadBoundedAsync(stream, 4, CancellationToken.None).GetAwaiter().GetResult().Length == 4);
});
Test("bounded UIA output rejects overflow", () =>
{
    using var stream = new MemoryStream(new byte[10]);
    Error("result_too_large", () => UiaWorkerHost.ReadBoundedAsync(stream, 4, CancellationToken.None).GetAwaiter().GetResult());
});
Test("bounded UIA output canceled", () =>
{
    using var stream = new MemoryStream(new byte[10]);
    Canceled(() => UiaWorkerHost.ReadBoundedAsync(stream, 20, new CancellationToken(true)).GetAwaiter().GetResult());
});
Test("worker executable copied transitively", () => Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "DesktopWorker", "AgentLink.Desktop.Worker.exe"))));
Test("worker runtime config copied transitively", () => Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "DesktopWorker", "AgentLink.Desktop.Worker.runtimeconfig.json"))));
Test("worker dependency copied transitively", () => Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "DesktopWorker", "AgentLink.Core.dll"))));
Test("disconnected guard blocks observations", () => Error("desktop_unavailable", () => Rules.Permission(new FakeConsent { IsConnected = false }, false, CancellationToken.None)));
Test("mutation permission checked immediately", () => Error("permission_denied", () => Rules.Permission(new FakeConsent { DesktopAllowed = false }, true, CancellationToken.None)));
Test("observe does not require control grant", () => Rules.Permission(new FakeConsent { DesktopAllowed = false }, false, CancellationToken.None));
Test("observation revocation checked after approval", () => Error("permission_denied",
    () => Rules.Permission(new FakeConsent { ObservationAllowed = false }, false, CancellationToken.None, true)));
Test("cancellation dominates guard", () => Canceled(() => Rules.Permission(new FakeConsent(), true, new CancellationToken(true))));
foreach (string method in new[] { "get_screen_size", "take_screenshot", "rdp_get_display_layout", "list_windows", "click" })
{
    Test($"disconnected handler never calls native {method}", () =>
    {
        var consent = new FakeConsent { IsConnected = false };
        using var operations = new DesktopOperations(consent);
        Error("desktop_unavailable", () => operations.InvokeAsync(Request(method), CancellationToken.None).GetAwaiter().GetResult());
        Check(consent.Requests.Count == 0);
    });
}
Test("denied observation never calls native", () =>
{
    var consent = new FakeConsent { Grant = false };
    using var operations = new DesktopOperations(consent);
    Error("permission_denied", () => operations.InvokeAsync(Request("get_screen_size"), CancellationToken.None).GetAwaiter().GetResult());
    Check(consent.Requests.Single().Capability == "observe");
});
Test("revoked observation never calls native", () =>
{
    using var operations = new DesktopOperations(new FakeConsent { ObservationAllowed = false });
    Error("permission_denied", () => operations.InvokeAsync(Request("get_screen_size"), CancellationToken.None).GetAwaiter().GetResult());
});
Test("human stop cancels the current desktop epoch before native execution", () =>
{
    var consent = new FakeConsent();
    using var operations = new DesktopOperations(consent);
    consent.OnRequest = operations.InvalidateObservations;
    Canceled(() => operations.InvokeAsync(Request("get_screen_size"), CancellationToken.None, CancellationToken.None).GetAwaiter().GetResult());
    consent.OnRequest = null;
    consent.Grant = false;
    Error("permission_denied", () => operations.InvokeAsync(Request("get_screen_size"), CancellationToken.None, CancellationToken.None).GetAwaiter().GetResult());
});
Test("revocation after approval never calls native", () =>
{
    var consent = new FakeConsent { DesktopAllowed = false };
    using var operations = new DesktopOperations(consent);
    Error("permission_denied", () => operations.InvokeAsync(Request("click"), CancellationToken.None).GetAwaiter().GetResult());
    Check(consent.Requests.Single().Capability == "desktop-control");
});
Test("disconnect during approval never calls native", () =>
{
    var consent = new FakeConsent();
    consent.OnRequest = () => consent.IsConnected = false;
    using var operations = new DesktopOperations(consent);
    Error("desktop_unavailable", () => operations.InvokeAsync(Request("click"), CancellationToken.None).GetAwaiter().GetResult());
});
Test("pre-canceled handler never prompts or calls native", () =>
{
    var consent = new FakeConsent();
    using var operations = new DesktopOperations(consent);
    Canceled(() => operations.InvokeAsync(Request("click"), new CancellationToken(true)).GetAwaiter().GetResult());
    Check(consent.Requests.Count == 0);
});
Test("disposed handler rejects before native", () =>
{
    var operations = new DesktopOperations(new FakeConsent());
    operations.Dispose();
    try { operations.InvokeAsync(Request("click"), CancellationToken.None).GetAwaiter().GetResult(); }
    catch (ObjectDisposedException) { return; }
    throw new Exception("Expected ObjectDisposedException");
});
Console.WriteLine($"PASS {passed} synthetic desktop tests. No live capture, UI Automation, cursor movement, or keyboard/mouse injection performed.");

internal sealed class FakeConsent : IConsentService
{
    public bool IsConnected { get; set; } = true;
    public bool DesktopAllowed { get; set; } = true;
    public bool ObservationAllowed { get; set; } = true;
    internal bool Grant { get; set; } = true;
    internal List<ConsentRequest> Requests { get; } = [];
    internal Action? OnRequest { get; set; }
    public Task<bool> RequestAsync(ConsentRequest request, CancellationToken token) { Requests.Add(request); OnRequest?.Invoke(); return Task.FromResult(Grant); }
    public void RevokeDesktop() => DesktopAllowed = false;
}
internal sealed class FakeInput : IInputSink
{
    internal HashSet<int> Held { get; init; } = [];
    internal List<Native.Input> Events { get; } = [];
    internal bool Accept { get; init; } = true;
    public bool IsHeld(int key) => Held.Contains(key);
    public bool Send(Native.Input input) { Events.Add(input); return Accept; }
}
