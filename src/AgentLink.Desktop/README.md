# Windows desktop operations

`DesktopOperations(IConsentService)` implements `IOperationHandler` and `IDisposable`.
It uses public Win32 APIs and System.Drawing. UI Automation uses the public
WindowsDesktop `System.Windows.Automation` client (the managed client for Windows
UIA/COM), exclusively inside `DesktopWorker\AgentLink.Desktop.Worker.exe`.
There are no external desktop packages.

## Integration

- Run inside the intended logged-on user's Windows session, never Session 0.
  This library does not select or reconnect to another session.
- Pass the bound link's cancellation token to every invocation. A retained
  observation remembers that token so an idle disconnect invalidates it too.
  Hosts with separate per-request cancellation use the three-argument
  `InvokeAsync(request, requestCancellation, connectionLifetime)` overload:
  completing a request must not end an observation, and dropping the connection
  must invalidate it even if the original request source was already disposed.
  Supply a stable authenticated `OperationRequest.ActorId` (or
  `OperationContext.ActorId`) when sharing a handler between actors.
- Every invocation checks `observe` or `desktop-control` through the host policy.
  The broker authorizes these without a dialog while device access is enabled;
  input still requires exclusive actor ownership. The host must accurately
  maintain `IsConnected`, `ObservationAllowed`, and `DesktopAllowed`, cancel the
  bound link on disconnect and revoke on device blocking/human takeover.
  Input events recheck access immediately before injection.
- Desktop calls across handler instances are serialized. Do not retry a mutation
  after an uncertain outcome without a fresh observation.
- Human Stop/release calls `InvalidateObservations()` to cancel queued and active
  desktop work and rotate its authorization epoch, independently of RDP lifetime.
- Keep the entire `DesktopWorker` directory beside the consuming executable.
  The project reference propagates its executable, DLL, runtime configuration,
  dependency file, and Core DLL to build/publish output. The worker is
  framework-dependent in ordinary builds and requires the .NET 10 **Windows
  Desktop Runtime**, including WPF/UIA. The repository MSIX script explicitly
  republishes the worker self-contained; it is never replaced by in-process UIA.
- Dispose the handler when its binding ends. Disposal cancels active work.

## Surface and limits

| Tools | Capability | Behavior |
|---|---|---|
| `get_screen_size`, `get_cursor_position`, `rdp_get_display_layout` | observe | Physical virtual-desktop dimensions, normalized cursor coordinates, and monitor/DPI/layout metadata. |
| `take_screenshot`, `zoom_region` | observe | Native-resolution PNG returned through `OperationResult.Image` and `MimeType = "image/png"`. All four crop parameters or none; zoom requires all four and is limited to 1920×1080. |
| `move_mouse`, `click`, `drag_mouse`, `scroll` | desktop-control | Coordinate validation, five click buttons, three drag buttons, single/double clicks; scroll notches clamped to [-20,20], positive Y down. |
| `type_text`, `press_keys` | desktop-control | Unicode UTF-16 input and named chords; 32768 UTF-16 text units, 1–16 keys, maximum key-name length 32. `usePaste=true` explicitly returns `unsupported`. |
| `list_windows` | observe | Visible session windows, including minimized windows, normalized physical bounds; at most 2000 visible windows and 192 KiB JSON. |
| `activate_window`, `resize_window`, `close_window` | desktop-control | Unique case-insensitive title substring, no guessed ambiguous match. Exact resize/move geometry and foreground/state changes are verified. Close posts WM_CLOSE, never terminates a process; `closed=false, closeRequested=true` accurately reports a surviving window/dialog. |
| `get_accessibility_tree`, `find_ui_element` | observe | Foreground or explicit same-desktop window; bounded tree/filter traversal. Name takes precedence over text. Handles alone may select a search root. |
| `wait_milliseconds` | observe | One cancellable wait, clamped to [0,5000]. Does not refresh an observation. |

Coordinates always refer to the **remote virtual desktop's top-left physical
pixel**. Negative native monitor origins are normalized consistently for cursor,
capture, windows and UIA. Points in monitor gaps are rejected. Window/UIA bounds
can be outside the desktop for offscreen/minimized windows; these are observations,
not valid input destinations. Input requires a prior successful observation by
the same actor. Resolution, origin, topology, primary-monitor, DPI or scale changes
invalidate input until re-observation. Every native call runs in a thread-scoped
per-monitor-v2 DPI context. A never-shown, disabled, no-activate Win32 probe obtains
actual monitor DPI without changing the host's process-wide DPI awareness.

Captures are capped at 16384 pixels per axis, 32 Mi pixels, and **16 MiB encoded
PNG**, checked before allocation and during encoding. No automatic downscaling,
clipboard use, OCR, browser, shell, or arbitrary process tools are present.
Virtual desktops exceeding the 65536-per-axis absolute input range are rejected
for absolute mouse input rather than rounded to a different pixel.

## Safety and failure behavior

Session state and SDK-defined WTS lock flags, a visible window station, and the
active Default input desktop must all be verified. Lock, UAC/secure desktops,
disconnect and noninteractive execution fail explicitly. Window identities are
checked against the process's Windows session and current desktop, including
focus/capture/owner routing. Critical/system windows and Agent Link broker/control
windows are protected. Captures intersecting visible Agent Link control UI fail
instead of exposing its local access/setup controls. Unknown identities fail closed.

Only successfully synthesized key/button downs are owned and released, in reverse
order, including cancellation and permission loss. Human-preheld modifiers are
preserved; held non-modifier keys/buttons cause `input_busy`. Typing with a held
human modifier is rejected. Input-up cleanup is permitted after consent revocation
solely for the handler's own synthesized inputs. Failed cleanup revokes desktop
control and reports `input_release_failed`; it is never reported as success.
Windows rejection is not labeled UIPI unless Windows can establish that cause.
Like other full-trust desktop clients, this is not a sandbox against a compromised
same-user process, nor an atomic barrier against simultaneous physical input.

UIA providers never execute on a broker worker thread. Each request gets one
isolated process in a kill-on-close, single-process, 512 MiB Windows job. A
2.5-second traversal budget and 5-second hard process deadline apply. The parent
kills/reaps only its own worker on timeout/cancellation; cleanup/read draining is
also bounded. If a worker or reader cannot be reaped, subsequent UIA requests are
disabled until broker restart, preventing repeated process/thread accumulation.
Tree depth is 1–10, visits 1–2000, JSON 192 KiB, names 1024 characters; limit hits
report `truncated` and `limit`. Timeout/provider failures do not return partial
trees. Password elements never have their name/value/text fetched; their names
are redacted and descendants are not traversed. UIA value/text patterns are not
queried for any element.

The supported argument shapes follow the
[Microsoft Learn Windows 365 for Agents reference](https://learn.microsoft.com/en-us/microsoft-copilot-studio/mcp-windows-365-agents)
(reviewed 2026-09-08). This is an 18-tool subset, not Windows 365 wire/service
parity. Deliberate differences include unique substring matching rather than
fuzzy/80%-threshold guesses, explicit bounds/errors instead of screenshot resizing,
and the extra RDP display-layout observation.

## Safe validation

From the repository root:

```powershell
dotnet build .\src\AgentLink.Desktop\AgentLink.Desktop.csproj -c Release --nologo
dotnet run --project .\tests\Desktop.Tests\Desktop.Tests.csproj -c Release --no-launch-profile
```

The executable test harness has no third-party framework dependencies. It uses
synthetic geometry, fake consent, fake input sinks, in-memory streams and native
ABI metadata. It never captures the display, enumerates UIA, moves the cursor,
opens a desktop, or injects input. Worker payload propagation is checked as files
only. Real DPI/topology changes, screenshot rendering, focus restrictions, lock
transitions, and third-party UIA provider behavior still require an authorized,
dedicated Windows test session; they were not exercised on the shared machine.
