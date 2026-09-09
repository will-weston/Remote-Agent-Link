using AgentLink.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace AgentLink.Desktop;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--uia-worker") return 2;
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        WorkerResponse response;
        try
        {
            string line = ReadRequest();
            UiaRequest request = JsonSerializer.Deserialize<UiaRequest>(line, Json.Options) ?? throw Rules.Invalid("Missing worker request.");
            using var dpi = new Native.DpiScope();
            Native.EnsureInteractive();
            if (request.SessionId != Native.SessionId || Native.Layout().LayoutId != request.Layout.LayoutId)
                throw Native.Unavailable("The UIA worker session or display configuration does not match the request.");
            if (Native.VerifyWindow(checked((nint)request.WindowHandle)) != request.ProcessId)
                throw new AgentLinkException("window_changed", "The UIA target changed ownership.");
            UiaResult result = ReadTree(request);
            Native.EnsureInteractive();
            if (Native.Layout().LayoutId != request.Layout.LayoutId)
                throw new AgentLinkException("display_layout_changed", "Display layout changed while reading UI Automation.");
            response = new(Json.Element(result));
        }
        catch (AgentLinkException exception) { response = new(Code: exception.Code, Error: exception.Message); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            response = new(Code: "uia_provider_failed", Error: $"UI Automation provider failed ({exception.GetType().Name}). No partial tree is returned.");
        }
        Console.WriteLine(JsonSerializer.Serialize(response, Json.Options));
        return 0;
    }
    private static string ReadRequest()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 32768; i++)
        {
            int character = Console.Read();
            if (character == -1 || character == '\n') return builder.ToString();
            builder.Append((char)character);
        }
        throw Rules.Invalid("Worker request exceeds 32768 characters.");
    }
    private static UiaResult ReadTree(UiaRequest request)
    {
        TreeArgs tree;
        FindArgs? find;
        if (request.Method == "get_accessibility_tree")
        {
            tree = Arguments.Read<TreeArgs>(request.Arguments);
            Rules.Tree(tree);
            find = null;
        }
        else if (request.Method == "find_ui_element")
        {
            find = Arguments.Read<FindArgs>(request.Arguments);
            Rules.Find(find);
            tree = new(10, 2000);
        }
        else throw Rules.Invalid("Unsupported UIA worker method.");
        AutomationElement root = AutomationElement.FromHandle(checked((nint)request.WindowHandle))
            ?? throw new AgentLinkException("uia_provider_failed", "No UI Automation root is available.");
        TreeWalker walker = TreeWalker.ControlViewWalker;
        var elements = new List<UiaNode>();
        var timer = Stopwatch.StartNew();
        var identities = new Dictionary<int, bool>();
        int visited = 0, bytes = 2048;
        string? limit = null;
        bool stopped = false;
        void CheckTime()
        {
            if (timer.ElapsedMilliseconds >= 2500)
                throw new AgentLinkException("uia_timeout", "UI Automation exceeded its 2.5-second traversal budget; no partial tree is returned.");
        }
        bool Allowed(int process)
        {
            if (identities.TryGetValue(process, out bool allowed)) return allowed;
            (string name, bool critical) = Native.ProcessIdentity(checked((uint)process));
            allowed = !critical && !Rules.ProtectedProcess(name);
            identities[process] = allowed;
            return allowed;
        }
        void Visit(AutomationElement element, int? parent, int depth)
        {
            CheckTime();
            if (visited >= tree.MaxElements) { limit = "maxElements"; stopped = true; return; }
            int index = visited++;
            AutomationElement.AutomationElementInformation current = element.Current;
            if (!Allowed(current.ProcessId)) return;
            // Never ask a password element for Name/Value/Text, or inspect its descendants.
            bool password = current.IsPassword;
            string name = Rules.AccessibleName(password, () => current.Name);
            if (name.Length > 1024) { name = name[..1024]; limit ??= "textLength"; }
            string role = current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
            if (role == "Edit") role = "TextBox";
            bool offscreen = current.IsOffscreen;
            System.Windows.Rect bounds = current.BoundingRectangle;
            PixelRect? normalized = null;
            if (!bounds.IsEmpty)
            {
                if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                    Math.Abs(bounds.X) > 10000000 || Math.Abs(bounds.Y) > 10000000 || bounds.Width > 10000000 || bounds.Height > 10000000)
                    throw new AgentLinkException("uia_provider_failed", "UIA provider returned invalid physical geometry.");
                normalized = new((int)Math.Floor(bounds.X) - request.Layout.NativeOriginX,
                    (int)Math.Floor(bounds.Y) - request.Layout.NativeOriginY, (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
            }
            PixelPoint? clickable = null;
            if (!password && !offscreen && element.TryGetClickablePoint(out System.Windows.Point point))
            {
                if (double.IsFinite(point.X) && double.IsFinite(point.Y) && Math.Abs(point.X) <= 10000000 && Math.Abs(point.Y) <= 10000000)
                {
                    PixelPoint candidate = request.Layout.FromNative(new((int)Math.Floor(point.X), (int)Math.Floor(point.Y)));
                    if (request.Layout.Monitors.Any(m => new PixelRect(m.X, m.Y, m.Width, m.Height).Contains(candidate)))
                        clickable = candidate;
                }
            }
            var node = new UiaNode(index, find is null ? parent : null, depth, name, role, normalized, clickable, password, current.IsEnabled, offscreen);
            if (find is null || Rules.UiaMatch(node, find))
            {
                int size = JsonSerializer.SerializeToUtf8Bytes(node, Json.Options).Length + 1;
                if (bytes + size > Rules.MaxUiaBytes) { limit = "responseBytes"; stopped = true; return; }
                bytes += size;
                elements.Add(node);
            }
            CheckTime();
            if (password) return;
            AutomationElement? child = walker.GetFirstChild(element);
            if (depth >= tree.MaxDepth)
            {
                if (child is not null) limit ??= "maxDepth";
                return;
            }
            while (child is not null && !stopped)
            {
                Visit(child, index, depth + 1);
                if (stopped) break;
                CheckTime();
                child = walker.GetNextSibling(child);
            }
        }
        Visit(root, null, 1);
        return new(elements, limit is not null, limit, request.Layout.LayoutId);
    }
}
