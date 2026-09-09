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
* `AgentLink.ProxyStub.dll` — seven generated COM interface proxies/stubs.
* `Native.Tests.exe` — native validation executable, not a shipping component.
* Matching `.pdb` files and DLL import `.lib`/`.exp` files.

Each project's intermediate directory is
`artifacts\obj\native\<project>\<Configuration>\`. MIDL-generated source and
headers belong to `AgentLink.ProxyStub`'s intermediate directory. Do not check
them in. Include `AgentLink.ProxyStub\THIRD-PARTY-NOTICES.txt` with redistributed
proxy/stub binaries.

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
returns a fresh actual out-of-process plugin in Legacy; in Modern it returns
`HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY)` and null.
Both classes use the same `Plugin`, listener, security and transport code.

The app extension also declares `InitializationDvcName` as `RALink.v1`.
On-demand discovery uses that metadata to associate a remote channel-open request
with the factory before the plugin has created its listener. `Public\channel.json`
alone is not an app-extension initialization-channel declaration.

The **new proxy/stub class CLSID** is
**`586B220F-6970-4E64-B5DD-438A298A876A`**, defined in
`AgentLink.ProxyStub\IWTSPlugin.idl` and `ProxyIdentity.h`.
Register that class against `AgentLink.ProxyStub.dll` in the package's
`ComInterface` declarations. Map all seven interfaces below to that proxy class.
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

There are no separately activatable listener/callback classes. No sample
application CLSIDs, rendering/window services, typelib, self-registration
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
* TLS, identity pairing, permissions, durable task management, and application
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
