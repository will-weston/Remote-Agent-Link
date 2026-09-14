# RALink.v1 protocol

The native plugin and WTS DLL bridge raw bytes. They do not interpret MCP, JSON, credentials, or application messages. The managed brokers establish mutually authenticated TLS 1.2/1.3 over that duplex stream before accepting requests.

## Framing

Each application record consists of:

| Field | Encoding |
| --- | --- |
| Payload length | Four-byte little-endian positive integer |
| Kind | One byte: request `1`, response `2`, cancellation `3` |
| Payload | UTF-8 JSON envelope of exactly the declared length |

The JSON envelope has `kind`, a GUID `N`-format `id`, and the applicable `method`, `arguments`, `actorId`, `result`, or `error`. Unknown envelope properties, mismatched kinds, invalid IDs, malformed JSON, truncated data, and invalid lengths fail explicitly. Responses contain exactly one result or error. Cancellation identifies an existing request and has no operation arguments.

Requests are limited to 256 KiB; responses to 24 MiB, including JSON/base64 overhead. The desktop image limit is lower. A peer supports at most 16 concurrent operations. Late responses to known cancelled calls are distinguished from unsolicited or duplicate replies.

Native writes are split into transport chunks; WTS receive code validates and removes `CHANNEL_PDU_HEADER` and FIRST/LAST framing before exposing bytes. Neither a native read nor an RDP write is an application-record boundary.

Version 1 carries each image in one bounded response, rather than an independent artifact stream. Small control records cannot interleave inside that record. Backpressure, cancellation deadlines, and link failure bound this behavior; clients must not assume unlimited screenshot throughput.

## Connection lifecycle

After TLS key-possession validation, `protocol.hello` exchanges protocol version,
Windows session ID and logon SID with bounded device metadata. Admission uses the
authenticated RDP DVC/WTS session rather than a saved fingerprint pairing.
Endpoint keys and logon identity still identify the selected target. Peer display
names remain untrusted labels. Update both endpoints together when migrating
from a pairing-based release; no UI pairing fallback is part of the new workflow.

Handshake metadata remains bounded before full session readiness. Device access
does not skip framing, certificate-key validation, actor identity or logon
checks. `protocol.ping` supplies a periodic transport heartbeat. The client sends
a ping every five seconds with a ten-second deadline; the remote endpoint expires
a connection after twenty seconds without an incoming request.

A failed heartbeat or closed stream revokes connection-scoped desktop grants. A new connection gets a new epoch. No desktop mutations are replayed automatically. An unacknowledged mutation has an unknown execution outcome until the client observes fresh state.

Tool execution on an endpoint requires its device-access switch to be enabled.
The switch defaults to Yes; it does not introduce per-operation approval
messages. No blocks new desktop/coding operations, revokes input epochs and
requests cancellation of owned tasks without tearing down the transport.
Outgoing peers still reject reverse operation requests. Reconnection does not
restore input ownership or replay mutations.

`tools.list` supplies the endpoint's supported catalog over broker RPC. The MCP
host instead declares a fixed shared catalog of 32 tools from startup. It checks
endpoint support at binding or lazily before invocation, never replaces
declarations, and does not emit `notifications/tools/list_changed`. Unknown
peer-supplied tool names are neither declared nor forwarded. A known unsupported
tool returns `unsupported_on_endpoint`; declarations are not readiness promises.

## Local IPC

The broker exposes per-user, per-Windows-session named pipes. The server DACL admits the current user only, kernel pipe mode rejects remote network clients, and the broker verifies the client process's session, user SID, and logon SID. Native transport and local control use different pipes. The MCP actor is validated and stamped at the broker, not accepted as an authenticated identity by itself.

### Local plugin lifecycle stream

`RemoteAgentLink.Transport.<user SID>.<session ID>.Clients` is a **separate**
native-to-broker byte pipe, not the raw TLS/DVC transport and not an MCP tool.
It uses the same local/logon peer checks and first-instance ownership. There
are 16 lifecycle connection slots, independent of control/transport slots so
idle reporters cannot consume MCP's control budget.

Each fixed-size 48-byte record contains:

| Offset | Bytes | Value |
| --- | --- | --- |
| 0 | 8 | ASCII `RALCLT1` followed by NUL |
| 8 | 16 | Nonzero plugin instance GUID, Windows in-memory GUID byte order |
| 24 | 16 | Nonzero lifecycle epoch GUID, same byte order |
| 40 | 4 | Little-endian state: initialized `0`, connected `1`, disconnected `2` |
| 44 | 4 | Zero; other values are rejected |

Unknown signatures/states, zero identities and truncated records close only
that reporter. A stream cannot change instance ID or replace another active
reporter. Records are state snapshots, not an event history; rapid transitions
can coalesce. The native worker writes on state changes and every three seconds,
retries unavailable/broken pipes every second and bounds pending writes to two
seconds. Termination cancels writes and closes the stream. The broker removes
the entry on EOF/failure or after a 15-second incomplete/missing-record deadline.
Starting/restarting the broker republishes the current state without needing a
DVC reconnect. No mutation is sent or replayed on this stream.

`list`/`get` return a `ConnectionInfo` with `targetKind: "localRdpClient"`,
`connectionId: "rdp-client:<instance GUID in N format>"`, `state`, and
`localSurfaceStatus: "unsupported_public_contract"`. `peerFingerprint`,
`remoteSessionId` and `logonId` are null, not fabricated remote identity.
`describe` returns an empty supported endpoint catalog; all 32 MCP declarations
remain present and binding remains immutable. Direct `invoke` calls cannot
acquire/release the broker's own desktop or run remote operations on these
entries. Local access No is still enforced. Presence is local only.

Existing authenticated targets have `targetKind: "remoteEndpoint"` and
`state: "ready"` and retain their endpoint/logon IDs. Their transport and
operation routing are unchanged. Update local broker/MCP binaries together:
strict older `ConnectionInfo` readers may reject the additional metadata.

### Local surface metadata stream

The optional `RemoteAgentLink.Transport.<user SID>.<session ID>.Clients.Surfaces`
byte pipe is independent of both the unchanged 48-byte lifecycle stream and raw
TLS transport. Older brokers can ignore it without losing discovery. It has 16
separate slots and the same bidirectional user/session/logon checks. The broker
also requires the actual reporter PID and process-start time to match the
existing lifecycle pipe's owner; the RDP client PID inside the record is a
different, diagnostic identity.
Failure to start this optional listener is diagnosed without stopping lifecycle
or control IPC; retry it by restarting the broker, not by replacing another owner.

Records are exactly 128 bytes, with little-endian integers and IEEE-754 scale:

| Offset | Bytes | Value |
| --- | --- | --- |
| 0 | 8 | ASCII `RALSRF1` followed by NUL |
| 8, 24 | 16 each | Nonzero instance and epoch GUIDs, Windows in-memory byte order |
| 40 | 8 | Nonzero metadata generation |
| 48 | 8 | Candidate local HWND value |
| 56 | 8 | RDP client process creation FILETIME |
| 64, 68 | 4 each | RDP client PID, Windows session ID |
| 72 | 4 | Status enum below |
| 76 | 4 | Signed HRESULT from the diagnostic query |
| 80, 84 | 4 each | Signed remote width, height |
| 88, 92 | 4 each | Signed view width, height |
| 96, 100 | 4 each | Signed view offsets X, Y |
| 104 | 4 | Float scale |
| 108 | 4 | `0` no validated metadata; `1` process-ownership checks passed |
| 112 | 16 | Reserved, all zero |

Status values: `0 pending`, `1 service_unavailable`, `2 service_error`,
`3 unsupported_session`, `4 invalid_geometry`, `5 identity_ambiguous`.
No status means surface-ready. Flag `1` is permitted only with status `5`,
nonnegative HRESULT, nonzero HWND/PID/process-start, dimensions 1–65536,
offsets 0–65536 and finite scale greater than zero and at most 32. All HWND,
process and geometry fields must be zero when flag `0` is set. Geometry is raw
service metadata, not a screenshot size, native-framebuffer guarantee or approved
coordinate conversion. Process ownership does not disambiguate connections.

Reports poll every second (COM calls can delay them); pipe writes have a
two-second bound. The broker expires a missing/partial report after 15 seconds.
Unknown values, reserved bytes, truncated records, same-epoch generation
regression, same-generation content changes, retargeting or duplicate ownership
close only the optional stream. Unknown lifecycle IDs and noncurrent epochs are
ignored. Disconnect/reconnect clears prior metadata immediately. Stream EOF,
failure or expiry clears only its metadata; lifecycle expiry removes the target.
Restart reconnects and republishes current metadata without a DVC handshake.

`list`/`get` expose optional `localSurface` plus its `localSurfaceStatus`. The HWND
is a hexadecimal string and process start is FILETIME, not broker process identity.
Absent metadata retains `unsupported_public_contract`. The three capability flags
`connectionIdentityVerified`, `backgroundInputSupported`, and
`freshMinimizedCaptureSupported` are always false. `describe` stays empty, binding
does not change, and no request/control protocol or MCP declaration is added.
The existing `rdp_get_connection` returns these read-only diagnostics without
executing surface work. `backgroundInputUnsupportedReason` explains the unproven
public path to the existing connection's input object;
`freshMinimizedCaptureUnsupportedReason` explains that HWND capture cannot
guarantee current pixels when the client suppresses minimized output. These
broker-generated explanations are not native wire fields or permission grants.

### Control RPC

Authenticated current-session metadata remains available without an incoming
RDP channel. Agent data/actions still require enabled device access, and desktop
operations require an available interactive desktop. `ui.show` is local control
IPC only, never a channel or MCP operation for changing access.

`mcp.presence` is internal authenticated control IPC, not a model-callable tool.
Its request contains `actorId`, `connectionId`, `currentSession`, and `connected`.
An initialized MCP process sends a heartbeat every 15 seconds and `connected=false`
on clean shutdown. The broker stamps actor identity just as for invocation,
tracks the local client, and forwards status only to its explicitly selected
remote endpoint. Missing heartbeats expire after 45 seconds; channel loss clears
remote presence. Presence does not grant control, enable a device or execute
work. Failed status reporting is diagnosed on stderr without blocking tool
listing or switching transports.

The MCP executable accepts an explicit `--broker-pipe name` override for local integration harnesses. It accepts only a simple local name and retains current-user server verification; it cannot target a network pipe. Automated MCP checks use random, first-instance pipe names and never impersonate a running production broker's pipe.

`--connection <connectionId>` configures an immutable remote target at MCP
startup. Endpoint support is checked on invocation without requiring an initial
bind call. All declarations remain visible during offline/pipe failures, access
changes and MCP process replacement. Errors are returned explicitly rather than
hidden as an empty catalog. Binding the same ID refreshes support; a different ID
is rejected. The flag is mutually exclusive with `--current-session` and never
enables a blocked device. A cached-tool error from the MCP host is not evidence of
transport loss and must not trigger a fallback to the agent host's own desktop.
