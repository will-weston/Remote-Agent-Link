# Windows 365-inspired API compatibility

Reference: [Microsoft Learn Windows 365 computer-use tool catalog](https://learn.microsoft.com/en-us/microsoft-copilot-studio/mcp-windows-365-agents), plus the [official repository](https://github.com/microsoft/windows-365-for-agents) observed at `36330536f24aebcdaeca96a53263a826cc2de226`.

This is a behavioral subset, **not** the Windows 365 service or a claim of complete wire compatibility. The actual MCP `tools/list` response is authoritative for implemented schemas.

| Group | Initial operations |
| --- | --- |
| Observation | `get_screen_size`, `get_cursor_position`, `take_screenshot`, `zoom_region` |
| Mouse | `move_mouse`, `click`, `drag_mouse`, `scroll` |
| Keyboard | `type_text`, `press_keys` |
| Windows | `list_windows`, `activate_window`, `resize_window`, `close_window` |
| Accessibility | `get_accessibility_tree`, `find_ui_element` |
| Timing | `wait_milliseconds` |
| RDP extensions | connection discovery/binding, layout metadata, acquire/release control |
| Copilot extensions | session management, durable delegation, events, cancellation, non-permission questions |

Important differences:

- A binding selects an existing paired RDP connection. It never provisions, resets, checks in, logs off, or deletes a Cloud PC.
- Images are PNG MCP image blocks. Coordinates are normalized physical remote virtual-desktop pixels; display changes invalidate prior observations.
- Permission and unavailable-desktop errors are explicit. No implicit clipboard fallback or secure-desktop interaction.
- Each MCP process binds to one target and cannot silently switch it. Multiple targets require separate processes.
- Browser/Edge, OCR, clipboard, direct shell/Python, process-management, Graph provisioning, and web screen-sharing APIs are deferred and must not appear as implemented tools.
- Enterprise security/compliance features of the Windows 365 cloud service are not inherited by this extension.
- Copilot collaboration tools are project-specific, not Windows 365 APIs.

Real Windows App transport, multi-monitor click/capture correspondence, and live Copilot handoff must be recorded separately from synthetic protocol and SDK-fake coverage.
