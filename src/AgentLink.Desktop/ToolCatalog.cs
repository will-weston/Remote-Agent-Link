using AgentLink.Core;
using System.Text.Json;

namespace AgentLink.Desktop;

internal static class ToolCatalog
{
    internal static IReadOnlyList<ToolDefinition> All => DesktopToolCatalog.All;
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
