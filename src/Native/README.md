# Native RDP transport

Windows x64, C++20, public Windows SDK/WRL only. No downloads or installation are
needed beyond the build prerequisites. The project-file defaults are **v145**
and SDK **10.0.26100.0**, with a statically linked C/C++ runtime.
The repository's `scripts\build.ps1` discovers and overrides the installed MSVC
toolset/Windows SDK for command-line and CI builds.

## Build and test

Run from the repository root in PowerShell. Use the script so the installed
toolset/SDK is selected rather than assuming a particular Visual Studio edition
or installation path:

```powershell
.\scripts\build.ps1 -NativeOnly
.\artifacts\native\Release\Native.Tests.exe
```

Building the test project builds all three production projects via project
references. Pass `-Configuration Debug` for a debug build. Individual projects
can also be built with MSBuild after setting up the VC environment in the same
`cmd.exe` process; apply the installed toolset and SDK overrides explicitly.
Use `/t:Rebuild` to validate clean MIDL generation.
To preserve existing outputs, pass an absolute, trailing-backslash
`/p:NativeArtifactsRoot=C:\src\Remote-Agent-Link\artifacts\validation\native-check\`
to MSBuild; native outputs, intermediates and generated MIDL files all use that
root. The default remains `artifacts\`.

Outputs under `artifacts\native\<Configuration>\`:

* `AgentLink.RdpPlugin.exe` — out-of-process COM factory and DVC listener.
* `AgentLink.Transport.dll` — remote WTS stream adapter.
* `AgentLink.ProxyStub.dll` — ten generated COM interface proxies/stubs.
* `Native.Tests.exe` — native validation executable, not a shipping component.
* Matching `.pdb` files and DLL import `.lib`/`.exp` files.

Each project's intermediate directory is
`artifacts\obj\native\<project>\<Configuration>\`. MIDL-generated source and
headers belong to `AgentLink.ProxyStub`'s intermediate directory. Do not check
them in. Include `AgentLink.ProxyStub\THIRD-PARTY-NOTICES.txt` with redistributed
proxy/stub binaries.

## Bounded native diagnostics (opt-in ETW)

The plugin EXE and WTS DLL register the self-describing TraceLogging provider
**`RemoteAgentLink.Native`**, GUID
**`945B8A72-5A20-4B8B-B3A3-B77D82860D15`**. There was no earlier native ETW
provider; this replaces the plugin's unstructured debugger messages. Registration
is best-effort on process entry/activation or `RAL_Open`, not in `DllMain` or
data/window callbacks. There is no native file writer, logging worker, queue,
dependency, or native-to-broker diagnostic protocol.

Without an enabled ETW session, event calls return before reading the clock or
updating rate counters. Enabled logging uses fixed storage and admits at most
**eight records per event kind per 60-second window, per loaded native module**.
There are 16 kinds (at most 128 admissions across their windows); these are
fixed windows starting at the first admitted event, not rolling windows, so a
boundary can admit two bursts close together. Idle time does not bank bursts.
The gate makes one atomic try-lock attempt and drops on contention rather than
waiting. The next admitted record of that kind includes the number suppressed
by rate/lock contention since the previous admission. There is no flush timer:
final suppression totals can disappear at exit. ETW can also lose events;
`Suppressed` does not measure ETW delivery or collector loss.

All records use event name **`NativeDiagnostic`**, level **4 (Information)**,
keyword **`0x1`**, including failures. Fixed fields are `Kind` (UInt32),
`Result` (HRESULT; Win32 failures use `HRESULT_FROM_WIN32`), `State` (UInt32),
`Correlation` (GUID), and `Suppressed` (UInt64). No free-form strings, payloads,
keys, screenshots, prompts, paths, peer labels, window handles/geometry, tokens
or user/logon identities enter these fields. ETW's normal event header still
contains timestamps and process/thread IDs. Correlation uses the existing random
plugin-instance GUID for lifecycle/service events and a separate random GUID
per DVC bridge or WTS context; it is zero when unavailable/not applicable.
It does not assert a cross-machine, DVC-to-plugin or broker-peer association.

| Kind | Meaning | State |
| --- | --- | --- |
| 0 `ServerStart` | COM initialization result | 0 |
| 1 `ServerStop` | COM server final result | 0 |
| 2 `Activation` | Class activation result and captured loader selection | 0 Modern/Modern, 1 Modern/Legacy, 2 Legacy/Modern, 3 Legacy/Legacy (requested/selected); 4 settings/activation failure without a usable selection |
| 3 `PluginInitialize` | Initialization result | 0 normal, 1 discovery-only after listener failure |
| 4 `PluginConnected` | Transition to connected, or epoch creation failure | 0 |
| 5 `PluginDisconnected` | Transition to disconnected | 0; no client-supplied reason |
| 6 `PluginTerminated` | First reporter stop | 0 |
| 7 `ListenerUnavailable` | DVC listener creation failed; discovery retained | 0 |
| 8 `BridgeOpen` | DVC worker connected to verified local broker pipe | 0 |
| 9 `BridgeClose` | DVC worker cleanup completed, including failed opens | 0 local/worker shutdown, 1 remote close observed |
| 10 `BridgeFailure` | First significant bridge failure; retries collapse to final timeout | 0 |
| 11 `ReporterFailure` | Worker, broker pipe open/verification or write failure | 0 lifecycle worker, 1 surface worker, 2 lifecycle pipe, 3 surface pipe; failed writes use bounded `ERROR_WRITE_FAULT` category |
| 12 `SurfaceStatus` | First probe result or changed status/HRESULT/epoch only | 0 Pending, 1 ServiceUnavailable, 2 ServiceError, 3 UnsupportedSession, 4 InvalidGeometry, 5 IdentityAmbiguous |
| 13 `WtsOpen` | WTS open result | 0 |
| 14 `WtsClose` | Previously opened WTS context closed | 0 |
| 15 `WtsFailure` | First sticky WTS stream fault | 0 |

Successful byte transfers, read polls, heartbeats, window notifications and
geometry/frame changes alone do not log. A service candidate never implies
capture/input support. Diagnostic calls are `noexcept`, preserve `GetLastError`,
and cannot change transport errors, access checks or cancellation behavior.
These are sampled troubleshooting events, not an audit trail; absence of an
event does not establish success. Managed rotating files remain separate.

### Local collection

The following syntax is verified against the local Windows `logman create
trace /?` and `logman stop /?` help. Run only when intentionally collecting,
from the repository root in a PowerShell session with ETW-controller rights:

```powershell
New-Item -ItemType Directory -Force .\artifacts | Out-Null
logman create trace RemoteAgentLink-Native -p "{945B8A72-5A20-4B8B-B3A3-B77D82860D15}" 0x1 4 -o "$PWD\artifacts\native-diagnostics.etl" -f bincirc -max 32 -bs 64 -nb 2 8 -ets
# Reproduce the issue, then stop only this trace session:
logman stop RemoteAgentLink-Native -ets
```

This requests a circular 32-MB ETL with 64-KB buffers (2 minimum, 8 maximum;
Windows may adjust buffer counts). Choose an unused trace name and do not
replace/stop another user's session. No installation, manifest registration or
trust change is needed. The collector, not callbacks, writes the ETL. Inspect it
with an ETW viewer supporting TraceLogging, such as Windows Performance Analyzer.
No collection session or installed-client acceptance was run to validate this
change. Treat collected ETLs as private diagnostics; review before sharing.

`Native.Tests.exe --startup-only` includes deterministic fake-clock burst,
retry, concurrency/drop-accounting and Win32-error-preservation checks, plus
the existing real COM-startup probe. `--activation-only` and
`--client-lifecycle-only` exercise instrumented lifecycles using test-owned
identities/pipes. These safe subsets do not prove live ETW delivery or the full
bridge suite; use the full native runner only in an idle broker-free session.

## Packaged COM identities

The application factory CLSID is
**`583E23E9-3A9F-461C-A3CA-6F99F2625B78`**, defined in
`Common\PluginActivation.h` as `ral::PluginFactoryClsid`.
Register this class on the packaged `AgentLink.RdpPlugin.exe` COM server and use
this same value for the RDP extension's `ComServer`/factory CLSID. The factory
implements `GetPluginCount` followed by `CreatePlugin` in the public sample's
vtable order. The modern loader also calls `GetPluginCount`: it returns one in
Modern mode, not an unconditional zero. In Legacy mode it returns zero and
rejects creation. Each enabled `CreatePlugin(0)` returns an independent plugin,
not a singleton; other indices are rejected with a null output.

The separate legacy class CLSID is
**`77A190C4-C7EB-4DB2-A317-9CFADA8F2A53`**, defined in the same header as
`ral::LegacyPluginClsid`. Register it on the **same packaged EXE COM server**,
not a DLL surrogate or a fourth application. It exposes `IWTSPluginFactory`
with count one in Legacy and zero in Modern. It also accepts direct
`IWTSPlugin` activation for compatible classic COM loaders. Direct activation
returns a fresh actual out-of-process plugin in Legacy and fails with null in
Modern. An explicit class-factory request for the disabled `IWTSPlugin` returns
`HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY)`; COM's `IUnknown`-first
activation can instead surface `E_NOINTERFACE` when it queries the disabled
object afterward.
Both classes use the same `Plugin`, listener, security and transport code.
An enabled legacy activation exposes both `IWTSPluginFactory` and `IWTSPlugin`
on one COM identity, regardless of the initially requested interface. This is
required for out-of-process activation, where COM can request `IUnknown` before
querying the caller's requested interface. Factory-created plugins remain
independent of that activation object's direct plugin.

The app extension also declares `InitializationDvcName` as `RALink.v1`.
On-demand discovery uses that metadata to associate a remote channel-open request
with the factory before the plugin has created its listener. `Public\channel.json`
alone is not an app-extension initialization-channel declaration.

The **new proxy/stub class CLSID** is
**`586B220F-6970-4E64-B5DD-438A298A876A`**, defined in
`AgentLink.ProxyStub\IWTSPlugin.idl` and `ProxyIdentity.h`.
Register that class against `AgentLink.ProxyStub.dll` in the package's
`ComInterface` declarations. Map all ten interfaces below to that proxy class.
`NumMethods` includes the three inherited `IUnknown` methods.
These method counts document the ABI; `NumMethods` is not an accepted attribute
of the MSIX `com:Interface` element.

| Interface | Public IID | NumMethods |
| --- | --- | ---: |
| IWTSPluginFactory | C0C62619-3BC1-4095-9B9A-84503E37DAA5 | 5 |
| IWTSPlugin | A1230201-1439-4E62-A414-190D0AC3D40E | 7 |
| IWTSListener | A1230206-9A39-4D58-8674-CDB4DFF4E73B | 4 |
| IWTSListenerCallback | A1230203-D6A7-11D8-B9FD-000BDBD1F198 | 4 |
| IWTSVirtualChannelCallback | A1230204-D6A7-11D8-B9FD-000BDBD1F198 | 5 |
| IWTSVirtualChannelManager | A1230205-D6A7-11D8-B9FD-000BDBD1F198 | 4 |
| IWTSVirtualChannel | A1230207-D6A7-11D8-B9FD-000BDBD1F198 | 5 |
| IWTSPluginServiceProvider | D3E07363-087C-476C-86A7-DBB15F46DDB4 | 4 |
| IWTSWindowChangedCallback | E8A47FD3-1AF6-4D7F-8E15-515CCAE1C00C | 4 |
| IWTSWindowInfoService | 2A002C7B-0120-4D04-BFB1-3C73110C8581 | 8 |

There are no separately activatable listener/callback classes. No sample
application CLSIDs, rendering or parent-service implementations, typelib, self-registration
exports, or production native registry writes are included.

## Loading preference and legacy COM activation

The packaged COM EXE reads the per-Windows-user
`HKCU\Software\RemoteAgentLink\PluginSettings` value `LoadingMode` using
`KEY_WOW64_64KEY`, preserving the package's registry virtualization view shared
with the broker. Only a missing key or value defaults to Modern. The value must
be `REG_DWORD`, exactly four bytes, with `0` meaning Modern or `1` Legacy.
Wrong types, sizes, values, access denial and other I/O errors explicitly fail
new activation rather than falling back. The native code never changes it.

Selection is captured for each new `IClassFactory::CreateInstance`, not at
server startup or each `GetPluginCount`/`CreatePlugin`. Existing factories and
plugins never change mode. Restart RDP clients after changing the preference;
this is activation selection, not authorization or live connection revocation.
`CreatePluginFactory` has explicit-mode overloads and the class factory accepts
an injectable settings reader for deterministic tests.

Classic registry integration uses the application's owned AddIns leaf and a
`Name` value containing the **legacy CLSID**, not a versioned WindowsApps DLL
path. Compatible legacy clients request `IID_IWTSPlugin` on that packaged
out-of-process class. Unlike a factory, `IWTSPlugin` has no count method:
disabled direct activation must fail explicitly with a null output, never
`S_OK` with a null object. The separate legacy factory interface remains
available for count-based activation and deterministic coverage.

There is no legacy LoadLibrary shim or in-process plugin DLL deployment.
The RDP client does not host this application's transport, broker, settings
reader, or COM-server shutdown bookkeeping; both activation paths use the
same packaged EXE and generated interface marshalling.

Automatic legacy setup is a broker/package responsibility gated to Windows 11.
Its registry-write-virtualization exclusion is limited to
`HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\RemoteAgentLink`;
the preference and other registry/file virtualization remain enabled.
`unvirtualizedResources` adds a separate Store-approval requirement, and the
excluded registry leaf needs ownership-aware cleanup before uninstall.
Modern loading retains its existing Windows 10 build 19041 minimum.

## Local plugin lifecycle discovery

Each initialized plugin reports an independent instance GUID and lifecycle epoch
to the local broker on the separate
`RemoteAgentLink.Transport.<user SID>.<session ID>.Clients` pipe. This begins
without `OnNewChannelConnection`, TLS or a remote broker. `Connected` changes
the epoch when transitioning into connected state; `Disconnected` retains the
instance, and termination cancels the reporter without joining in a COM callback.
An absent/restarted broker is retried independently of the DVC worker.
Both modern factory-created and direct legacy plugins use this reporter.
Failure to create the optional DVC listener is diagnosed but no longer fails
plugin initialization: discovery remains active and channel acceptance stays
disabled for that plugin. A new plugin activation is needed to retry listener
creation; no remote channel or operation readiness is asserted.

The worker validates the pipe server's user/session/logon LUID and byte mode,
retains a COM server lease, and bounds/cancels/drains overlapped writes.
Fixed 48-byte records, heartbeat and expiry rules are specified in
[the protocol](../../docs/protocol.md#local-plugin-lifecycle-stream).
Reporting carries no HWND, input request, remote identity, authorization or
operation payload. Existing raw TLS bridge bytes are unchanged.

The broker exposes these as `localRdpClient` MCP targets even without a remote
endpoint. They are discovery-only and return no supported endpoint tools:
**local RDP-surface capture/input is not implemented**. An authenticated remote
broker is a distinct `remoteEndpoint` target, never an automatic promotion of
the client instance. This avoids falsely associating several windows or plugin
objects in one RDP client process with one authenticated remote logon.
The broker presentation's peer list still describes DVC peers, not these local
MCP discovery entries.

`Native.Tests.exe --client-lifecycle-only` exercises the real reporter on a
random test-owned pipe: initialization without a DVC, state changes, reconnect
epochs, broker restart and worker cleanup. Activation fixtures explicitly
inject a resolver returning `S_FALSE` to disable reporting, so the existing
safe activation subset never contacts a production pipe. The lifecycle subset
does not operate a desktop or prove installed RDP compatibility.

## Local RDP-surface control contract gap

### Implemented: diagnostic capability probe

Each connected instance queries the public service provider on the **actual
manager passed to its Initialize**, using the MIT sample's
`RDCLIENT_WINDOW_INFO_SERVICE` identity. A separate MTA worker obtains session
type, client PID and `GetWindowInfo(nullptr)` for full-desktop sessions only.
This NULL lookup is a capability experiment, not a guarantee across client
versions. RemoteApp is explicitly unsupported. Services can be absent, pending,
broken or return unusable geometry; none of those failures removes lifecycle
discovery or changes Modern/Legacy selection.

Public IDL/vtable/marshaling definitions come only from the attributed Microsoft
sample. The production COM EXE and inbound callbacks are MTA; a non-MTA
initialization caller does not start this optional probe. Calls run off lifecycle
callbacks with COM cancellation after three seconds, including unsubscribe.
Window notifications only signal a re-query, never publish callback-supplied
identity. One subscription is attempted per retained service; failed subscription
falls back to one-second polling, with an unsubscribe attempt even after an
ambiguous failure. Late callbacks retain only weak state. Workers retain the
server lease until cleanup; lifecycle reporting uses another worker.

The separate versioned `.Clients.Surfaces` stream carries diagnostic metadata,
not operations. See [its exact layout](../../docs/protocol.md#local-surface-metadata-stream).
The broker associates it with an existing same-process/start-time lifecycle
reporter, plugin GUID and current connected epoch. Missing/stale surface data
never removes lifecycle entries. Geometry generations change on metadata,
notification and epoch changes; results crossing a notification/lifecycle
boundary are discarded.

A nonzero service HWND is checked against its reported PID, process creation
time, user, Windows session and logon LUID. Positive bounded dimensions and
finite positive scale are required. **Even a successful ownership check remains
`identity_ambiguous`**: same-process windows, fallback containers and HWND reuse
cannot be ruled out by these checks. The raw remote/view dimensions, offsets and
scale are not an approved crop/coordinate transform. No marker window is created,
no parent service is invoked, and no title/process enumeration is used.

`connectionIdentityVerified`, `backgroundInputSupported` and
`freshMinimizedCaptureSupported` are always false. This phase does **not** enable
capture, input, window messages, local desktop operations or additional MCP tools.
Connection identity corroboration, public connection-targeted input and fresh
minimized pixels remain independent operational gates.

`--activation-only` includes test-owned, real cross-process COM manager/service
and callback marshaling, a canceled hung query, late callbacks, geometry
failures, unavailable services, reconnect epochs and surface-pipe restart.
It registers no production COM classes or registry entries, opens no production
broker pipe, and creates/captures/operates no real windows. Pure geometry/ABI and
synthetic broker tests are not installed-client identity or minimized-capture
evidence.

Public contracts investigated on 2026-09-14:

| Public contract | What it provides and what remains missing |
| --- | --- |
| [`IWTSPluginServiceProvider`](https://learn.microsoft.com/windows/win32/api/tsvirtualchannels/nn-tsvirtualchannels-iwtspluginserviceprovider) | Queryable from the channel manager supplied to `Initialize`; `GetService` obtains client services. It is not itself a screen or input interface. |
| [`IWTSWindowInfoService`](https://learn.microsoft.com/windows-365/link/iwts-window-info-service) | Client PID, session type, HWND, dimensions, view offsets, scale and change subscriptions. The probe tries a NULL remote HWND for a full desktop, treating all results as diagnostic candidates until exact connection identity and viewport semantics are independently established. Availability is client-dependent, not inferred from loader support or a nonzero HWND. |
| [`IWTSWindowParentService` in Microsoft's public sample IDL](https://github.com/microsoft/SampleOutOfProcRdpPlugin/blob/edfaf5ef24c948de784276f5bf8b88dd59c50e50/IWTSPlugin.idl) | `SetParent(remoteHwnd, containerWindowType, childWindow)` attaches plugin-owned visuals to a client container. It does not expose a pixel-readback or input method. The sample is evidence of available interface shape, not a guarantee that a shipped client exposes a capture-only desktop viewport or protected-controls boundary through it. |
| [`IWTSBitmapRenderer`](https://learn.microsoft.com/windows/win32/api/tsvirtualchannels/nn-tsvirtualchannels-iwtsbitmaprenderer) | `Render` accepts plugin-supplied bitmaps for mapped visual output; statistics and removal do not read the remote desktop framebuffer. This is rendering **into**, not capture **from**, an RDP surface. |
| [`IGraphicsCaptureItemInterop::CreateForWindow`](https://learn.microsoft.com/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow) | Windows can capture a particular known HWND. This does not establish which HWND/viewport is this plugin's connection, or exclude client chrome, credentials and protected broker controls. Capturing an arbitrary RDP-process window is not an acceptable association. |
| [`IMsRdpClientNonScriptable::SendKeys`](https://learn.microsoft.com/windows/win32/termserv/imsrdpclientnonscriptable-sendkeys) | Sends atomic scancode sequences to an owned RDP ActiveX control. The plugin contracts investigated do not document obtaining the particular Windows App connection's ActiveX control through `GetService`. Owning a new control is a different client architecture, not attaching this plugin to an existing connection. |
| [`SendInput`](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput) | Injects into the system keyboard/mouse input stream and accepts no destination HWND/connection. A foreground-window check followed by injection does not atomically constrain delivery. It cannot satisfy the no-unrestricted-local-desktop boundary. |

The [public out-of-process sample](https://github.com/microsoft/SampleOutOfProcRdpPlugin/tree/edfaf5ef24c948de784276f5bf8b88dd59c50e50)
queries window services during `Initialize`. Its `Client\IWTSWindowInfoServiceImpl.cpp`
leaves the window-info/PID/session-type methods `E_NOTIMPL`, so running that toy
client would not validate real per-connection mapping. No private implementation,
window-title search, process-wide window guessing, or invented service IID was used.

The unresolved requirement is a verified, service-bound **capture and input
destination**, with explicit viewport/scale/lifetime behavior and a way to
exclude protected UI. The public metadata probe narrows the investigation; its
window metadata alone is not sufficient evidence.
Connection-scoped input in particular is not supplied by the contracts above;
posting arbitrary window messages is not a documented RDP plugin input API.
Until this is established and integrated with observation epochs, local access
revocation, exclusive input and cancellation, client-only operations remain
`unsupported_on_endpoint`. This is a **partial implementation (discovery)**,
not completion of the requested local-surface control behavior.

Live acceptance must separately establish service availability on each supported
client, multiple same-process connections, resize/scale/minimize/reconnect
behavior, focus races and protected-window exclusion. No installed RDP,
screen capture or input acceptance was performed by the deterministic tests.

## Local client-side bridge

The listener name is exactly **`RALink.v1`**. Each accepted channel asynchronously
opens its own client connection to:

```text
\\.\pipe\RemoteAgentLink.Transport.<current process user SID>.<Windows process session ID>
```

The broker must already be running through explicit UI setup. The plugin never
starts it, listens for network connections, or executes agent jobs.

The pipe is a **raw, duplex byte stream**: there is no greeting, handshake,
length prefix, channel PDU header, or application framing added by native code.
The managed brokers layer TLS over it. Connection retries stop after five
seconds; failure explicitly closes the DVC. The pipe must be byte-mode and its
server must have the same user SID, Windows session, and authentication/logon
LUID. The pipe is opened with identification-only impersonation rights. The
broker still must enforce its own ACLs, local-client restrictions, first-instance
protection, and peer checks.

`OnDataReceived` copies ciphertext to a bounded queue and returns. The queue cap
is **32 MiB including the chunk currently being written**, with a **16 MiB**
per-callback limit. Oversized data/queue overflow closes the channel instead of
dropping bytes. A background pipe writer drains the queue. A separate reader
makes serialized `IWTSVirtualChannel::Write` calls of at most **16 KiB**.

`OnClose`, `Disconnected`, `Terminated`, and callback destruction signal
cancellation without joining worker threads in the callbacks. Overlapped pipe
operations are canceled and drained before their buffers or handles are freed.
Outgoing COM calls are cancellation-enabled and watched on a threadpool timer:
disconnect cancels writes, and an unresponsive write/close is canceled after ten
seconds. COM proxies stay within the MTA. Workers retain their state and server
leases; remote channel references are released on worker exit, breaking the
callback/channel ownership cycle.

The server uses COM process reference counting, suspended registration of both
classes before a single resume, fast rundown, and class-object revocation
before COM teardown. It exits after
the last object/worker/server lock is released, or after an unused activation's
60-second idle grace period. Its lifetime has no connection to durable agent
jobs, which belong to the separate managed broker.

`AgentLink.RdpPlugin.exe --check-startup` exercises the executable's COM setup
without registering classes or opening channels. Fast rundown is set before
`CoInitializeSecurity`; reversing that order fails startup with `RPC_E_TOO_LATE`.
The native suite and packaging script run this probe. It does not replace a
packaged cross-process activation check.
`Native.Tests.exe --startup-only` isolates this regression while a real broker is
running; `--activation-only` isolates loading gates, settings, and both COM classes
without registering either class or opening the broker pipe. The full native
bridge suite needs the current user's broker pipe free.

## Remote managed adapter ABI

Declarations are in `AgentLink.Transport\AgentLink.Transport.h`. Exports have
exact, undecorated names on x64 and use `__stdcall`:

```cpp
void* RAL_Open(const wchar_t* channelName, uint32_t* error);
uint32_t RAL_Read(void* context, uint8_t* buffer, uint32_t capacity,
                  uint32_t timeoutMs, uint32_t* bytesRead);
uint32_t RAL_Write(void* context, const uint8_t* buffer, uint32_t length,
                   uint32_t* bytesWritten);
void RAL_Close(void* context);
```

* Errors are Win32 codes returned directly, not HRESULTs or a `GetLastError`
  contract. No C++ exception crosses the ABI. Output counts are initialized to
  zero. `RAL_Close(nullptr)` is harmless; non-null contexts must come from
  `RAL_Open` and be closed exactly once.
* `RAL_Open` accepts only `L"RALink.v1"`, opens
  `WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, ..., WTS_CHANNEL_OPTION_DYNAMIC)`,
  and rejects Session 0.
* The adapter uses the documented **borrowed `WTSVirtualFileHandle`** for
  overlapped I/O, not concurrently invoked non-thread-safe WTS read/write
  functions. Only `WTSVirtualChannelClose` closes that borrowed handle.
* Reads and writes may run concurrently; each direction is serialized.
  `timeoutMs` is capped at **1,000 ms**, including `INFINITE`. A zero timeout
  polls. Success with zero bytes means no complete record is currently
  available, **not EOF**. Pending kernel reads persist between polls; timeout
  does not discard or duplicate bytes.
* Each complete WTS read must contain its `CHANNEL_PDU_HEADER`. The adapter
  checks FIRST/MIDDLE/LAST ordering, consistent total lengths, exact final
  length, progress, and a **16 MiB** limit before exposing application bytes.
  Unknown flags, short headers, oversized reads, `ERROR_MORE_DATA`, and
  incomplete records at EOF fault the stream. Records are fully validated
  before delivery; leftover bytes survive capacity-limited consumer reads.
* Each write is split into at most **16 KiB**. Short writes advance only by
  confirmed bytes. Failure returns its Win32 code and the confirmed prefix in
  `bytesWritten`; it does not report success or replay an ambiguous write.
  Each pending write has a ten-second bound. I/O/protocol failures are sticky;
  close and reopen the transport rather than attempting TLS resynchronization.
* **Shutdown order:** cancel the managed polling loop, await outstanding reads
  **and writes**, then call `RAL_Close`. Close is not a concurrent cancellation
  API. It cancels/drains any kernel read retained after the final timed poll.
* TLS, session identity, device access, durable task management, and application
  record framing are intentionally managed-layer responsibilities.

## Validation boundaries

The native test executable covers parser limits/malformed/truncated fragments,
multi-chunk records through 16 MiB, consumer slicing, DLL ABI guards, construction
of all seven generated COM proxies, independent plugin instances, raw duplex
named-pipe transfer, 16 KiB channel writes, missing-broker timeout, queue limits,
disconnect/OnClose cancellation, and reference-count cleanup. It loads the proxy
DLL directly without registration.

The safe `--activation-only` selector covers both mode gates, independent
plugin objects, per-activation snapshots, direct legacy activation, class
locks, missing/malformed/inaccessible settings under unique volatile
test-owned HKCU keys, both CLSID-to-class-factory construction paths with an
injected mode reader, invalid interfaces/indices, failures and output guards.
It does not activate the installed package or register production CLSIDs.
The legacy checks also exercise `IUnknown`-first activation, symmetric interface
queries, controlling-unknown identity, independent direct/factory plugin state,
and a static interface set across preference changes.
Cross-process checks start a test-owned child with random activation CLSIDs and
fixed injected mode snapshots, exercising real `CoCreateInstance` and the
generated proxies for enabled/disabled modern and legacy classes. Proxy mappings
are process-local (`CoRegisterPSClsid`), not registry writes or package
registration. The child never opens a channel or the broker pipe and has a
bounded lifetime; this is not evidence of installed Windows App compatibility.
Settings tests restore their test key ACL and remove only their owned keys.
Existing bridge fixtures use explicit Modern mode, not real user state.

The raw bridge test briefly owns the exact current-user/session transport pipe
using `FILE_FLAG_FIRST_PIPE_INSTANCE`. Run it in an idle development session
without a live broker or RDP plugin. It fails rather than taking over an existing
broker pipe; that refusal is not a passed bridge test. Do not interrupt live
work to bypass it. `--activation-only`, `--startup-only` and `--manifest-only`
remain available for nonintrusive subsets. Default execution includes activation
tests and the full guarded bridge suite.

Tests do **not** prove Windows App discovery, package COM activation/marshalling
across processes, actual RDP/WTS delivery, TLS authentication, or reconnect with
live Windows 365. Those require an approved packaged, two-machine integration
run. Nothing here installs a package/certificate, changes production registry
entries, or launches an RDP session.

The suite also reads `packaging\AgentLink.Msix\Package.appxmanifest` using the
Windows AppX schema reader and checks its three application roles. This validates
the manifest structure, not installation, executable payloads, or client discovery.

Public references:

* [Microsoft public out-of-process sample](https://github.com/microsoft/SampleOutOfProcRdpPlugin)
* [WTS read chunk/header contract](https://learn.microsoft.com/windows/win32/api/wtsapi32/nf-wtsapi32-wtsvirtualchannelread)
* [Borrowed WTS handle and overlapped I/O](https://learn.microsoft.com/windows/win32/api/wtsapi32/nf-wtsapi32-wtsvirtualchannelquery)
