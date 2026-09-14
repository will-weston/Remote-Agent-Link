# Diagnostics

Remote Agent Link keeps local, bounded diagnostic logs. They are troubleshooting
records, not a complete audit trail or proof that an input reached an application.
There is no logging service, network upload or new package dependency.

## Find the logs

On each broker, open **Settings > Local storage and endpoint details > Open
diagnostic logs**. The read-only path, active level, writer status and loss counters are shown there.
The directory is `diagnostics` beneath that broker's existing private session
data directory. Use the displayed path: MSIX can virtualize AppData.

Each MCP process logs beneath its own Windows `LocalApplicationData` folder at
`RemoteAgentLink\diagnostics\mcp`. For an unpackaged process this is normally
`%LOCALAPPDATA%\RemoteAgentLink\diagnostics\mcp`; a packaged execution alias may
see the package's virtualized equivalent. MCP logs stay on the computer running
the MCP process, not automatically on the remote target. Obtain broker logs
from both endpoints when diagnosing a channel failure.

Files are UTF-8 JSON Lines, named `slot-0.0.jsonl`, `slot-0.1.jsonl`, etc.
The second number is the rotation index: `0` is current, larger is older.
An exclusive `.lock` file reserves a writer slot. Do not remove these files
while a process is using them. Multiple MCP processes use different slots.
Idle slots are reused across restarts, rather than accumulating per-process
directories. Sort records by `timestamp`, not by filename.

Each record has schema version `1`, UTC `timestamp`, process `pid`, a random
process `run`, `component`, `level`, and a fixed `event` name. Depending on the
event it also contains an allowlisted `operation`, `outcome`, `errorCode`,
numeric HRESULT/Win32 code, elapsed `durationMs`, or a random `correlation`.
Process-start records include the Core assembly's build/module GUID.
Correlation identifies a call, native plugin instance, or broker channel within
its respective event family; it is **not** a cross-hop distributed trace ID or
an endpoint fingerprint. Correlate separate endpoint logs using time and channel
transitions, not by assuming their local channel IDs are equal.

An interrupted write can leave an incomplete final line. On restart, such a
current file is rotated before new records are written; ignore an incomplete
trailing line when inspecting a crash log.

For a readable view of a completed/current intact file, copy its path from
Settings and use PowerShell:

```powershell
Get-Content -LiteralPath 'C:\path-from-settings\slot-0.0.jsonl' |
    ConvertFrom-Json |
    Select-Object timestamp, event, operation, outcome, errorCode, durationMs, correlation |
    Format-Table -AutoSize
```

Read files rather than editing or holding exclusive handles on active logs.
If you need the complete text of a field, use `Format-List` instead of a table.

## What gets recorded

The default `info` level records:

- Broker/MCP startup and shutdown, channel creation, TLS authentication, readiness
  and closure, and significant listener/transport failures.
- Local plugin lifecycle changes and surface capability-status changes. Repeated
  lifecycle heartbeats and unchanged surface geometry are not logged.
- Device-access changes, local control revocation, input acquisition/release
  outcomes, rejected/failed operations, and cancellation.
- MCP initialization and explicit binding, plus presence failure/recovery
  transitions rather than every presence heartbeat.

For a missing connection, distinguish **no plugin lifecycle report** from a
reported plugin with **no authenticated channel ready**. `ChannelOpened` without
`ChannelAuthenticated` suggests a transport/TLS failure; authentication without
`ChannelReady` suggests the protocol handshake. A `SurfaceChanged` event with
`identity_ambiguous` is diagnostic metadata, not working capture/input support.

Operation failures include an error category such as `device_access_disabled`,
`not_bound`, `unsupported_on_endpoint`, `desktop_busy`, or `io_error`. Unknown
peer error codes become `other_agent_error`; exception messages are not recorded.
The existing error response/UI still provides user-facing recovery guidance.

Successful ordinary tool calls and call-start records are `debug` only. A
completion means the handler returned, not that a remote app visibly reacted.
Coding submission completion means acceptance, not completion of the durable
task. Use Activity or `agent_get_task`/`agent_read_events` for the existing
durable task state and results; those stores are separate and may contain private
outputs.

## Increase detail temporarily

`RALINK_LOG_LEVEL` is a **local process environment setting**, read at startup:

| Value | Behavior |
| --- | --- |
| `info` or unset | Lifecycle, access, failures and selected control outcomes |
| `warning` | Warnings/errors and logging-loss summaries |
| `debug` | Also sampled call starts/successes with elapsed time |
| `off` | No new managed log files or records |

Unrecognized values use `info` and produce a bounded `ConfigurationInvalid`
event without storing the supplied value. Restart the relevant broker or MCP
host so its newly launched process inherits the setting. Changing it in an
unrelated terminal does not change an already running process. For example,
before starting an MCP host from PowerShell:

```powershell
$env:RALINK_LOG_LEVEL = 'debug'
# Start the intended MCP host from this terminal; its MCP children inherit it.
```

Remove the setting and restart after reproducing the problem. Debug still obeys
the same byte, queue and rate bounds; it never enables argument/result logging.
SDK/host console logging is not enabled by this variable. MCP stdout remains
exclusively the stdio protocol.

## Resource and failure limits

| Resource | Bound |
| --- | --- |
| Producer queue | 512 fixed-shape records; producers never wait for disk |
| Repeated category | First 8 records per event category per minute |
| Overall event rate | 120 accepted records per process per minute |
| Loss summaries | At most one per writer batch, normally every 2 seconds |
| Files | 3 files of at most 1 MiB per writer slot |
| Concurrent file writers | 4 slots per directory: at most 12 MiB total JSONL |
| Graceful shutdown | Up to 2 seconds to drain/flush; may lose records afterward |

The limits also apply in debug mode. Event-rate limiting and a full queue
increment counters; `LoggingSummary` exposes `suppressed` and `dropped` counts.
After a storm, these counters explain gaps rather than silently presenting a
complete history. Shutdown, a process crash, or unavailable storage can still
lose queued data. There is no synchronous disk flush per operation and no
per-frame, pixel, byte-transfer, mouse-movement or heartbeat trace stream.

These are bounds **per diagnostic directory**, not retention for task databases,
all historical Windows logons, exported copies, or ETW recordings. Broker session
directories have their own bounded logs; MCP slots share one directory in the
MCP process's package/user context.

File logging is best effort. Read-only/full/unavailable storage, file sharing
conflicts or occupied slots disable that process's file sink rather than
blocking agent operations or retrying disk writes continuously. The broker
shows the failure in Settings. A fixed, payload-free stderr warning and
`RemoteAgentLink-Diagnostics` ETW event 1 record the numeric failure. Correct the
storage/slot problem, then restart that process. Do not stop another agent just
to reclaim a slot. Logging cannot change device access, replay a mutation,
release another actor's input or change an MCP binding.

## Privacy and sharing

Directories use the existing current-user-only ACL policy. Structured logging
uses explicit allowlists instead of truncating arbitrary strings. It does not
record typed keys/text, screenshots, accessibility trees, window titles, command
arguments, prompts, model output, credentials, endpoint fingerprints, SIDs,
workspace paths, request/response JSON, or exception messages/stacks.

Timestamps, method names and outcomes can still reveal activity. Review logs
before sharing them. Copy only the relevant `.jsonl` files; do not export the
entire broker data directory, certificates, identity files or task databases.
No automatic upload or diagnostic tool callable by an agent is added.

## Native and startup diagnostics

Native activation may fail before a broker exists. Native ETW tracing covers
that separate boundary; see the [native diagnostic guide](../src/Native/README.md).
Native ETW is opt-in and is not automatically copied into the managed JSONL
files. Its recording lifetime and disk limits belong to the tracing session.

The existing `ui-startup.log` (64 KiB maximum) and
`RemoteAgentLink-Broker-UI` provider remain available for WinUI startup failures.
They use bounded payload-free stack/symbol diagnostics, not operation payloads.
The older `RemoteAgentLink-Broker` observer provider/stderr fallback remains
available before managed process logging is initialized. Managed sink-health
events use `RemoteAgentLink-Diagnostics`; if no ETW listener is active, that
fallback is not a persistent record.
