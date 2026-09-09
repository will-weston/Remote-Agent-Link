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

After TLS key-possession validation, `protocol.hello` exchanges protocol version, Windows session ID and logon SID. A modern client explicitly advertises `supportsPendingPairing=true` and includes bounded `displayName` and `trustedPeer` metadata. The latter is a peer report, not local authority. An old client sending `{}` receives exactly the original three-field response: extra fields, including null properties, would break its strict deserializer. Already-paired legacy peers remain supported; their missing device/session metadata is displayed as unknown.

An unpaired channel may exchange only bounded hello/heartbeat metadata and cancellation. Frames are capped at 4096 bytes before payload allocation, metadata objects at 2048 UTF-8 bytes, and pending traffic at 32 frames per five seconds. Tool catalogs and execution remain unavailable until current local trust and negotiated readiness permit admission. `protocol.ping` supplies a periodic heartbeat. The client sends a ping every five seconds with a ten-second deadline; the remote endpoint expires a connection after twenty seconds without an incoming request.

A failed heartbeat or closed stream revokes connection-scoped desktop grants. A new connection gets a new epoch. No desktop mutations are replayed automatically. An unacknowledged mutation has an unknown execution outcome until the client observes fresh state.

Successful TLS certificate validation is not sufficient for admission: the current pin is checked again while registering the peer under the same lock used for trust changes. A change since the initial handshake closes the channel before readiness or consent activation, even if a revocation notification has not yet run. Incoming operation dispatch and outgoing forwarding recheck current trust. A handshake racing pairing revocation cannot become an untracked authorized peer.

`tools.list` supplies the supported remote catalog. The local MCP host caches that catalog after binding and emits `notifications/tools/list_changed`. Human grants are enforced at invocation; listing an operation is not authorization to execute it.

## Local IPC

The broker exposes per-user, per-Windows-session named pipes. The server DACL admits the current user only, kernel pipe mode rejects remote network clients, and the broker verifies the client process's session, user SID, and logon SID. Native transport and local control use different pipes. The MCP actor is validated and stamped at the broker, not accepted as an authenticated identity by itself.

Authenticated local current-session catalog access and durable agent reads/cancellation remain available without an incoming RDP channel. That does not make the desktop available or grant new permissions. `ui.show` is local control IPC only, never a channel or MCP permission-approval operation.

The MCP executable accepts an explicit `--broker-pipe name` override for local integration harnesses. It accepts only a simple local name and retains current-user server verification; it cannot target a network pipe. Automated MCP checks use random, first-instance pipe names and never impersonate a running production broker's pipe.

`--connection <connectionId>` configures an immutable remote target at MCP startup. It loads that endpoint's catalog without requiring an initial bind call. Ordinary offline/pipe failures leave only connection-management tools available until a catalog can be loaded, with an explicit stderr diagnostic. Pairing-required states also retain management tools and explicitly direct the human to verify both endpoint fingerprints. Malformed responses, unauthorized pipes, configuration failures, and generic operation failures are not hidden as ordinary downtime. A missing target is never replaced with an available one. Binding the same ID refreshes its catalog; binding a different ID is rejected. The flag is mutually exclusive with `--current-session` and does not grant any capability.
