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

- A binding selects an exact discovered target without fingerprint pairing. `remoteEndpoint` selects an authenticated broker; `localRdpClient` selects a plugin lifecycle instance even without DVC readiness. The latter is currently metadata-only, not a local rendering/input implementation. It never provisions, resets, checks in, logs off, or deletes a Cloud PC.
- Images are PNG MCP image blocks. Coordinates are normalized physical remote virtual-desktop pixels; display changes invalidate prior observations.
- Permission and unavailable-desktop errors are explicit. No implicit clipboard fallback or secure-desktop interaction.
- Each MCP process binds to one target and cannot silently switch it. Multiple targets require separate processes.
- `ConnectionInfo` adds `targetKind`, `state`, optional `localSurfaceStatus` and optional `localSurface` diagnostic service metadata. Local plugin entries have null `peerFingerprint`, `remoteSessionId` and `logonId`; their GUID identity is not a remote endpoint identity. Surface status distinguishes pending/unavailable/error/unsupported-session/invalid-geometry/ambiguous identity; even ownership-checked HWND metadata keeps connection identity, background input and fresh-minimized-capture flags false. They return an empty supported catalog, not fewer MCP declarations. These local IPC metadata additions require updating strict older broker/MCP readers together. Authenticated endpoint identity values and remote-broker execution semantics remain unchanged.
- All 32 declarations are stable from startup: five management, 18 desktop and nine agent tools. `tools/list` does not wait for the broker or remote catalog. Binding, device access, support changes and disconnects do not emit `notifications/tools/list_changed`. Existing connection identity fields and the first JSON text block remain unchanged; bind guidance is an additional text block reporting actual endpoint support.
- Declared tools can return `not_bound`, `unsupported_on_endpoint`, `device_access_disabled` or an explicit transport error. The device switch defaults to Yes; no per-action broker approval is requested. Desktop input still uses exclusive actor ownership and a fresh display observation.
- A host's cached-tool error is not evidence of RDP disconnection. Inspect the same target and report the error; never substitute local shell automation, `--current-session`, or a coding-agent session for failed remote desktop tools.
- Browser/Edge, OCR, clipboard, direct shell/Python, process-management, Graph provisioning, and web screen-sharing APIs are deferred and must not appear as implemented tools.
- Enterprise security/compliance features of the Windows 365 cloud service are not inherited by this extension.
- Copilot collaboration tools are project-specific, not Windows 365 APIs.

Real Windows App transport, multi-monitor click/capture correspondence, and live Copilot handoff must be recorded separately from synthetic protocol and SDK-fake coverage.
