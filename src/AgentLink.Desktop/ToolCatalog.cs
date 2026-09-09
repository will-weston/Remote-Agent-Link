using AgentLink.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentLink.Desktop;

internal static class ToolCatalog
{
    private static JsonObject Integer(int? min = null, int? max = null)
    {
        var schema = new JsonObject { ["type"] = "integer" };
        if (min is not null) schema["minimum"] = min;
        if (max is not null) schema["maximum"] = max;
        return schema;
    }
    private static JsonObject String(params string[] values)
    {
        var schema = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 };
        if (values.Length > 0) schema["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return schema;
    }
    private static JsonObject Object(params (string Name, JsonNode Schema)[] properties) =>
        new() { ["type"] = "object", ["properties"] = new JsonObject(properties.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Name, p.Schema))), ["additionalProperties"] = false };
    private static JsonArray Names(params string[] names) => new(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
    private static ToolDefinition Tool(string name, string description, bool mutating, JsonObject schema, params string[] required)
    {
        schema["required"] = Names(required);
        return new(name, description, JsonSerializer.SerializeToElement(schema), mutating, mutating ? "desktop-control" : "observe");
    }
    private static JsonObject Crop(bool optional)
    {
        var schema = Object(("x", Integer(0)), ("y", Integer(0)), ("width", Integer(1)), ("height", Integer(1)));
        if (optional)
            schema["dependentRequired"] = new JsonObject {
                ["x"] = Names("y", "width", "height"), ["y"] = Names("x", "width", "height"),
                ["width"] = Names("x", "y", "height"), ["height"] = Names("x", "y", "width") };
        return schema;
    }
    internal static IReadOnlyList<ToolDefinition> All { get; } = Build();
    private static IReadOnlyList<ToolDefinition> Build()
    {
        var click = Object(("x", Integer(0)), ("y", Integer(0)), ("button", String("Left", "Right", "Middle", "Forward", "Backward")), ("clickCount", Integer(1, 2)));
        click["dependentRequired"] = new JsonObject { ["x"] = Names("y"), ["y"] = Names("x") };
        var resize = Object(("title", String()), ("action", String("Resize", "Move", "Maximize", "Minimize", "Restore")),
            ("x", Integer(0)), ("y", Integer(0)), ("width", Integer(1)), ("height", Integer(1)));
        resize["dependentRequired"] = new JsonObject { ["x"] = Names("y"), ["y"] = Names("x") };
        resize["allOf"] = JsonNode.Parse("""[{"if":{"properties":{"action":{"const":"Resize"}}},"then":{"required":["width","height"]}},{"if":{"properties":{"action":{"const":"Move"}}},"then":{"required":["x","y"],"not":{"anyOf":[{"required":["width"]},{"required":["height"]}]}}},{"if":{"properties":{"action":{"enum":["Maximize","Minimize","Restore"]}}},"then":{"not":{"anyOf":[{"required":["x"]},{"required":["y"]},{"required":["width"]},{"required":["height"]}]}}}]""");
        var find = Object(("text", String()), ("role", String()), ("name", String()), ("windowHandle", Integer(1)));
        find["anyOf"] = new JsonArray(new[] { "text", "role", "name", "windowHandle" }.Select(n => (JsonNode)new JsonObject { ["required"] = Names(n) }).ToArray());
        var zoom = Crop(false);
        zoom["properties"]!["width"]!["maximum"] = 1920;
        zoom["properties"]!["height"]!["maximum"] = 1080;
        return Array.AsReadOnly(new[]
        {
            Tool("get_screen_size", "Observe remote physical virtual-desktop width and height; refresh the input layout.", false, Object()),
            Tool("get_cursor_position", "Observe normalized remote cursorX and cursorY.", false, Object()),
            Tool("take_screenshot", "Capture a native-resolution PNG (maximum 16 MiB). Supply all four crop parameters or none.", false, Crop(true)),
            Tool("zoom_region", "Capture native-resolution PNG region, at most 1920x1080. No resizing.", false, zoom, "x", "y", "width", "height"),
            Tool("move_mouse", "Move to a normalized remote physical-pixel position.", true, Object(("x", Integer(0)), ("y", Integer(0))), "x", "y"),
            Tool("click", "Click once or twice, at x/y or the current remote cursor.", true, click),
            Tool("drag_mouse", "Drag between remote physical-pixel positions using Left, Right or Middle.", true,
                Object(("startX", Integer(0)), ("startY", Integer(0)), ("endX", Integer(0)), ("endY", Integer(0)), ("button", String("Left", "Right", "Middle"))), "startX", "startY", "endX", "endY"),
            Tool("scroll", "Scroll at x/y in notch units; deltaX/right and deltaY/down are clamped to [-20,20].", true,
                Object(("x", Integer(0)), ("y", Integer(0)), ("deltaX", Integer()), ("deltaY", Integer())), "x", "y"),
            Tool("type_text", "Type Unicode without the clipboard. usePaste=true is explicitly unsupported.", true,
                Object(("text", new JsonObject { ["type"] = "string", ["maxLength"] = 32768 }), ("usePaste", new JsonObject { ["type"] = "boolean", ["default"] = false })), "text"),
            Tool("press_keys", "Press a chord of 1..16 standard/modifier/function keys; preserve preheld modifiers.", true,
                Object(("keys", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 16, ["items"] = String() })), "keys"),
            Tool("list_windows", "List visible windows in this remote session with normalized physical geometry.", false, Object()),
            Tool("activate_window", "Activate one visible window by case-insensitive title substring. Ambiguity is rejected.", true, Object(("title", String())), "title"),
            Tool("resize_window", "Resize, Move, Maximize, Minimize or Restore one title-substring match. Ambiguity is rejected.", true, resize, "title", "action"),
            Tool("close_window", "Request graceful close of one title-substring match. Ambiguous, system and protected windows are rejected.", true, Object(("title", String())), "title"),
            Tool("get_accessibility_tree", "Read the foreground window's bounded UI Automation tree; password nodes are redacted. Provider hard timeout is 5s.", false,
                Object(("maxDepth", Integer(1, 10)), ("maxElements", Integer(1, 2000)))),
            Tool("find_ui_element", "Find bounded UIA elements by name/text and role substring. name takes precedence; omit handle for foreground.", false, find),
            Tool("wait_milliseconds", "Wait once for ms clamped to [0,5000], cancellable on link disconnect.", false, Object(("ms", Integer())), "ms"),
            Tool("rdp_get_display_layout", "Observe normalized remote physical monitor geometry, native origin, DPI and layoutId. Refreshes stale input layout.", false, Object())
        });
    }
    internal static object Validate(OperationRequest request)
    {
        if (request.Arguments.ValueKind != JsonValueKind.Object) throw Rules.Invalid("Arguments must be an object.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ToolDefinition tool = All.FirstOrDefault(t => t.Name == request.Method)
            ?? throw new AgentLinkException("unknown_tool", "Unknown desktop operation.");
        JsonElement properties = tool.InputSchema.GetProperty("properties");
        foreach (JsonProperty property in request.Arguments.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out JsonElement schema) || !seen.Add(property.Name) || property.Value.ValueKind == JsonValueKind.Null)
                throw Rules.Invalid($"Unknown, duplicate, or null argument: {property.Name}.");
            bool validType = schema.GetProperty("type").GetString() switch
            {
                "integer" => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out _),
                "string" => property.Value.ValueKind == JsonValueKind.String,
                "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "array" => property.Value.ValueKind == JsonValueKind.Array,
                _ => false
            };
            if (!validType) throw Rules.Invalid($"Invalid JSON type for {property.Name}.");
        }
        foreach (JsonElement name in tool.InputSchema.GetProperty("required").EnumerateArray())
            if (!request.Arguments.TryGetProperty(name.GetString()!, out _)) throw Rules.Invalid($"{name.GetString()} is required.");
        object args = request.Method switch
        {
            "move_mouse" => Arguments.Read<PointArgs>(request.Arguments),
            "click" => Arguments.Read<ClickArgs>(request.Arguments),
            "drag_mouse" => Arguments.Read<DragArgs>(request.Arguments),
            "scroll" => Arguments.Read<ScrollArgs>(request.Arguments),
            "take_screenshot" or "zoom_region" => Arguments.Read<CaptureArgs>(request.Arguments),
            "type_text" => Arguments.Read<TextArgs>(request.Arguments),
            "press_keys" => Arguments.Read<KeysArgs>(request.Arguments),
            "activate_window" or "close_window" => Arguments.Read<TitleArgs>(request.Arguments),
            "resize_window" => Arguments.Read<ResizeArgs>(request.Arguments),
            "get_accessibility_tree" => Arguments.Read<TreeArgs>(request.Arguments),
            "find_ui_element" => Arguments.Read<FindArgs>(request.Arguments),
            "wait_milliseconds" => Arguments.Read<WaitArgs>(request.Arguments),
            _ => Arguments.Read<EmptyArgs>(request.Arguments)
        };
        switch (args)
        {
            case ClickArgs a: Rules.Pair(a.X, a.Y); Rules.Button(a.Button); if (a.ClickCount is < 1 or > 2) throw Rules.Invalid("clickCount must be 1 or 2."); break;
            case DragArgs a: Rules.Button(a.Button, true); break;
            case TextArgs a: Rules.Text(a); break;
            case KeysArgs a: KeyMap.Parse(a.Keys); break;
            case TitleArgs a: Rules.Title(a.Title); break;
            case ResizeArgs a: Rules.Resize(a); break;
            case TreeArgs a: Rules.Tree(a); break;
            case FindArgs a: Rules.Find(a); break;
        }
        return args;
    }
}
