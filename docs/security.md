# Security and device access

Remote Agent Link is **not an OS sandbox**. The broker, desktop APIs, and an authorized Copilot runtime execute with the remote user's normal rights. Same-user full-trust compromise and local administrators are outside its isolation guarantee.

## Authorization

- **Allow agents to use this device defaults to Yes. There is no fingerprint pairing or per-action broker approval.** An agent reaching this Windows session through the authenticated RDP channel or same-user/logon local IPC can use supported operations while access is enabled. Install/run it only in sessions you trust.
- This deliberately replaces the earlier independent application-pairing barrier with trust in the existing authenticated RDP session. It is not an additional check that the caller is a particular human or a particular AI product. The RDP client's server/authentication policy and the Windows account's rights still matter.
- TLS protects broker traffic and proves possession of endpoint certificate keys. Endpoint identities and Windows-logon checks still isolate targets and task ownership; labels supplied by peers are not identity proof. There is no arbitrary network endpoint or Internet-facing MCP listener.
- Endpoint keys live in the current user's Windows certificate store. Application state uses a user-only ACL. No global certificate trust changes or saved fingerprint allowlist are required for connection.
- **No** blocks agent observation, input and coding operations on this device, invalidates desktop epochs and requests cancellation of broker-owned active work. RDP, other enabled computers and external user applications are not closed. Cancellation is cooperative; already-executed effects are not rolled back.
- Re-enabling does not replay work or restore old input ownership. Input remains exclusive to one MCP actor; another agent cannot release or preempt its lease. Disconnect, lock and human stop invalidate desktop access until the normal connection/observation/control prerequisites are restored.
- The saved switch is shared across this Windows user's logons. New operations reread it; active brokers reconcile it every second. A missing/unreadable live setting blocks access. Failed persistence cannot undo a local No by reloading an older saved Yes.
- The access switch and local administration UI are not model-callable operations. Broker windows remain protected from computer-use targeting even though per-action approval dialogs are gone.
- Status/logging observer failures are reported through bounded diagnostics and cannot interrupt revocation. A second-launch window request reports `ui_unavailable` when no control surface can accept delivery.

Desktop pixels, accessibility text, file contents, and other agents' messages are
untrusted task data. They cannot change the device switch, target or task scope.
The MCP client's own tool-permission policy is separate; this application does
not override it. No-dialog operation does not bypass enterprise-managed runtime
requirements or the declared workspace/tool/budget limits.

All 32 MCP declarations remain visible independently of device readiness. Failed
operations must be reported against the same target, never retried through a
local shell, another desktop or an unrelated coding-agent session. These
instructions cannot prevent an external MCP host from exposing its own shell.
Connected-agent status comes from authenticated, expiring presence heartbeats;
it is not proof that an agent is currently controlling the desktop.

## Limits and ownership

Desktop operations must fail on unavailable/protected desktops, stale layout observations, ambiguous window selection, and denied input. Secure-desktop/UAC access, elevation, clipboard-backed paste, and credential extraction are not supported.

Non-UI continuation requires an explicitly scoped unattended task, enabled device access and finite task/workspace/tool limits. Managed work uses the remote user's Copilot account and may consume its credits without an additional broker dialog. SDK credit caps are soft limits, not guaranteed hard billing ceilings. A disconnected RDP session may still be logged off by enterprise policy; the project never changes that policy.

An attached interactive CLI remains user-owned. Detach must not terminate it. A plain terminal is not automatically an attachable runtime, and MCP never accepts arbitrary runtime URLs or connection secrets.

No local GitHub token is copied to the remote machine. The remote runtime uses the remote user's existing authentication. Do not place runtime connection tokens, prompts, screenshots, signing keys, or private task outputs in source control.

## RDP client integration

Local plugin lifecycle reports use a separate same-user/session/logon pipe.
They identify a plugin object, not a remote authenticated Windows endpoint.
No remote name, fingerprint, HWND or permissions are inferred from a report.
Optional surface diagnostics on another pipe contain public-service HWND and
geometry candidates, tied to the same reporter process/start time and current
instance epoch. User/session/logon and HWND/PID checks establish only process
ownership, not exact connection identity, protected-UI exclusion or pixel freshness.
All candidates remain ambiguous and grant no operation authority. There is no
parent-marker experiment, process/title guessing, global-input fallback or
silent routing to another endpoint.
`localRdpClient` targets are metadata-only; they cannot route to the broker's
own `DesktopOperations`, acquire/release another actor's input ownership, or
delegate coding work. A later authenticated DVC endpoint is a different target
and never changes an existing MCP binding. The device switch does not hide
discovery and cannot be changed through lifecycle reporting.

RDP loading selection is not an authorization grant and is not exposed as an MCP
tool. Modern and legacy activation retain the same out-of-process transport,
authenticated-session and device-access boundaries. Legacy setup writes only the current user's
owned AddIns leaf, never HKLM or conventional COM registration. Conflicting
package-family ownership or a changed plugin value is not overwritten. Cleanup
preserves unrelated values and subkeys.

The MSIX declares `unvirtualizedResources` solely for a selective Windows 11
registry-write exclusion of that leaf; the loading preference and all other
registry/file-system virtualization remain unchanged. This is an additional
restricted-capability Store approval gate, not a general Store eligibility
claim. Excluded registry data can survive uninstall: switch to Modern and
resolve any cleanup warning before removing the package.

## Diagnostics and retention

Structured diagnostics record random run/channel correlation IDs, allowlisted
method/error categories and connection transitions, never endpoint fingerprints,
SIDs, arbitrary peer strings, exception messages or operation payloads. Private
rotating files, queue/rate bounds and explicit loss counters limit their impact.
They are a sampled troubleshooting history, not an audit log. Debug adds timings,
not screenshots, keys or prompts. See [diagnostics and retention](diagnostics.md).
Durable task outputs remain separate and may contain private code: retain them
only as needed and do not export the entire data directory as diagnostic logs.

Development MSIX signing is distinct from endpoint TLS identity. The packaging script reuses an approved persistent Windows signing identity and fails before producing replacement outputs when its private key is missing or its history is inconsistent. A genuinely first-time development identity retains a non-exportable key in the Windows user certificate store; only its public certificate is exported. A public `.cer` cannot recover a discarded private key. The script never rotates an established signer or installs trust automatically. See [signing and recovery](installation.md#build-and-signing).
