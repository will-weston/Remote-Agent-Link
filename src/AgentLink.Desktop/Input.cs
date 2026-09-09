using AgentLink.Core;
using System.Runtime.InteropServices;

namespace AgentLink.Desktop;

internal readonly record struct Key(ushort Code, bool Modifier = false, bool Extended = false);
internal static class KeyMap
{
    internal static Key[] Parse(string[]? names)
    {
        if (names is null || names.Length is < 1 or > 16 || names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 32))
            throw Rules.Invalid("keys must contain 1..16 key names.");
        Key[] keys = names.Select(ParseOne).OrderByDescending(k => k.Modifier).ToArray();
        if (keys.Select(k => k.Code).Distinct().Count() != keys.Length) throw Rules.Invalid("A chord cannot contain duplicate keys or aliases.");
        return keys;
    }
    private static Key ParseOne(string name)
    {
        string key = name.ToLowerInvariant();
        if (key.Length == 1 && key[0] is >= 'a' and <= 'z') return new((ushort)char.ToUpperInvariant(key[0]));
        if (key.Length == 1 && key[0] is >= '0' and <= '9') return new(key[0]);
        if (key.StartsWith('f') && int.TryParse(key.AsSpan(1), out int number) && number is >= 1 and <= 24) return new((ushort)(0x70 + number - 1));
        if (key.Length == 7 && key.StartsWith("numpad", StringComparison.Ordinal) && key[6] is >= '0' and <= '9') return new((ushort)(0x60 + key[6] - '0'));
        return key switch
        {
            "ctrl" or "control" => new(0x11, true), "alt" => new(0x12, true), "shift" => new(0x10, true),
            "win" or "windows" or "meta" => new(0x5B, true, true),
            "enter" or "return" => new(0x0D), "escape" or "esc" => new(0x1B), "tab" => new(0x09),
            "space" or "spacebar" => new(0x20), "backspace" => new(0x08), "delete" or "del" => new(0x2E, Extended: true),
            "insert" => new(0x2D, Extended: true), "home" => new(0x24, Extended: true), "end" => new(0x23, Extended: true),
            "pageup" or "pgup" => new(0x21, Extended: true), "pagedown" or "pgdn" => new(0x22, Extended: true),
            "left" or "arrowleft" => new(0x25, Extended: true), "up" or "arrowup" => new(0x26, Extended: true),
            "right" or "arrowright" => new(0x27, Extended: true), "down" or "arrowdown" => new(0x28, Extended: true),
            "capslock" => new(0x14), "numlock" => new(0x90, Extended: true), "scrolllock" => new(0x91),
            "printscreen" => new(0x2C, Extended: true), "pause" => new(0x13),
            "apps" or "menu" => new(0x5D, Extended: true),
            "semicolon" or ";" => new(0xBA), "equals" or "=" => new(0xBB), "comma" or "," => new(0xBC),
            "minus" or "-" => new(0xBD), "period" or "." => new(0xBE), "slash" or "/" => new(0xBF),
            "backquote" or "`" => new(0xC0), "leftbracket" or "[" => new(0xDB), "backslash" or "\\" => new(0xDC),
            "rightbracket" or "]" => new(0xDD), "quote" or "'" => new(0xDE),
            "multiply" => new(0x6A), "add" => new(0x6B), "subtract" => new(0x6D), "decimal" => new(0x6E), "divide" => new(0x6F, Extended: true),
            _ => throw Rules.Invalid($"Unsupported key name '{name}'. Use letters, digits, F1..F24, or standard named keys.")
        };
    }
    internal static bool IsHeld(Key key, Func<int, bool> held) =>
        held(key.Code) || (key.Code == 0x5B && held(0x5C)) ||
        (key.Code == 0x10 && (held(0xA0) || held(0xA1))) ||
        (key.Code == 0x11 && (held(0xA2) || held(0xA3))) ||
        (key.Code == 0x12 && (held(0xA4) || held(0xA5)));
    internal static bool ShouldPress(Key key, Func<int, bool> held)
    {
        if (!IsHeld(key, held)) return true;
        if (key.Modifier) return false;
        throw new AgentLinkException("input_busy", "A requested non-modifier key is already held by the user.");
    }
}

internal interface IInputSink
{
    bool IsHeld(int key);
    bool Send(Native.Input input);
}
internal sealed class Win32InputSink : IInputSink
{
    public bool IsHeld(int key) => InputLease.Held(key);
    public bool Send(Native.Input input) => Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) == 1;
}
internal sealed class InputLease(Action guard, IInputSink? inputSink = null) : IDisposable
{
    private readonly IInputSink sink = inputSink ?? new Win32InputSink();
    private readonly List<Native.Input> releases = [];
    private bool disposed;
    internal static bool Held(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;
    private static Native.Input Keyboard(ushort key, ushort scan, uint flags) => new()
    {
        Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { VirtualKey = key, Scan = scan, Flags = flags, ExtraInfo = 0x52414C } }
    };
    private static Native.Input Mouse(uint flags, uint data = 0, int x = 0, int y = 0) => new()
    {
        Type = 0, Data = new Native.InputUnion { Mouse = new Native.MouseInput { Flags = flags, MouseData = data, Dx = x, Dy = y, ExtraInfo = 0x52414C } }
    };
    private void Send(Native.Input input)
    {
        guard();
        if (!sink.Send(input))
            throw Native.Failure("SendInput (input may have been blocked)");
    }
    private void Down(Native.Input down, Native.Input up)
    {
        // Record only an input successfully synthesized by this lease.
        Send(down);
        releases.Add(up);
    }
    internal void ReleaseLast()
    {
        if (releases.Count == 0) return;
        Native.Input input = releases[^1];
        // Key/button-up cleanup deliberately does not require a surviving control grant.
        if (!sink.Send(input)) throw Native.Failure("Synthesized input release");
        releases.RemoveAt(releases.Count - 1);
    }
    internal void Move(PixelPoint point, DisplayLayout layout)
    {
        PixelPoint absolute = layout.ToAbsolute(point);
        Send(Mouse(Native.MouseMove | Native.MouseAbsolute | Native.MouseVirtualDesk, 0, absolute.X, absolute.Y));
    }
    internal void ButtonDown(string button)
    {
        (int key, uint down, uint up, uint data) = button switch
        {
            "Left" => (1, 0x0002u, 0x0004u, 0u), "Right" => (2, 0x0008u, 0x0010u, 0u),
            "Middle" => (4, 0x0020u, 0x0040u, 0u), "Backward" => (5, 0x0080u, 0x0100u, 1u),
            "Forward" => (6, 0x0080u, 0x0100u, 2u), _ => throw Rules.Invalid("Invalid mouse button.")
        };
        if (sink.IsHeld(key)) throw new AgentLinkException("input_busy", "The requested mouse button is already held by the user.");
        Down(Mouse(down, data), Mouse(up, data));
    }
    internal void Scroll(int deltaX, int deltaY)
    {
        if (deltaY != 0) Send(Mouse(0x0800, unchecked((uint)(-Math.Clamp(deltaY, -20, 20) * 120))));
        if (deltaX != 0) Send(Mouse(0x1000, unchecked((uint)(Math.Clamp(deltaX, -20, 20) * 120))));
    }
    internal void Press(Key[] keys)
    {
        // Preflight the entire chord before pressing any part of it.
        Key[] owned = keys.Where(key => KeyMap.ShouldPress(key, sink.IsHeld)).ToArray();
        foreach (Key key in owned)
        {
            if (!KeyMap.ShouldPress(key, sink.IsHeld)) continue;
            uint flags = key.Extended ? Native.ExtendedKey : 0;
            Down(Keyboard(key.Code, 0, flags), Keyboard(key.Code, 0, flags | Native.KeyUp));
        }
        while (releases.Count > 0) ReleaseLast();
    }
    internal void Type(string text)
    {
        foreach (char c in text)
        {
            if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(sink.IsHeld))
                throw new AgentLinkException("input_busy", "Typing is paused while a human modifier key is held.");
            Down(Keyboard(0, c, Native.Unicode), Keyboard(0, c, Native.Unicode | Native.KeyUp));
            ReleaseLast();
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        bool failed = false;
        for (int i = releases.Count - 1; i >= 0; i--)
            if (!sink.Send(releases[i])) failed = true;
        releases.Clear();
        if (failed) throw new AgentLinkException("input_release_failed", "Windows rejected release of synthesized input. Use human takeover before further automation.");
    }
}
