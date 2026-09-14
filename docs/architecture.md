# Architecture

```text
Local Copilot -> stdio MCP -> local broker -> native COM/DVC plugin
                                      mutual TLS over RDP
Remote Copilot <- Copilot SDK <- remote broker <- native WTS transport
                                      |
                              desktop operations
                                      |
                              isolated UIA worker
```

The package contains both endpoint roles. The client plugin is not the remote server: the remote broker opens `WTSVirtualChannelOpenEx` from the logged-in user's session. Session 0 is rejected. The broker is a normal user process independent of Windows App, the COM server, MCP clients, and terminal windows. Keep it running for authorized disconnected work.

Each user/session has distinct control and native transport pipes. A TLS endpoint certificate identifies the user endpoint; the remote Windows logon SID distinguishes successive logons even when the numeric session ID is reused. Channel admission uses the authenticated RDP session without fingerprint pairing. The device-access switch defaults to Yes and authorizes supported operations without per-action dialogs. Reconnection creates a new epoch and does not restore input ownership or stale observations.

Each MCP process has an immutable explicit target and an actor ID. The local broker stamps that actor with the actual connecting process ID and start time. The remote broker prefixes it with authenticated peer and channel identity. Desktop observations and the exclusive input lease belong to that actor, not to every process connecting from the same machine. A transport reconnect retains the same endpoint/logon connection ID but creates a fresh epoch and input actor scope.

Durable Copilot sessions/tasks instead belong to the authenticated endpoint principal (or the explicit local-logon principal). They do not use the transient MCP process identity: a new local MCP process must be able to retrieve previously accepted work after reconnect. Other endpoints remain isolated. Every task retains its explicit workspace/tool/deadline scope; turning off device access blocks new operations and requests cancellation of broker-owned work.

The MCP inventory is declared once from shared Core schemas: five management,
18 desktop and nine agent tools. Endpoint `describe` supplies invocation-time
support, not replacement declarations. A host can retain its startup catalog
through binding, access changes and reconnects without list-change notifications.
Authenticated MCP presence heartbeats supply the dashboard's agent status;
saved configuration, connected RDP computers and connected agents are distinct.

The local outgoing peer rejects reverse requests. Remote agents cannot tunnel arbitrary local tool calls. Own-session mode accesses the same remote broker directly, with the same desktop arbiter.

Local native plugin lifecycle discovery is separate from DVC readiness.
`Initialize` starts an asynchronous same-logon reporter; `Connected`,
`Disconnected`, termination, pipe loss and heartbeat expiry update or remove its
MCP-visible entry. The reporter does not require TLS, a remote broker, or a
remote channel-open request once the plugin has been initialized. A fresh GUID
identifies each plugin object; reconnect on that object changes its epoch.
Connection labels are opaque instance labels, not inferred hostnames.

These `localRdpClient` entries have no remote endpoint/logon identity and no
implemented operation surface. Explicit binding and metadata work; desktop,
input ownership and coding calls fail closed. Existing `remoteEndpoint` targets
still operate through the authenticated remote broker. The raw DVC pipe does
not carry a plugin-instance correlation preface, so these two kinds are not
merged, deduplicated by a label, or silently substituted for each other.
Local rendering/input remains an [open public-contract gap](../src/Native/README.md#local-rdp-surface-control-contract-gap),
not a fallback to `DesktopOperations` on the client machine.

A separate optional public window-service probe reports versioned diagnostics
on `.Clients.Surfaces`. It uses the manager belonging to that plugin, validates
process/logon ownership, and publishes raw geometry with a generation. The
broker joins it only to the same reporter process, instance and connected epoch.
Service absence or failure does not affect lifecycle discovery. Ownership-checked
HWNDs still have `identity_ambiguous` status; neither input nor capture is enabled.
Full-desktop identity corroboration, background connection-targeted input and
fresh minimized capture remain open gates, not implied by public service lookup.

Desktop calls use physical remote virtual-desktop coordinates, not the scaled client window. UI Automation runs out of process so a hung provider can be terminated without blocking transport or the broker's human control window.

Copilot task state is separate from transport state. Task acceptance, dispatch ambiguity, event cursors, and final outputs belong to the durable remote store. Losing an acknowledgement is not permission to automatically replay a mutation or delegated prompt.

## Deployment boundaries

- x64 only initially; native/RID payloads must not be mixed across architectures.
- The broker and MCP host have separate publish directories to isolate their dependency graphs.
- The UIA worker must retain its complete executable/dependency/runtime bundle.
- Copilot CLI authentication and any opt-in interactive runtime are owned by the remote user.
- MSIX installation alone does not start a remote broker. Open the app once on each computer; its device-access switch defaults to Yes. Packaged startup follows Windows/user policy.
- Modern discovery needs no legacy registry entry. The explicit Windows 11 legacy setting manages only this app's per-user AddIns leaf and uses a separate packaged out-of-process COM class.
- No RDP client patch, private SDK, internal feed, in-process plugin DLL, or out-of-band network listener is a product dependency.

## RDP loading modes

The modern AppExtension continues to advertise the original `IWTSPluginFactory`
and `RALink.v1` initialization name. Classic AddIns discovery uses a distinct
CLSID exposing `IWTSPlugin` directly. Both reuse the same out-of-process listener
and authenticated broker transport; the remote WTS endpoint does not select a
loading model.

The Windows-user preference is captured for each new COM activation. A factory
for the unselected path reports zero plugins and rejects creation. Disabled
direct legacy activation returns an error and a null output, not a successful
null plugin. Existing factories and connections are not retargeted; restart the
RDP client to apply a change. A malformed or inaccessible preference blocks new
activation rather than silently choosing a path.

The broker and COM EXE share the package's virtualized
`HKCU\Software\RemoteAgentLink\PluginSettings\LoadingMode` DWORD (0 Modern,
1 Legacy). Only `HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\RemoteAgentLink`
is excluded from registry write virtualization. Windows 11 is required for the
automatic integration; there is no global/downlevel virtualization-disable
fallback. See the [installation caveats](installation.md#modern-and-legacy-rdp-loading).
