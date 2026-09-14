# Installation, operation, and acceptance

## Build and signing

Build on Windows x64 with PowerShell 7.2+, the .NET SDK in `global.json`, Visual Studio's C++ desktop workload, and a Windows SDK. `scripts\build.ps1` discovers the installed MSVC toolset rather than requiring the author's Visual Studio path.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1 -SkipBuild
.\scripts\package.ps1 -DevSign -SkipBuild
```

The package and SHA-256 digest are written to `artifacts\packages`. Managed applications, the WinUI 3/Windows App SDK runtime, and the UIA worker are published self-contained for `win-x64`. The staging layout is printed for inspection. Keep the complete Broker directory, including native runtime DLLs, PRI indexes, XBF markup and assets; this is not a single-file application.

The shell uses the public Windows App SDK 2.4 runtime and its modular WinUI dependencies. Its appearance follows the public [Windows design principles](https://learn.microsoft.com/windows/apps/design/design-principles), [Mica guidance](https://learn.microsoft.com/windows/apps/design/style/mica), and [accessibility guidance](https://learn.microsoft.com/windows/apps/design/accessibility/accessibility-overview). Reference client code is not a build dependency. SDK binary licenses and notices remain separate from the project's source license.

Startup failures report a phase and HRESULT. The bounded `ui-startup.log` in the session's private data directory records startup phases, payload presence and stack symbols, but excludes exception messages, arguments and source paths. The shell reads high-contrast state through Win32 and observes its window's settings/theme notifications rather than subscribing to a CoreWindow accessibility event.

The packaging script runs `AgentLink.Broker.exe --check-installation` and `AgentLink.RdpPlugin.exe --check-startup` against that layout. These load the native DLLs, check their exports and COM startup ordering, and check the self-contained UIA payload without registering plugin classes, creating endpoint credentials, opening an RDP channel, or accessing the desktop. They are not packaged COM activation or live-session tests.

`scripts\test-package.ps1` checks all block-map payload hashes and binds signed metadata to the CMS signature; `-Certificate` can require the expected public signer. It also rejects accidental private-state or CLI-runtime inclusion. These offline checks do not replace Windows installation/signature policy: a development certificate remains untrusted until an administrator explicitly approves it.

Without `-DevSign`, the package is unsigned unless `-SigningCertificateThumbprint` selects an existing certificate from the current user's `My` store. Set `-Publisher` to its exact subject and increment the four-part `-Version` for updates. Keep production signing outside source control.

`-DevSign` reuses the public signer from the highest numeric certificate-bearing package version in `artifacts\packages` and the ignored public pin at `artifacts\signing\development.cer`. If both exist, they must agree; multiple different candidates are an error, not an invitation to pick another certificate. Unsigned builds do not replace a prior signing identity; a signed package missing its `.cer` must have that public certificate restored or use explicit certificate selection. The matching accessible private key must be in `CurrentUser\My` or `LocalMachine\My`. Publisher subject, validity, Code Signing EKU, digital-signature usage, and RSA key access are checked **before building, publishing, or replacing package outputs**. Missing/expired keys and inconsistent signer state never trigger automatic rotation.

For a genuinely first-time development identity with no prior package/pin or matching current-user certificate, the script first checks the current user's installed `RemoteAgentLink` packages and reads `My`, `TrustedPeople`, `TrustedPublisher`, `Root`, and `CA` in both current-user and local-machine certificate stores. An installed package, matching public certificate (including expired certificates), or unreadable discovery source prevents automatic creation. Cleaning artifacts does not authorize replacing an existing identity; restore the approved public certificate/pin and matching private key, or use explicit certificate selection.

Only when no prior identity is found does the script create a six-month, non-exportable RSA key in the current user's Windows CNG storage and retain its certificate in `CurrentUser\My`. Subsequent runs reuse it, including after cleaning artifacts when that personal-store identity is unambiguous. Only public `.cer` files are written to artifacts; no PFX or private key is written to the repository. No trust-store changes or package installation are performed. Preserve the Windows user profile/key storage; for a controlled update channel or portable backup, provision and manage a persistent signing certificate explicitly instead.

Older packaging versions used ephemeral signing keys. If a private key was discarded, retaining its `.cer`, trusting its public certificate, or keeping the package installed does **not** recover it. Restore the matching certificate and private key from a separately retained secure backup if one exists, or deliberately select another available current-user certificate with `-SigningCertificateThumbprint` and the exact `-Publisher`. A different signer requires explicit trust/update planning; the script cannot promise an unchanged-trust update without the original key. Explicit signing also exports its public `.cer`; conflicting development pin/output history must be reconciled deliberately, not silently discarded.

An absent certificate/private-key association is not proof that Windows deleted the underlying key. A prior signing-tool PFX import can leave a persisted software key after the PFX or certificate object is removed. In that case, an administrator/developer can compare **public keys only** and deliberately reassociate the exact original public certificate with its existing matching key, then verify signing from a fresh process. This does not reconstruct a key from a `.cer`, require private-key export, or authorize choosing a different identity. The packaging script does not enumerate or recover orphaned keys automatically.

Run `.\scripts\test.ps1 -Suite Signing -SkipBuild` for isolated signing regression checks, also included in the default suite and CI. The checks use a unique test publisher, verify cross-process key reuse and signing with `signtool`, and remove only their own test certificates, keys, and scratch artifacts; they never change certificate trust.

## Install the same package on both endpoints

Compare the package digest through your trusted distribution channel. For a development package only, an administrator can inspect and explicitly trust the public signing certificate in the appropriate Windows Trusted People store. Do not confuse package signing with the separate endpoint TLS identities. A future Store distribution uses Store installation/signing; this repository has not been submitted or approved.

Install the signed package using Windows App Installer or:

```powershell
Add-AppxPackage -Path .\RemoteAgentLink_0.5.0.0_x64.msix
```

Installation does not enable legacy AddIns registration. Modern is the default;
legacy integration requires an explicit choice in Settings. Neither mode uses
`regsvr32`, an internal client build as a product dependency, or the sample's
development-machine COM registration.

Open **Remote Agent Link** from Start on both devices. The default `both` role automatically starts the local listener and remote-channel retry loop; no Connect button or role selection is needed. Advanced deployments can still select a role explicitly:

```powershell
ralink-broker.exe --role local
ralink-broker.exe --role remote
ralink-broker.exe --role both
```

Only one broker runs per Windows user/session. Opening the application again requests the existing window through authenticated local IPC. Installation alone does not start the remote broker; open it once on each computer. **Allow agents to use this device** defaults to **Yes**. There is no pairing or per-action broker approval. The optional packaged startup task is disabled by default; **Settings** shows its current Windows state and opens Startup Apps for explicit opt-in. Organization/user-disabled startup is not bypassed. Startup uses the default `both` role.

Update **both endpoints** when moving from a pairing-based release to 0.5.0.0.
The simplified model trusts the authenticated RDP session and Windows user rather
than a separately approved fingerprint. Enabled agents can use supported desktop
and scoped coding operations without a broker prompt. Existing running processes
do not change behavior merely because a new package was built.

### DVC-independent MCP discovery

Update the local broker, native plugin and MCP together, then restart the RDP
client to load the new plugin code. Once the RDP client initializes a plugin,
MCP can list its `localRdpClient` instance without a working `RALink.v1` channel
or remote broker. Starting the local broker afterward also works: initialized
plugins retry their lifecycle pipe. This does not force Windows App to load
an extension it has not chosen to initialize; on-demand activation remains
client-controlled. Sixteen concurrent lifecycle reporters are admitted, with
excess reporters retrying rather than taking over an existing pipe.

These entries report initialized/connected/disconnected state and can be bound
by exact ID, but **local RDP rendering/input remains unsupported**. Do not
interpret a connected lifecycle as working desktop tools. For the currently
implemented remote desktop/agent operations, both endpoint brokers and the
authenticated DVC remain necessary; select the separate `remoteEndpoint` ID.
The existing Home peer view describes those authenticated channels.
The [native contract investigation](../src/Native/README.md#local-rdp-surface-control-contract-gap)
records the remaining blocker, not a claim of completed local control.

Approved live acceptance must verify local-only discovery with the remote
broker stopped, two plugin instances in one RDP process, disconnected-state
reporting, broker restart, and entry removal after plugin termination. Binding
the client entry must never start controlling the host desktop or a newly
arriving remote endpoint. The isolated native lifecycle and managed tests are
not evidence that a shipped RDP client invokes the required lifecycle callbacks.

Use the registered `ralink-mcp.exe` execution alias for installed MCP clients, not the versioned executable path under `WindowsApps`. Direct execution can run without package identity and fail dependency loading even when the DLL exists. Installed acceptance must check the MCP process's actual package identity as well as its tool responses; a loose-file launch is not equivalent to package activation.

### Modern and legacy RDP loading

In **Settings > RDP client loading**, **Use modern RDP plugin loading** is On by
default. Off selects **Legacy compatibility** for outgoing RDP connections on
this PC, for this Windows account. It is independent of the local/remote broker
role and the device-access switch. A remote endpoint that only accepts incoming
channels needs no matching loader selection.

| Mode | Discovery and activation |
| --- | --- |
| Modern | MSIX `com.microsoft.rdp.plugin.wtsplugin` AppExtension, original factory CLSID `583E23E9-3A9F-461C-A3CA-6F99F2625B78`, `IWTSPluginFactory`, and `RALink.v1` |
| Legacy | Per-user AddIns `Name={77A190C4-C7EB-4DB2-A317-9CFADA8F2A53}`, separate packaged COM class exposing `IWTSPlugin` directly |

Both modes keep the implementation in `AgentLink.RdpPlugin.exe`, outside the
RDP client. Legacy targets clients that permit COM **local-server** activation
and the public DVC interfaces; it does not promise compatibility with every old
RDP client or an in-process-only loader. No DLL path, `LocalServer32` write,
elevation, or machine-wide registration is used.

**Restart the RDP client after changing modes.** Existing factory objects and
connections keep their original mode; changing the setting does not disconnect
them, alter trust, or restore/revoke desktop grants. A registered entry is not
proof of an active channel. Modern on-demand loading also calls
`GetPluginCount`, so the two paths have separate activation identities rather
than globally returning zero from the modern factory. The unselected factory
reports zero; disabled direct legacy activation returns an explicit error.

Automatic legacy registration requires **Windows 11** and the installed
package's scoped integration. Modern retains the existing Windows 10 build
19041 package minimum. The app does not attempt a global registry-virtualization
opt-out on older Windows or launch unpackaged helpers to evade virtualization.
An unpackaged developer run cannot configure the installed integration.

The only excluded registry path is:

```text
HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\RemoteAgentLink
```

The app records `OwnerPackageFamilyName` beside `Name`. It will not overwrite
another package's entry or a changed `Name`. Reapplying the same owned
registration is safe, and an interrupted owned registration has a repair action.
Modern is saved before cleanup, so a cleanup error still blocks new legacy
activations and is reported rather than hidden. Unrelated values/subkeys are
preserved. The mode itself stays in the package's virtualized, per-user
`Software\RemoteAgentLink\PluginSettings` key as DWORD `LoadingMode` (0 or 1).
Missing defaults to Modern; invalid data blocks new activations until repaired.

**Before uninstalling, switch to Modern and resolve any cleanup warning.**
Excluded registry entries can survive MSIX uninstall. If the app was removed
first, reinstall the same package family, select Modern, complete cleanup, then
uninstall. Do not delete the AddIns parent or another plugin's entries. A different
Store package family is an identity/migration change, not a supported side-by-side
replacement for the same activation CLSIDs.

**Store gate:** the package now declares `unvirtualizedResources` for this one
selective registry exclusion. Current Microsoft capability guidance limits its
intended scenarios; Store approval for this RDP use is not established and must
not be assumed. Successful sideloading does not grant that approval. Partner
Center identity, capability approval, uninstall behavior, and client acceptance
are separate prerequisites; no Store submission is performed by these scripts.

Public contracts: [DVC registration](https://learn.microsoft.com/windows/win32/termserv/dvc-plug-in-registration),
[selective virtualization](https://learn.microsoft.com/windows/msix/desktop/flexible-virtualization),
and [restricted capabilities](https://learn.microsoft.com/windows/apps/package-and-deploy/app-capability-declarations#restricted-capabilities).
The two sample repositories establish the factory/direct-COM distinction, not a
universal shipped-client support guarantee.

### Let Copilot find the MCP server

The tray application is the **broker**, not a listening MCP server. Each MCP client starts a separate stdio process; it cannot discover the broker as an HTTP endpoint or attach to another client's stdin/stdout.

Open **Setup** in the app and choose **Add to Copilot CLI**, or run:

```powershell
copilot mcp add remote-agent-link -- "$env:LOCALAPPDATA\Microsoft\WindowsApps\ralink-mcp.exe"
```

This uses the official CLI to register the stable execution alias, without changing desktop permissions. The app leaves existing/custom entries alone. Start a new Copilot CLI session and use `/mcp show remote-agent-link` to inspect its loaded server. A saved configuration is not proof that an already-running agent has reloaded it.

All 32 tools are declared from startup, including screenshots, mouse/keyboard and
agent operations. `rdp_list_connections` discovers computers;
`rdp_bind_connection` chooses the exact target and checks endpoint support.
Binding never adds/removes declarations or emits `notifications/tools/list_changed`.
A successful bind preserves connection identity and reports the endpoint's
supported tool names. No host catalog refresh is needed.

The same declarations remain available while unbound, offline or blocked.
Invocation returns an explicit `not_bound`, `unsupported_on_endpoint`,
`device_access_disabled` or connection error as appropriate. A dedicated drawing
command is unnecessary: use screenshot and input tools on the selected computer.

An MCP host's stale/cached-tool error is not proof that RDP disconnected. Inspect
`rdp_get_connection` without changing targets, then report the problem rather
than switching to a local shell or coding-agent workaround. `--current-session`
explicitly selects the machine running that MCP process; it is never a fallback
for an unavailable remote desktop. A fixed `--connection <connectionId>` remains
available for saved target configuration, not as a catalog-cache workaround.

Home reports connected agents separately from saved MCP setup and RDP computers.
Each initialized MCP process registers authenticated presence, refreshes it every
15 seconds, and unregisters on clean shutdown. Missing heartbeats expire after
45 seconds; an abrupt process exit can therefore remain visible briefly.

The default user profile is `%USERPROFILE%\.copilot`; `COPILOT_HOME` selects a different profile. The Setup page shows the profile it checked. Workspace configuration, disabled-server settings and enterprise policy can override user configuration. Other MCP clients have their own configuration stores; `.vscode\mcp.json` is not Copilot CLI configuration. The manual setup section provides JSON and explains `--current-session` versus remote-device discovery.

### Allow agents to use this device

The **Home** switch applies to the Windows desktop where this app is running, identified by its PC name:

- **Yes (default):** allow supported desktop and scoped coding operations over this authenticated RDP/Windows session without pairing or per-action broker dialogs.
- **No:** block new agent operations on this device, revoke desktop control and request cancellation of broker-owned coding work. RDP connections, other enabled computers and external user apps stay open. Cancellation does not undo completed effects.

The saved choice is per Windows user/device, at the endpoint identity's private
storage root in `desktop-access.json`, and survives logon/broker restarts. When
that root setting is absent, migration inspects legacy session settings: any
explicit No wins; malformed or unreadable data fails closed. The new root setting
is authoritative thereafter, and legacy files are left intact.
Other running logons for this Windows user reread the shared preference before
executing agent operations and reconcile active work within one second. An
unreadable or missing live preference blocks access. A failed-save No stays
effective in that broker rather than reverting to an older saved Yes.
Turning it back on does not restore old leases or restart cancelled
work. The tray block action and `Ctrl+Alt+Pause` use the same No behavior. Normal
RDP disconnect still revokes desktop leases without changing the saved switch.
The MCP host's own permissions and enterprise runtime requirements remain
separate; this switch does not disable them.

On the remote machine, start the broker inside the actual logged-in RDP session, not an administrative service or Session 0. Closing the window hides it to the notification area and leaves authorized work running. If its tray icon is unavailable, the window minimizes to the taskbar instead. Explorer/icon loss restores a reachable control surface. **Quit** deliberately stops the broker and confirms when work or control would be interrupted; Windows logoff/shutdown is not vetoed.

## Connect and use

1. Open the app on both computers and connect through Windows App. **Home** shows connected computers and agents; no Verify or Pair step is needed.
2. Keep the intended device's switch at **Yes**. Configure Copilot through **Setup**, or use a computer's MCP configuration in connection details.
3. The agent calls `rdp_list_connections`, selects the intended `connectionId`, and calls `rdp_bind_connection`. A configured fixed target skips this selection step, never silently choosing another computer.
4. Observe with `take_screenshot` or `get_screen_size`, then reserve mouse/keyboard control with `rdp_acquire_control` and use the input tools. Control acquisition has no approval dialog and does not preempt another agent.

After opening or activating an app, observe its controls before sending further
input. A visible window title does not prove that a newly launched app is ready.
Use `get_accessibility_tree` or `take_screenshot`, then observe the result after
input: a successful injection response is not an acknowledgement from the app.
Release control when the task is finished.

Restore the app from its tray icon or Start to view connections or block access.
Captures intersecting visible broker UI are deliberately rejected to protect its
local access/setup controls; minimize it before computer-use actions.

Turning off the controlled endpoint's device-access switch or pressing
Ctrl+Alt+Pause blocks access to **that device**. The same actor must obtain fresh
input ownership and observation after disconnect, lock or revocation.

To configure a remote CLI to use its own broker directly, use `ralink-mcp.exe --current-session`. This is a separate MCP process, not a recursive connection back to the local computer.

## Copilot prerequisites

The remote user must provide their own Copilot authentication. Managed SDK sessions and opt-in interactive sessions have different ownership. An ordinary terminal window is not attachable by default.

Open **Settings** to select an already installed physical Copilot runtime executable matching **1.0.79**, the pin for SDK **1.0.11**. Select the actual executable, not a reparse-point shim, and retain its adjacent runtime files. Setup does not download, start, or authenticate it. **Activity** retains task filtering, paging, details and explicit cancellation, including local cancellation while RDP is disconnected.

**Live interactive attachment is not implemented in the production adapter.** It returns `incompatible_runtime`; the fake-runtime contract and encrypted registration API are not evidence of working live attachment. The remaining gate is a verified policy-preserving exclusive-turn handoff and cancellation contract. See [Copilot integration](../src/AgentLink.Copilot/README.md) for exact schemas, permissions, runtime setup, and reconciliation.

Use only a compatible CLI/runtime with an explicitly supported authenticated server interface and human handoff. Never guess a hidden launch flag, scrape terminal output, register arbitrary network endpoints through MCP, or forward local GitHub tokens. If an installed runtime cannot provide the required interface or safe permission mediation, live attachment must fail explicitly rather than weaken those requirements.

## Disconnect, updates, and recovery

For connection, access or MCP failures, use the [diagnostic logs](diagnostics.md).
Settings shows the broker's actual private log directory and writer status.
Logging is bounded and payload-free; temporarily enable local
`RALINK_LOG_LEVEL=debug` for sampled operation timings, then restart the relevant
process. It does not enable native tracing or change agent permissions.

Desktop operations stop on disconnect or unavailable desktop. Non-UI jobs continue only under an explicitly scoped unattended task and finite policy/budget while device access remains enabled. Unsupported scope/managed-policy requests cannot be turned into silent approvals. There is no out-of-band status or cancellation channel to the disconnected local machine.

Reconnect to the same authenticated logon and retrieve the existing task ID/events. Do not resubmit a prompt simply because its acknowledgement was lost. An interrupted/unknown dispatch requires reconciliation, not an automatic retry.

For an unknown managed turn, stop the broker and independently establish that its prior runtime/turn has stopped. Run `ralink-broker.exe --reconcile-session <managed-session-id>` and confirm locally, then restart the broker and explicitly resume. This does not rewrite historical outcomes or resend prompts. The command refuses to share a live broker's writer.

Before an update, cancel or finish active jobs and explicitly **Quit** the broker; closing its window is not quitting. Do not rely on MSIX package replacement to preserve running processes. Windows session-host policy may log off disconnected users; the project does not change that policy.

For a local client update, also close its RDP connection windows and allow the plugin's COM process to exit normally. A broker can be stopped while a client still retains the native plugin. If installation reports `0x80073D02` (resources in use), release those clients and wait for normal shutdown before retrying; do not force package replacement over active work or log off the remote user.

Use the data-folder action in **Settings** to locate the actual writable directory. Packaged Windows apps may virtualize AppData; do not assume an unpackaged shell sees the same path. Back up required task outputs before uninstalling. Uninstall may remove package-owned writable data; it is not a guaranteed archival mechanism. User endpoint certificates are separate and should be removed only through an explicit user-managed rotation/cleanup procedure.

For identity rotation, stop every broker for that user, preserve needed task data, remove or archive the old `identity.json`, and restart to create a new endpoint identity. Select the resulting new connection ID explicitly; no fingerprint pairing is required. Old principal-owned tasks do not automatically transfer to a replacement identity. Do not delete unrelated certificates or user data.

## Required real-environment acceptance

### Current preview evidence

The earlier `0.3.0.4` package was installed on two Windows VMs using the original
signer. A supplied developer-preview RDCore client exercised real channel
discovery/reconnection; packaged WinUI, tray behavior, 480x400 layout, saved MCP
setup and execution-alias identity were exercised. That release used pairing,
per-action approvals and a changing MCP catalog. Its evidence does **not**
validate the 0.5.0.0 access model or redesigned dashboard.

The new build requires its own installed two-endpoint acceptance. Source changes
or a signed artifact are not evidence that already-running brokers/MCP clients
have been updated. Shipped-client support, live Copilot work and unattended
continuation remain separate gates below.

### Release gates

The following cannot be inferred from protocol fakes or a custom sample client:

| Scenario | Required evidence |
| --- | --- |
| Shipped Windows App discovery | Exact client version, packaged factory activation, and `RALink.v1` round trip without client patches |
| Clean-machine installation | One MSIX on each endpoint, proxy activation without prior COM registration, native dependencies present |
| Legacy loading | Windows 11 external visibility of the owned AddIns entry, compatible client's direct OOP COM activation, real channel/reconnect, Modern cleanup and no duplicate listener |
| Store capability | Explicit approval of the scoped `unvirtualizedResources` scenario and an accepted uninstall/identity migration policy |
| Simple access | Fresh default Yes, no pairing/dialogs on both endpoints, No blocks desktop/coding access and cancels owned work while RDP stays up, correct agent presence |
| Desktop correctness | Benign app, DPI/multi-monitor/negative-origin correspondence, layout invalidation, lock/UAC behavior, human Stop |
| Local client surface identity | Public service availability on the exact shipped version; multiple same-process connections, window recreation/reuse and independent identity corroboration. Current metadata deliberately reports ambiguous identity. |
| Background client input | Public acquisition of the exact existing connection's input destination; keys/chords/Unicode/mouse while another app has focus and while minimized, with no global input or local side effects |
| Fresh minimized capture | Controlled remote visual changes while actually minimized, current pixels and protected-UI exclusion; cached frames, successful HWND lookup or capture callbacks are insufficient |
| Managed Copilot | Authenticated remote prompt/result, permissions, questions, cancellation, and working-directory policy |
| Live interactive Copilot | Supported runtime/version, authenticated registration, explicit session handoff, busy rejection, process preserved on detach |
| Disconnect continuation | One preauthorized non-UI task, disconnect, reconnect, same task result without redispatch, no restored desktop lease |
| Maintenance | Disabled/enabled startup, dual roles, active-task shutdown, signed update, and documented data retention |

No supported Windows App/client version or completed live acceptance is claimed until these records exist.
