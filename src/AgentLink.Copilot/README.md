# Copilot integration

## Broker integration

```csharp
await using var agents = new CopilotOperations(consent, dataDirectory);
// Include agents.Tools in the broker's tool registry.
var result = await agents.InvokeAsync(
    new OperationRequest(method, arguments, authenticatedPeerId), connectionCancellationToken);
// On authenticated transport/heartbeat changes, including connection loss:
agents.OnConnectionChanged(connected);
```

Keep this object in the **remote per-user/logon broker**, not the local MCP
process or an RDP-channel object. Do not dispose it on disconnect. Dispose it at
broker shutdown. A stable authenticated `ActorId` isolates sessions/tasks between
authenticated endpoints; if omitted, `OperationContext.ActorId` is used, then `"local"`.

The broker must survive transport-client exit. This library does not install a
service, alter disconnected-session timeout policies, survive logoff/reboot, or
keep a disconnected desktop usable. Unattended workers use broker/task/deadline
cancellation, never the per-request transport token.

The broker's device-access policy implements `IConsentService`: Yes permits
supported scoped work without per-task approval dialogs; No rejects new
operations and requests cancellation of owned work, including unattended tasks.
Each task still records its exact workspace, read/write scope, commands, timeout
and optional credit limit. These limits are not replaced with unrestricted
runtime approval. Enterprise-managed requirements and requests outside the
supported scope must fail explicitly, never wait for a removed approval UI.
The policy runs without holding transport locks; disconnect and device blocking
invalidate the relevant operation lifetimes.

An overload `(consent, dataDirectory, ICopilotRuntimeFactory)` supports offline
tests and future compatible runtime adapters. The production default always uses
the official SDK; it does not simulate model execution.

### Disconnected local Control UI

Reuse the broker's existing instance; do not construct a second task writer.
The local WinUI human-control surface can call these methods without an RDP
connection, delegation consent, or an `ActorId`:

```csharp
IReadOnlyList<AgentTask> tasks = agents.ListLocalTasks(
    limit: 100, activeOnly: true, beforeTaskId: null);
AgentTask selected = agents.GetLocalTask(selectedTaskId);
AgentTask cancellation = agents.CancelLocalTask(selectedTaskId);
```

These methods deliberately show/control tasks across this Windows user's
endpoints. **Do not expose or proxy them as MCP/RDP methods.** Remote operations keep
their actor ownership checks; there are no local-control or registration RPC
tools. The local API grants no new permissions and cannot answer approval
questions.

`ListLocalTasks` returns newest-accepted first, with limits 1–200. For the next
page, pass the last returned `TaskId` as `beforeTaskId`, retaining the same filter.
Use `activeOnly: false` for history, including interrupted/unknown tasks. Poll
`GetLocalTask` or refresh the list for outcomes. Snapshot `Prompt` and `Grant`
let the local human identify exactly what was authorized; snapshots contain no
private reasoning.

`CancelLocalTask` durably sets `CancellationRequested` and signals only the
selected delegated worker. It does not wait for runtime abort acknowledgement:
`cancelled`, `interrupted`, or `outcome_unknown` describe the eventual result.
Repeated cancellation of a terminal task is a no-op. Unknown-outcome tasks are
not blindly aborted or replayed; they require the local reconciliation flow.
Cancellation recorded before SDK dispatch survives restart as cancelled, without
starting a runtime. Stop UI polling before disposing the broker instance.

## Explicit installed runtime, no startup/build downloads

The public package is pinned to **GitHub.Copilot.SDK 1.0.11**, available on the
repository's public Microsoft mirror. Its actual NuGet
`build\GitHub.Copilot.SDK.props` pins **Copilot CLI runtime 1.0.79**. The production
adapter checks runtime version and protocol before creating/resuming sessions.
Other versions fail `incompatible_runtime` until explicitly validated in code.

The local setup UI must select an already installed, trusted runtime executable:

```csharp
LocalSetup.ConfigureManagedRuntime(dataDirectory, installedRuntimeExe);
```

Use the matching official runtime distribution; keep its required adjacent
runtime files (including `runtime.node` for the runtime wrapper). Setup does not
download, update, start, authenticate, or log in to the CLI. The library sets
`CopilotSkipCliDownload=true` and keeps package build assets private. It does not
discover an arbitrary executable through PATH. The remote user's runtime owns
authentication: `UseLoggedInUser=true`, no `GitHubToken`, token-provider callback,
credential copying, environment-token injection, or login operation is supplied.

Missing setup fails `runtime_not_configured`. Managed creation uses
`RuntimeConnection.ForStdio`; explicit resume first verifies the session through
`ListSessionsAsync`, then uses `ResumeSessionAsync` with
`ContinuePendingWork=false`. Only previously recorded broker-owned session IDs
can be resumed. No resume automatically replays a task.

Managed sessions expose only `view`, `glob`, `rg`, `powershell`, `apply_patch`, and
`ask_user`. Ambient config discovery, custom instructions, file hooks, host Git
operations, cross-session store, skills, and scheduling are disabled. Runtime
device/server managed policy is not disabled or overwritten. Enterprise
self-fetch through `EnableManagedSettings` is not forced: that SDK mode requires
an explicit GitHub token, which this broker deliberately does not acquire.
The custom typed permission handler always honors `ManagedApprovalRequired`.
It uses one-request approval only, never `ApproveAll` or persistent permission
changes.

### Safe local launcher/setup sequence

1. The remote human separately installs a trusted official CLI/runtime
   distribution matching the **1.0.79** pin, following the vendor's published
   installation instructions. Retain required adjacent files. Do not redistribute
   runtime binaries through this package without separately establishing terms
   and provenance.
2. The remote human establishes their own CLI login using the vendor's supported
   interactive UI. The broker neither requests nor forwards that credential.
3. The local Control UI selects the installed `.exe` with a file picker and
   calls `LocalSetup.ConfigureManagedRuntime(dataDirectory, selectedExe)`.
   This records a path only; it neither launches a process nor downloads a CLI.
4. While device access is enabled, `agent_start_session` uses the
   official SDK's `RuntimeConnection.ForStdio(path: selectedExe)` to launch its
   own runtime. **No separate headless launch command is required.** The SDK owns
   that process and the broker checks its reported version before proceeding.
5. For a user's existing interactive CLI, stop at the compatibility gate below.
   Do not infer `--ui-server`, token flags, or an undocumented launcher command;
   an ordinary terminal session is not proof of a supported authenticated attach
   interface. Registration alone never bypasses this gate.

## Live interactive attachment: fail-closed compatibility gate

**The stock production adapter does not yet enable live attachment.** SDK
1.0.11 exposes authenticated `RuntimeConnection.ForUri(url, connectionToken)`,
`GetForegroundSessionIdAsync`, and session resume, but these alone do not satisfy
the required ownership boundary:

* `ResumeSessionAsync` configures the session, including permission and other
  options; omitting an injected managed-settings layer can clear it.
* There is no verified public atomic exclusive-turn handoff/busy rejection
  contract coordinated with interactive input.
* `CopilotSession.AbortAsync` sends a session-wide abort without a delegated
  message/lease precondition, so a racing interactive turn cannot be safely
  distinguished by a client-side check.
* Ordinary session disposal sends `session.destroy`, not merely a subscription
  detach. Client shutdown only shuts down a spawned/FFI-owned runtime, not a
  `ForUri` runtime; the blocker is preserving the shared session and turn, not
  the name of the client's `StopAsync` method.

Consequently, `agent_attach_session` returns **`incompatible_runtime` before
connecting**, rather than changing permissions, scraping a terminal, calling
Stop/ForceStop, or pretending that attachment is implemented. Managed work is
fully functional. The offline fake tests exercise the lifecycle contract for a
future compatible externally owned runtime; they are **not** live integration
evidence.

The local setup API stores an explicit registration for future compatibility:

```csharp
LocalSetup.RegisterInteractiveRuntime(
    dataDirectory, registrationId, loopbackEndpoint, runtimeIssuedConnectionToken,
    explicitlySelectedSessionId, workingDirectory, runtimeVersion, expiresAt);
```

This is a **local-human setup API**, never an MCP method. The token must be
runtime-issued with adequate entropy (minimum 32 characters); registration expires
within 24 hours. The record is bound to the Windows user SID and the logon SID
returned by `AgentLink.Core.WindowsLogon.Current`, which reads unfiltered token
groups rather than `WindowsIdentity.Groups`. The token is protected by DPAPI
CurrentUser. Only literal loopback HTTP
TCP endpoints with an explicit port and no userinfo/path/query are accepted.
Do not expose the registration record, protected token, or local setup API to MCP.
`agent_attach_session` accepts only `registrationId` and an explicit `sessionId`.
It never scans ports, accepts endpoint/token arguments, chooses the foreground
session implicitly, or changes foreground.

### Human registration JSON schema

The complete versioned input schema is
[`live-runtime-registration.schema.json`](live-runtime-registration.schema.json).
It is also embedded in the assembly and exposed as
`LocalSetup.RegistrationInputSchema`, so the packaged UI needs no loose schema
file. Required JSON properties are:

| Property | Constraint |
| --- | --- |
| `schemaVersion` | Integer `1` |
| `registrationId` | Nonblank local registration name, at most 128 characters |
| `endpoint` | Literal loopback HTTP URI with an explicit port, such as `http://127.0.0.1:41235` or `http://[::1]:41235` |
| `connectionToken` | Runtime-issued connection secret, 32–4096 characters; password/write-only UI field, never a GitHub credential |
| `sessionId` | Exact human-selected session, nonblank, at most 128 characters |
| `workingDirectory` | Existing canonical absolute Windows path; no reparse points or broker-data overlap |
| `runtimeVersion` | Nonblank reported runtime version, at most 128 characters |
| `expiresAt` | ISO-8601 explicit-offset timestamp in the future and within 24 hours |

The local human UI can build/import that JSON and call:

```csharp
LocalSetup.RegisterInteractiveRuntimeJson(dataDirectory, registrationJsonElement);
```

The importer rejects unknown fields, unsupported schema versions, invalid
endpoints, and expired grants. It performs semantic filesystem and expiry checks
in addition to the JSON schema. Input documents cannot supply `userSid`, `logonId`,
or `protectedConnectionToken`: those are generated internally.

This JSON is **local setup input**, not an on-disk plaintext configuration file
or an RPC payload. Prefer building it in memory from local controls. Do not log
it, place it in command-line arguments/environment variables, copy it through
MCP, or persist the token to an unprotected import file. Clear the password
control after import. If a trusted launcher supplies a local import file, the
local setup application must protect and remove that sensitive file.

Persistence remains transactional SQLite: table `settings`, key
`live:<registrationId>`, with a camel-case `LiveRuntimeRegistration` payload. It
contains endpoint/session/workspace/version/expiry, current Windows user SID,
the shared `S-1-5-5-*` logon SID in `logonId`, and DPAPI CurrentUser
`protectedConnectionToken` ciphertext.
Do not write this internal row directly; use the public setup methods. The
declaration neither authorizes a task nor verifies live compatibility.
Earlier development records using a hexadecimal authentication-LUID string fail
closed and must be re-registered locally; they are not silently migrated or
treated as a current-logon grant.

Enabling this gate requires a verified official runtime/SDK contract with:

1. Authenticated registration and same-logon identity.
2. Non-mutating attachment preserving all existing policy.
3. Runtime-enforced exclusive delegated-turn ownership and atomic busy rejection.
4. Cancellation targeting only the owned turn.
5. Connection-only detach/disposal preserving the external runtime and session.

Pinned upstream evidence: [`Client.cs`](https://github.com/github/copilot-sdk/blob/a550258d5c37bd662197536992a23d633bfe5804/dotnet/src/Client.cs#L621-L646)
only requests runtime shutdown for an owned CLI/FFI host;
[`Session.cs`](https://github.com/github/copilot-sdk/blob/a550258d5c37bd662197536992a23d633bfe5804/dotnet/src/Session.cs#L1786-L1790)
performs session-wide abort, and its
[`DisposeAsync`](https://github.com/github/copilot-sdk/blob/a550258d5c37bd662197536992a23d633bfe5804/dotnet/src/Session.cs#L1912-L1924)
sends `session.destroy`.

No interactive launch flags are invented here. The local setup application must
use a **documented supported launcher/runtime combination** when one satisfies
these requirements. `OfficialSdkRuntimeFactory.LiveCompatibilityRequirements`
exposes the requirements for the control UI.

## Task contract and policy

`agent_send` requires `sessionId`, `prompt`, `idempotencyKey`, and
`timeoutSeconds` (1–86400). `workingDirectory` defaults to the session directory
and cannot switch it. `unattended` defaults to false; true requires an explicit
positive finite `maxAiCredits`. `allowWorkspaceRead`, `allowWorkspaceWrite`,
and `approvedCommands` describe the task's explicit scope, defaulting to no
workspace/command preauthorization. Device access does not widen these fields.

Only canonical absolute local Windows paths without reparse points or alternate
streams qualify. The workspace cannot overlap broker-private data. Trusted
workspace read/write requests can be scoped explicitly. Exact, task-scoped
non-UI `dotnet build/test/--info` and `git status/diff/log/show` commands without
shell expression syntax qualify; extra paths, URLs, redirection, sandbox-bypass
requests, and managed-approval requirements prevent preapproval.

**This is not an OS sandbox.** Trusted build scripts can execute arbitrary code,
and path checks cannot provide kernel-level protection against filesystem races.
Only trusted workspaces should receive unattended build grants. Unrecognized or
out-of-scope permissions are not automatically approved. Desktop requests
additionally require `DesktopAllowed`; a coding session is never a workaround
for failed remote computer-use tools.

The required credit ceiling is passed through the pinned SDK's typed
`session.Rpc.Options.UpdateAsync(sessionLimits: ...)` API before message dispatch.
Unsupported limit RPCs fail before sending. These are **post-paid soft limits for
the SDK's current session accounting window**, not guaranteed hard billing caps
or guaranteed fresh allowances on every task. A finite broker deadline is also
enforced. Grants, observed `CopilotUsage.TotalNanoAiu` usage (deduplicated by event),
and reported accounting-window credits at exhaustion are persisted. Observed
usage is not a promise that every provider supplied billing telemetry. No
reasoning-token counts or private reasoning text are returned.

SDK normal `ask_user` questions produce durable question IDs.
`agent_answer_question` accepts only the currently pending non-permission
question, with choice validation; it cannot grant permissions or increase
budgets. Arbitrary elicitation forms/URL challenges are local-only and declined
after local attention, not relayed as remote credential/authorization forms.

## Durability and cancellation

SQLite uses WAL, `synchronous=FULL`, parameter-bound statements, transactional
task/event updates, a unique `(sessionId,idempotencyKey)` constraint, and an active
writer constraint per session. An exclusive store lease prevents two broker
instances from running the same tasks. The private `dataDirectory\copilot`
directory is current-user-owned with a protected user-only DACL.

Acceptance is committed **before** returning the task ID or scheduling work.
Repeating the same key/grant/prompt returns the original task even after restart
or result expiry; a different payload produces `idempotency_conflict`. Different
simultaneous turns return `session_busy`, never surprise-inject or silently queue.

States: `accepted`, `running`, `waiting_for_input`, `waiting_for_approval`,
`waiting_for_desktop`, `completed`, `failed`, `cancelled`, `interrupted`, and
`outcome_unknown`. Disconnect interrupts attended work and attempts an owned-turn
abort. Scoped unattended work can continue while device access stays enabled;
normal questions can wait, but removed broker approval dialogs are not a recovery
path for unsupported permissions. Device blocking requests cancellation of owned
work without rolling back prior effects.
Cancellation responses acknowledge **broker receipt**, not guaranteed completed
cancellation; poll `agent_get_task` for the outcome. Busy detach is rejected.

Before dispatch, a durable marker is committed. A crash/transport failure between
SDK dispatch and message-ID persistence is `outcome_unknown`, never retried.
Other dispatched work still active at restart is conservatively unknown as well;
accepted undispatched work is interrupted. Acknowledged runtime aborts on clean
shutdown are interrupted. Side effects already performed are not rolled back.

An uncertain session is blocked pending local reconciliation. After the broker
is stopped and a local human has verified the prior managed turn really stopped,
the local UI may call `LocalSetup.ReconcileStoppedManagedSession(...)`.
This only acknowledges session safety; it never changes the historical task
outcome or resends its prompt. Restart and explicitly resume afterward.
The task's unknown outcome and the session's reconciliation requirement commit
in one transaction. Ordinary session saves cannot clear that requirement.
Recovery repairs older split-write records. Local reconciliation atomically
records task acknowledgement markers and clears the session requirement, so
historical unknown outcomes do not re-block an explicitly reconciled session
after another restart.

Events include status, normal questions, final answers, tool summaries and usage,
not reasoning streams or raw tool payloads. Each task retains at most 1,000
events; pages accept `afterCursor` and `limit` (1–200), returning stable cursors,
`hasMore`, and `historyTruncated`. Event `data` is a JSON-encoded string.
Payloads expire 30 days after the task deadline, pruned on broker startup or
session listing. Minimal task/idempotency tombstones remain to prevent duplicate
execution; expired results are explicitly marked. No promise of perpetual
conversation/result retention is made.

## Offline build/test

From the repository root:

```powershell
dotnet restore .\tests\Copilot.Tests\Copilot.Tests.csproj
dotnet build .\src\AgentLink.Copilot\AgentLink.Copilot.csproj --no-restore
dotnet run --project .\tests\Copilot.Tests\Copilot.Tests.csproj --no-restore
```

The root `NuGet.Config` uses only the public Microsoft dotnet-public mirror:
`https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json`.
The direct references are exactly pinned to SDK 1.0.11, Microsoft.Data.Sqlite
10.0.9, and System.Security.Cryptography.ProtectedData 10.0.9, all available there.
SDK 1.0.13 exists upstream but is not required by this integration. No NuGet v2
override or private feed is required.
The standalone console tests use injected fake runtimes, real SQLite, and local
synthetic registration tokens. They make **no model calls or login requests**.
