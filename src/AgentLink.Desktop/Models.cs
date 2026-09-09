using AgentLink.Core;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentLink.Desktop;

internal sealed record EmptyArgs;
internal sealed record PointArgs(int? X = null, int? Y = null);
internal sealed record CaptureArgs(int? X = null, int? Y = null, int? Width = null, int? Height = null);
internal sealed record ClickArgs(int? X = null, int? Y = null, string Button = "Left", int ClickCount = 1);
internal sealed record DragArgs(int? StartX = null, int? StartY = null, int? EndX = null, int? EndY = null, string Button = "Left");
internal sealed record ScrollArgs(int? X = null, int? Y = null, int DeltaX = 0, int DeltaY = 0);
internal sealed record TextArgs(string? Text = null, bool UsePaste = false);
internal sealed record KeysArgs(string[]? Keys = null);
internal sealed record TitleArgs(string? Title = null);
internal sealed record ResizeArgs(string? Title = null, string? Action = null, int? X = null, int? Y = null, int? Width = null, int? Height = null);
internal sealed record TreeArgs(int MaxDepth = 5, int MaxElements = 500);
internal sealed record FindArgs(string? Text = null, string? Role = null, string? Name = null, long? WindowHandle = null);
internal sealed record WaitArgs(int? Ms = null);
internal readonly record struct PixelPoint(int X, int Y);
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    internal long Right => (long)X + Width;
    internal long Bottom => (long)Y + Height;
    internal bool Contains(PixelPoint point) => point.X >= X && point.Y >= Y && point.X < Right && point.Y < Bottom;
    internal bool Intersects(PixelRect other) => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}
internal sealed record DisplayMonitor(string DeviceName, int X, int Y, int Width, int Height, uint DpiX, uint DpiY, bool Primary, int ScalePercent = 100);
internal sealed record DisplayLayout(int NativeOriginX, int NativeOriginY, int Width, int Height, DisplayMonitor[] Monitors)
{
    public string CoordinateSpace => "remote-virtual-desktop-physical-pixels";
    public string LayoutId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{NativeOriginX},{NativeOriginY},{Width},{Height}|" +
        string.Join("|", Monitors.OrderBy(m => m.DeviceName, StringComparer.Ordinal).Select(m =>
            $"{m.DeviceName},{m.X},{m.Y},{m.Width},{m.Height},{m.DpiX},{m.DpiY},{m.Primary},{m.ScalePercent}")))));
    internal PixelRect Bounds => new(0, 0, Width, Height);
    internal PixelPoint ToNative(PixelPoint point)
    {
        ValidatePoint(point);
        return new(checked(point.X + NativeOriginX), checked(point.Y + NativeOriginY));
    }
    internal PixelPoint FromNative(PixelPoint point) => new(checked(point.X - NativeOriginX), checked(point.Y - NativeOriginY));
    internal void ValidatePoint(PixelPoint point)
    {
        if (!Bounds.Contains(point) || !Monitors.Any(m => new PixelRect(m.X, m.Y, m.Width, m.Height).Contains(point)))
            throw Rules.Invalid("Coordinates must refer to a pixel on a remote monitor, not a virtual-desktop gap.");
    }
    internal void ValidateRect(PixelRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 || rect.Right > Width || rect.Bottom > Height)
            throw Rules.Invalid("The rectangle must fit inside the remote virtual desktop.");
    }
    internal PixelPoint ToAbsolute(PixelPoint point)
    {
        ValidatePoint(point);
        if (Width > 65536 || Height > 65536)
            throw new AgentLinkException("desktop_unavailable", "The virtual desktop exceeds the absolute-input coordinate range.");
        return new((int)Math.Min(65535, ((long)point.X * 65536 + 32768) / Width),
            (int)Math.Min(65535, ((long)point.Y * 65536 + 32768) / Height));
    }
}
internal sealed record LayoutObservation(string LayoutId, CancellationToken LinkToken, CancellationToken AuthorizationToken = default)
{
    internal string? CurrentId => LinkToken.IsCancellationRequested || AuthorizationToken.IsCancellationRequested ? null : LayoutId;
}
internal sealed record WindowInfo(string Title, string ProcessName, long Handle, int X, int Y, int Width, int Height, uint ProcessId);
internal sealed record UiaRequest(string Method, JsonElement Arguments, DisplayLayout Layout, long WindowHandle, uint ProcessId, int SessionId);
internal sealed record UiaNode(int Index, int? ParentIndex, int Depth, string Name, string Role,
    PixelRect? Bounds, PixelPoint? ClickablePoint, bool IsPassword, bool IsEnabled, bool IsOffscreen);
internal sealed record UiaResult(IReadOnlyList<UiaNode> Elements, bool Truncated, string? Limit, string LayoutId);
internal sealed record WorkerResponse(JsonElement? Data = null, string? Code = null, string? Error = null);

internal static class Rules
{
    internal const int MaxImageBytes = 16 * 1024 * 1024;
    internal const int MaxImageDimension = 16384;
    internal const long MaxImagePixels = 32 * 1024 * 1024;
    internal const int MaxUiaBytes = 192 * 1024;
    internal static AgentLinkException Invalid(string message) => new("invalid_arguments", message);
    internal static int Required(int? value, string name) => value ?? throw Invalid($"{name} is required.");
    internal static PixelPoint Point(int? x, int? y) => new(Required(x, "x"), Required(y, "y"));
    internal static void Pair(int? x, int? y)
    {
        if (x.HasValue != y.HasValue) throw Invalid("x and y must be supplied together.");
    }
    internal static PixelRect Capture(CaptureArgs args, DisplayLayout layout, bool zoom)
    {
        int count = new[] { args.X, args.Y, args.Width, args.Height }.Count(v => v.HasValue);
        if (count != 4 && (count != 0 || zoom)) throw Invalid("Supply all four crop parameters together.");
        PixelRect rect = count == 0 ? layout.Bounds : new(args.X!.Value, args.Y!.Value, args.Width!.Value, args.Height!.Value);
        layout.ValidateRect(rect);
        if (zoom && (rect.Width > 1920 || rect.Height > 1080)) throw Invalid("zoom_region is limited to 1920x1080.");
        if (rect.Width > MaxImageDimension || rect.Height > MaxImageDimension || (long)rect.Width * rect.Height > MaxImagePixels)
            throw new AgentLinkException("capture_too_large", $"Capture exceeds {MaxImageDimension} pixels per axis or {MaxImagePixels} total pixels. Request an explicit crop.");
        return rect;
    }
    internal static string Button(string? value, bool drag = false)
    {
        string[] allowed = drag ? ["Left", "Right", "Middle"] : ["Left", "Right", "Middle", "Forward", "Backward"];
        return allowed.FirstOrDefault(v => string.Equals(v, value, StringComparison.Ordinal))
            ?? throw Invalid("Invalid mouse button.");
    }
    internal static void Text(TextArgs args)
    {
        if (args.Text is null || args.Text.Length > 32768) throw Invalid("text is required and limited to 32768 UTF-16 units.");
        if (args.UsePaste) throw new AgentLinkException("unsupported", "usePaste=true is unavailable: clipboard access is not implemented or authorized.");
        for (int i = 0; i < args.Text.Length; i++)
        {
            char c = args.Text[i];
            if (char.IsHighSurrogate(c))
            {
                if (++i == args.Text.Length || !char.IsLowSurrogate(args.Text[i])) throw Invalid("text contains an unpaired surrogate.");
            }
            else if (char.IsLowSurrogate(c)) throw Invalid("text contains an unpaired surrogate.");
        }
    }
    internal static string Title(string? value) => Arguments.Required(value, "title", 1024);
    internal static WindowInfo Match(IEnumerable<WindowInfo> windows, string title)
    {
        WindowInfo[] matches = windows.Where(w => w.Title.Contains(Title(title), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length switch
        {
            0 => throw new AgentLinkException("window_not_found", "No visible session window matches that title."),
            1 => matches[0],
            _ => throw new AgentLinkException("ambiguous_window", "Multiple visible windows match; use a more specific title.")
        };
    }
    internal static void Resize(ResizeArgs args)
    {
        Title(args.Title);
        Pair(args.X, args.Y);
        if (args.Action == "Resize")
        {
            if (Required(args.Width, "width") <= 0 || Required(args.Height, "height") <= 0)
                throw Invalid("width and height must be positive.");
        }
        else if (args.Action == "Move")
        {
            Point(args.X, args.Y);
            if (args.Width.HasValue || args.Height.HasValue) throw Invalid("Move does not accept width or height.");
        }
        else if (args.Action is "Maximize" or "Minimize" or "Restore")
        {
            if (args.X.HasValue || args.Width.HasValue || args.Height.HasValue) throw Invalid("This action does not accept geometry.");
        }
        else throw Invalid("action must be Resize, Move, Maximize, Minimize, or Restore.");
    }
    internal static void Tree(TreeArgs args)
    {
        if (args.MaxDepth is < 1 or > 10 || args.MaxElements is < 1 or > 2000)
            throw Invalid("maxDepth must be 1..10 and maxElements must be 1..2000.");
    }
    internal static void Find(FindArgs args)
    {
        if (new[] { args.Text, args.Role, args.Name }.All(string.IsNullOrWhiteSpace) && args.WindowHandle is null)
            throw Invalid("Supply at least one of text, role, name, or windowHandle.");
        foreach (string? value in new[] { args.Text, args.Role, args.Name })
            if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 1024)) throw Invalid("Filters must be nonblank and at most 1024 characters.");
        if (args.WindowHandle is <= 0) throw Invalid("windowHandle must be a positive native window handle.");
    }
    internal static bool UiaMatch(UiaNode node, FindArgs args) =>
        !node.IsPassword &&
        ((args.Name ?? args.Text) is not { } name || node.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
        (args.Role is null || node.Role.Contains(args.Role, StringComparison.OrdinalIgnoreCase));
    internal static string AccessibleName(bool password, Func<string> readName) => password ? "[redacted]" : readName();
    internal static int WaitDuration(int? ms) => Math.Clamp(Required(ms, "ms"), 0, 5000);
    internal static bool ProtectedProcess(string name) =>
        new[] { "AgentLink.Broker", "AgentLink.Control", "AgentLink.Desktop.Worker", "csrss", "winlogon",
            "wininit", "services", "lsass", "smss", "dwm", "fontdrvhost", "sihost", "LogonUI", "System", "Idle",
            "consent", "CredentialUIBroker", "LockApp", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchUI" }
        .Contains(Path.GetFileName(name).EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(name)[..^4] : Path.GetFileName(name), StringComparer.OrdinalIgnoreCase);
    internal static void Permission(IConsentService consent, bool mutation, CancellationToken token, bool requireObservation = false)
    {
        token.ThrowIfCancellationRequested();
        if (!consent.IsConnected) throw new AgentLinkException("desktop_unavailable", "The bound remote link is disconnected.");
        if (mutation && !consent.DesktopAllowed) throw new AgentLinkException("permission_denied", "Desktop control was revoked or approval UI is active.");
        if (!mutation && requireObservation && !consent.ObservationAllowed)
            throw new AgentLinkException("permission_denied", "Desktop observation was revoked or approval UI is active.");
    }
    internal static void RequireLayout(string? observed, DisplayLayout current)
    {
        if (observed is null) throw new AgentLinkException("observation_required", "Observe the remote display before sending desktop input.");
        if (observed != current.LayoutId) throw new AgentLinkException("display_layout_changed", "The display topology, DPI, or resolution changed. Re-observe before sending input.");
    }
}

internal sealed class CappedMemoryStream(int maximum) : MemoryStream
{
    internal bool LimitExceeded { get; private set; }
    private void Check(long end)
    {
        if (end > maximum)
        {
            LimitExceeded = true;
            throw new AgentLinkException("capture_too_large", "PNG exceeds the 16 MiB artifact limit. Request a smaller crop.");
        }
    }
    public override void Write(byte[] buffer, int offset, int count) { Check(Position + count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(Position + buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(Position + 1); base.WriteByte(value); }
    public override void SetLength(long value) { Check(value); base.SetLength(value); }
}
