using AgentLink.Copilot;
using AgentLink.Core;
using GitHub.Copilot;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Official SDK pinned public surface; no runtime download", SdkSurface),
    ("Durable acceptance, concurrent idempotency and result replay", DurableIdempotency),
    ("Exactly one writer and idempotency conflicts", SingleWriter),
    ("Unattended work survives request cancellation and disconnect", Unattended),
    ("Attended work interrupts and aborts on disconnect", Disconnect),
    ("Cancellation targets the owned turn", Cancel),
    ("Disconnected local Control UI lists and cancels peer-owned tasks", LocalControl),
    ("Undispatched local cancellation survives broker restart", DurableCancellation),
    ("Finite timeout is enforced", Timeout),
    ("Out-of-scope permissions fail without dialogs or reconnect replay", PermissionWaiting),
    ("Managed policy is never preapproved", ManagedPolicy),
    ("Unknown permissions cannot invoke an approval handler", ConsentEpoch),
    ("Device off blocks all coding APIs and cancels broker-owned work", DeviceAccess),
    ("Device No during acceptance cannot strand a committed task", DeviceAccessDuringAcceptance),
    ("Channel loss cancels only its principal's attended work", PrincipalDisconnect),
    ("Usage accounting is durable and deduplicated", UsageAccounting),
    ("Normal questions survive disconnect and enforce choice schema", Questions),
    ("Unknown dispatch outcome is never retried", UnknownOutcome),
    ("Restart records accepted/interrupted/unknown truthfully", Recovery),
    ("Unknown outcomes and reconciliation requirements commit atomically", AtomicUnknownOutcome),
    ("Legacy crash gaps repair and local reconciliation survives restart", ReconciliationRecovery),
    ("Cursor bounds, retention and durable tombstones", Retention),
    ("Only a local same-logon registration can attach", Registration),
    ("Registration shares broker logon SID and rejects stale identity formats", RegistrationLogon),
    ("Local registration JSON is strict, protected and versioned", RegistrationJson),
    ("Busy live turns reject surprise injection; detach preserves CLI", LiveOwnership),
    ("Production live adapter fails closed without contacting endpoint", LiveCompatibility),
    ("Canonical workspace and command grants fail closed", Policy),
    ("Endpoint policy denial and actor isolation", ConsentAndOwnership),
    ("One broker writer owns the store", StoreLease),
    ("Managed resume obeys endpoint authorization", Resume),
    ("Unconfigured production runtime cannot launch", RuntimeConfiguration),
    ("MCP cannot submit connection endpoints, tokens or permission answers", ToolSchemas)
};
int passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
        passed++;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {error}");
        Environment.ExitCode = 1;
    }
}
Console.WriteLine($"{passed}/{tests.Length} offline tests passed. No model requests, credentials, or login were used.");

static Task SdkSurface()
{
    Check(OfficialSdkRuntimeFactory.SdkVersion == "1.0.11", "SDK pin");
    Check(OfficialSdkRuntimeFactory.RequiredRuntimeVersion == "1.0.79", "runtime pin");
    Check(typeof(CopilotClient).Assembly.GetName().Name == "GitHub.Copilot.SDK", "official package assembly");
    Check(RuntimeConnection.ForUri("http://127.0.0.1:1", new string('x', 32)) is UriRuntimeConnection, "authenticated descriptor");
    Check(typeof(CopilotClient).GetMethod("GetForegroundSessionIdAsync") is not null, "foreground API exists");
    return Task.CompletedTask;
}

static async Task DurableIdempotency()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var arguments = fixture.SendArguments(session, "durable-key");
    var sends = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Call("agent_send", arguments)));
    var id = sends[0].GetProperty("taskId").GetString()!;
    Check(sends.All(value => value.GetProperty("taskId").GetString() == id), "same durable identity");
    await Until(() => fixture.Runtime.Sends == 1);
    fixture.Runtime.Emit("reasoning", "must-not-be-visible");
    fixture.Runtime.Emit("tool_summary", "Build complete");
    fixture.Runtime.Finish("final answer");
    var task = await fixture.State(id, "completed");
    Check(task.GetProperty("result").GetString() == "final answer", "final result");
    var events = await fixture.Call("agent_read_events", new { taskId = id });
    Check(!events.GetRawText().Contains("must-not-be-visible", StringComparison.Ordinal), "reasoning excluded");
    await fixture.Operations.DisposeAsync();
    fixture.Operations = new CopilotOperations(fixture.Consent, fixture.Data, fixture.Factory);
    var replay = await fixture.Call("agent_send", arguments);
    Check(replay.GetProperty("taskId").GetString() == id && replay.GetProperty("state").GetString() == "completed", "replay after restart");
    Check(fixture.Factory.OpenCount == 1 && fixture.Runtime.Sends == 1, "no restart redispatch");
}

static async Task SingleWriter()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var task = await fixture.Send(session, "a");
    await Until(() => fixture.Runtime.Sends == 1);
    await Error("session_busy", () => fixture.Send(session, "b"));
    var changed = new { sessionId = session, prompt = "different", idempotencyKey = "a", timeoutSeconds = 10 };
    await Error("idempotency_conflict", () => fixture.Call("agent_send", changed));
    fixture.Runtime.Finish();
    await fixture.State(task, "completed");
}

static async Task Unattended()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    using var request = new CancellationTokenSource();
    var task = await fixture.Call("agent_send", fixture.SendArguments(session, "offline", true), request.Token);
    var id = task.GetProperty("taskId").GetString()!;
    request.Cancel();
    await Until(() => fixture.Runtime.Sends == 1);
    fixture.Connection(false);
    int approvals = fixture.Consent.Requests.Count;
    Check(await fixture.Runtime.Permission(new("read", "Read workspace", Path.Combine(fixture.Workspace, "code.cs"))), "granted read offline");
    Check(await fixture.Runtime.Permission(new("shell", "Build", Command: "dotnet build", PossiblePaths: [fixture.Workspace])), "exact granted build offline");
    Check(fixture.Consent.Requests.Count == approvals, "no new human approval offline");
    Check(fixture.Runtime.PreparedBudget == 5 && fixture.Runtime.Aborts == 0, "soft budget and independent lifetime");
    fixture.Runtime.Finish("offline done");
    await fixture.State(id, "completed");
    fixture.Connection(true);
    var saved = await fixture.Call("agent_get_task", new { taskId = id });
    Check(saved.GetProperty("result").GetString() == "offline done", "reconnect result");
    Check(fixture.Consent.Requests.Any(r => r.Capability == "unattended-execution" && r.Unattended), "additional human grant");
}

static async Task Disconnect()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var id = await fixture.Send(session, "disconnect");
    await Until(() => fixture.Runtime.Sends == 1);
    fixture.Connection(false);
    var task = await fixture.State(id, "interrupted");
    Check(task.GetProperty("errorCode").GetString() == "connection_lost", "disconnect state");
    Check(fixture.Runtime.Aborts == 1, "owned turn abort");
}

static async Task Cancel()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var id = await fixture.Send(session, "cancel");
    await Until(() => fixture.Runtime.Sends == 1);
    await fixture.Call("agent_cancel_task", new { taskId = id });
    await fixture.State(id, "cancelled");
    Check(fixture.Runtime.Aborts == 1 && fixture.Runtime.LastAbort == "message-1", "message ownership");
}

static async Task LocalControl()
{
    await using var fixture = new Fixture();
    var firstSession = await fixture.Start();
    var first = await fixture.Send(firstSession, "local-first", true);
    var firstRuntime = fixture.Runtime;
    await Until(() => firstRuntime.Sends == 1);
    var secondSession = (await fixture.Call("agent_start_session", new { workingDirectory = fixture.Workspace },
        owner: "second-peer")).GetProperty("sessionId").GetString()!;
    var second = (await fixture.Call("agent_send", fixture.SendArguments(secondSession, "local-second", true),
        owner: "second-peer")).GetProperty("taskId").GetString()!;
    var secondRuntime = fixture.Runtime;
    await Until(() => secondRuntime.Sends == 1);
    fixture.Connection(false);
    int approvals = fixture.Consent.Requests.Count;
    Check(fixture.Operations.ListLocalTasks(activeOnly: true).Count == 2, "local human sees tasks across paired peers");
    Check(fixture.Operations.ListLocalTasks(limit: 1)[0].TaskId == second &&
        fixture.Operations.ListLocalTasks(limit: 1, beforeTaskId: second)[0].TaskId == first, "bounded stable local paging");
    var requested = fixture.Operations.CancelLocalTask(first);
    Check(requested.CancellationRequested, "local cancellation receipt is durable");
    await Until(() => fixture.Operations.GetLocalTask(first).State == "cancelled");
    Check(firstRuntime.Aborts == 1 && secondRuntime.Aborts == 0 &&
        fixture.Operations.GetLocalTask(second).State == "running", "only selected delegated turn is cancelled");
    fixture.Operations.CancelLocalTask(first);
    Check(firstRuntime.Aborts == 1, "terminal cancellation is idempotent");
    fixture.Operations.CancelLocalTask(second);
    await Until(() => fixture.Operations.GetLocalTask(second).State == "cancelled");
    Check(fixture.Consent.Requests.Count == approvals && fixture.Operations.ListLocalTasks(activeOnly: true).Count == 0,
        "local cancellation needs neither RDP nor new approval");
    await Error("invalid_arguments", () =>
    {
        fixture.Operations.ListLocalTasks(limit: 201);
        return Task.CompletedTask;
    });
    await Error("task_not_found", () =>
    {
        fixture.Operations.CancelLocalTask("not-found");
        return Task.CompletedTask;
    });
    await Error("unknown_method", () => fixture.Call("agent_list_local_tasks", new { }));
    await Error("unknown_method", () => fixture.Call("agent_cancel_local_task", new { taskId = second }));
    await Error("task_not_found", () => fixture.Call("agent_cancel_task", new { taskId = second }));
    await fixture.Operations.DisposeAsync();
    fixture.Operations = new CopilotOperations(fixture.Consent, fixture.Data, fixture.Factory);
    Check(fixture.Operations.GetLocalTask(first).CancellationRequested &&
        fixture.Operations.GetLocalTask(first).State == "cancelled", "local task snapshots persist while offline");
}

static async Task DurableCancellation()
{
    await using var fixture = new Fixture();
    await fixture.Operations.DisposeAsync();
    using (var store = new TaskStore(LocalSetup.EnsurePrivateDirectory(fixture.Data)))
    {
        store.SaveSession(new("cancel-before-dispatch", fixture.Workspace, "peer"));
        var now = DateTimeOffset.UtcNow;
        store.Accept(new("local-cancel", "cancel-before-dispatch", "cancel-key", "fingerprint", "prompt", "accepted",
            new(fixture.Workspace, false, false, [], true, 20, 1), now, now.AddSeconds(20), CancellationRequested: true));
    }
    fixture.Connection(false);
    fixture.Operations = new CopilotOperations(fixture.Consent, fixture.Data, fixture.Factory);
    var task = fixture.Operations.GetLocalTask("local-cancel");
    Check(task.State == "cancelled" && task.ErrorCode == "cancelled_before_dispatch" &&
        fixture.Factory.OpenCount == 0, "restart honors durable undispatched cancellation without launching runtime");
}

static async Task Timeout()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var result = await fixture.Call("agent_send", new { sessionId = session, prompt = "wait", idempotencyKey = "timeout", timeoutSeconds = 1 });
    var task = await fixture.State(result.GetProperty("taskId").GetString()!, "failed");
    Check(task.GetProperty("errorCode").GetString() == "timeout" && fixture.Runtime.Aborts == 1, "finite deadline abort");
    await Error("invalid_arguments", () => fixture.Call("agent_send",
        new { sessionId = session, prompt = "bad", idempotencyKey = "bad", timeoutSeconds = 0 }));
    await Error("invalid_arguments", () => fixture.Call("agent_send",
        new { sessionId = session, prompt = "bad", idempotencyKey = "bad", timeoutSeconds = 10, unattended = true }));
}

static async Task PermissionWaiting()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var id = await fixture.Send(session, "permission", true);
    await Until(() => fixture.Runtime.Sends == 1);
    fixture.Connection(false);
    int before = fixture.Consent.Requests.Count;
    var permission = fixture.Runtime.Permission(new("shell", "Other command", Command: "other", PossiblePaths: []));
    Check(!await permission && fixture.Consent.Requests.Count == before, "out-of-scope permission must be denied without approval");
    fixture.Connection(true);
    Check(!await permission && fixture.Consent.Requests.Count == before, "reconnect replayed denied permission");
    await Error("question_not_found", () => fixture.Call("agent_answer_question", new { taskId = id, questionId = "permission", answer = "approve" }));
    fixture.Runtime.Finish();
    await fixture.State(id, "completed");
}

static async Task ManagedPolicy()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var id = await fixture.Send(session, "managed", true);
    await Until(() => fixture.Runtime.Sends == 1);
    var count = fixture.Consent.Requests.Count;
    fixture.Consent.Allow = false;
    bool result = await fixture.Runtime.Permission(new("read", "Enterprise approval", Path.Combine(fixture.Workspace, "code.cs"), ManagedApprovalRequired: true));
    Check(!result && fixture.Consent.Requests.Count == count, "managed approval was granted or prompted");
    fixture.Runtime.Finish();
    await fixture.State(id, "completed");
}

static async Task ConsentEpoch()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var id = await fixture.Send(session, "epoch", true);
    await Until(() => fixture.Runtime.Sends == 1);
    var delayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    int calls = 0;
    fixture.Consent.Handler = (_, _) => Interlocked.Increment(ref calls) == 1 ? delayed.Task : Task.FromResult(true);
    var permission = fixture.Runtime.Permission(new("unknown", "New approval"));
    Check(!await permission && calls == 0, "Unknown permission reached approval handler.");
    fixture.Connection(false);
    delayed.TrySetResult(true);
    await Task.Delay(30);
    Check(!await permission, "Late approval changed a denied permission.");
    fixture.Connection(true);
    Check(!await permission && calls == 0, "Reconnect retried a denied permission.");
    fixture.Runtime.Finish();
    await fixture.State(id, "completed");
}

static async Task DeviceAccess()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var arguments = fixture.SendArguments(session, "device-off", true);
    var id = (await fixture.Call("agent_send", arguments)).GetProperty("taskId").GetString()!;
    var runtime = fixture.Runtime;
    await Until(() => runtime.Sends == 1 &&
        fixture.Operations.GetLocalTask(id).MessageId is not null);
    fixture.Consent.SetDeviceAccess(false);
    fixture.Operations.OnDeviceAccessChanged(false);
    foreach (var tool in fixture.Operations.Tools)
        await Error("device_access_disabled", () => fixture.Call(tool.Name, new { }));
    await Until(() => TaskStates.Terminal(fixture.Operations.GetLocalTask(id).State));
    var cancelled = fixture.Operations.GetLocalTask(id);
    Check(cancelled.State == "cancelled" && cancelled.ErrorCode == "device_access_disabled" &&
        cancelled.CancellationRequested && runtime.Aborts == 1 && runtime.LastAbort == "message-1" &&
        runtime.ExternalStops == 0, "Device No must cancel only the owned turn, including unattended work.");
    Check(fixture.Operations.ListLocalTasks().Single().TaskId == id, "Local human metadata unavailable while off.");
    fixture.Consent.SetDeviceAccess(true);
    fixture.Operations.OnDeviceAccessChanged(true);
    var replay = await fixture.Call("agent_send", arguments);
    Check(replay.GetProperty("taskId").GetString() == id && runtime.Sends == 1 &&
        replay.GetProperty("state").GetString() == "cancelled", "Re-enable replayed a cancelled task.");
    var next = await fixture.Send(session, "fresh-after-on");
    await Until(() => runtime.Sends == 2);
    runtime.Finish();
    await fixture.State(next, "completed");

    // Exercise the acceptance boundary when an authorization epoch changes
    // while the adapter's policy check is completing.
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Consent.Handler = (_, _) => { entered.TrySetResult(); return release.Task; };
    var pending = fixture.Send(session, "cancel-before-accept");
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    fixture.Consent.SetDeviceAccess(false);
    fixture.Operations.OnDeviceAccessChanged(false);
    fixture.Consent.SetDeviceAccess(true);
    fixture.Operations.OnDeviceAccessChanged(true);
    release.TrySetResult(true);
    try { await pending; throw new Exception("An old request survived an Off/On epoch."); }
    catch (OperationCanceledException) { }
    Check(runtime.Sends == 2 && fixture.Operations.ListLocalTasks().Count == 2,
        "A cancelled pre-acceptance request was accepted or replayed.");
    fixture.Consent.Handler = null;
    Register(fixture);
    await fixture.Call("agent_attach_session", new { registrationId = "human", sessionId = "live" });
    var external = fixture.Runtime;
    var externalTask = await fixture.Send("live", "external-owned");
    await Until(() => fixture.Operations.GetLocalTask(externalTask).MessageId is not null);
    fixture.Consent.SetDeviceAccess(false);
    fixture.Operations.OnDeviceAccessChanged(false);
    await Until(() => TaskStates.Terminal(fixture.Operations.GetLocalTask(externalTask).State));
    Check(external.Aborts == 1 && external.LastAbort == "message-1" && external.ExternalStops == 0 &&
        external.Disposals == 0, "Device No killed or disposed an external user session instead of cancelling only its owned turn.");
}

static async Task DeviceAccessDuringAcceptance()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var store = typeof(CopilotOperations).GetField("_store",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(fixture.Operations)
        as TaskStore ?? throw new InvalidOperationException("Task store is unavailable.");
    var database = typeof(TaskStore).GetField("_connection",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(store)
        as SqliteConnection ?? throw new InvalidOperationException("Task writer is unavailable.");
    database.CreateFunction("disable_device_during_acceptance", () =>
    {
        fixture.Consent.SetDeviceAccess(false);
        return 1;
    });
    using (var command = database.CreateCommand())
    {
        command.CommandText = """
            CREATE TRIGGER disable_during_acceptance AFTER INSERT ON tasks
            WHEN NEW.idempotency_key = 'acceptance-race'
            BEGIN SELECT disable_device_during_acceptance(); END;
            """;
        command.ExecuteNonQuery();
    }
    var accepted = await fixture.Call("agent_send", fixture.SendArguments(session, "acceptance-race", true));
    var id = accepted.GetProperty("taskId").GetString()!;
    await Until(() => TaskStates.Terminal(fixture.Operations.GetLocalTask(id).State));
    var cancelled = fixture.Operations.GetLocalTask(id);
    Check(cancelled.State == "cancelled" && cancelled.ErrorCode == "device_access_disabled" &&
        cancelled.CancellationRequested && !cancelled.DispatchStarted && fixture.Runtime.Sends == 0,
        "No during commit left accepted work stranded or dispatched without access.");
    fixture.Consent.SetDeviceAccess(true);
    fixture.Operations.OnDeviceAccessChanged(true);
    var next = await fixture.Send(session, "after-acceptance-race");
    await Until(() => fixture.Runtime.Sends == 1);
    fixture.Runtime.Finish();
    await fixture.State(next, "completed");
}

static async Task PrincipalDisconnect()
{
    await using var fixture = new Fixture();
    var localSession = await fixture.Start();
    var localTask = await fixture.Send(localSession, "local");
    var localRuntime = fixture.Runtime;
    var remoteSession = (await fixture.Call("agent_start_session", new { workingDirectory = fixture.Workspace },
        owner: "remote-principal")).GetProperty("sessionId").GetString()!;
    var remoteTask = (await fixture.Call("agent_send", fixture.SendArguments(remoteSession, "remote"),
        owner: "remote-principal")).GetProperty("taskId").GetString()!;
    var remoteRuntime = fixture.Runtime;
    await Until(() => localRuntime.Sends == 1 && remoteRuntime.Sends == 1 &&
        fixture.Operations.GetLocalTask(remoteTask).MessageId is not null);
    fixture.Operations.OnPrincipalDisconnected("remote-principal");
    await Until(() => fixture.Operations.GetLocalTask(remoteTask).State == "interrupted");
    Check(remoteRuntime.Aborts == 1 && localRuntime.Aborts == 0 &&
        fixture.Operations.GetLocalTask(localTask).State == "running", "A remote disconnect cancelled local independent work.");
    await Error("disconnected", () => fixture.Call("agent_send", fixture.SendArguments(remoteSession, "stale"),
        owner: "remote-principal"));
    fixture.Operations.OnPrincipalConnected("remote-principal");
    var fresh = (await fixture.Call("agent_send", fixture.SendArguments(remoteSession, "fresh"),
        owner: "remote-principal")).GetProperty("taskId").GetString()!;
    await Until(() => remoteRuntime.Sends == 2);
    remoteRuntime.Finish();
    localRuntime.Finish();
    await Until(() => fixture.Operations.GetLocalTask(fresh).State == "completed");
    await fixture.State(localTask, "completed");
}

static async Task UsageAccounting()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var id = await fixture.Send(session, "usage", true);
    await Until(() => fixture.Runtime.Sends == 1);
    fixture.Runtime.Usage("usage-1", 1234);
    fixture.Runtime.Usage("usage-1", 1234);
    fixture.Runtime.Usage("usage-2", 5678);
    fixture.Runtime.Usage("bad", double.NaN);
    fixture.Runtime.BudgetExhausted(5.2);
    var waiting = await fixture.State(id, "failed");
    Check(waiting.GetProperty("observedNanoAiu").GetDouble() == 6912 &&
        waiting.GetProperty("creditWindowUsed").GetDouble() == 5.2, "observed usage kept in native SDK units and credits");
    await Error("question_not_found", () => fixture.Call("agent_answer_question", new { taskId = id, questionId = "budget", answer = "increase" }));
    Check(waiting.GetProperty("errorCode").GetString() == "budget_exhausted" && fixture.Runtime.Aborts == 1,
        "Budget exhaustion must stop owned work, not queue approval or increase the limit.");
    await fixture.Operations.DisposeAsync();
    fixture.Operations = new CopilotOperations(fixture.Consent, fixture.Data, fixture.Factory);
    var persisted = await fixture.Call("agent_get_task", new { taskId = id });
    Check(persisted.GetProperty("observedNanoAiu").GetDouble() == 6912, "accounting persists across restart");
}

static async Task Questions()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var id = await fixture.Send(session, "question", true);
    await Until(() => fixture.Runtime.Sends == 1);
    fixture.Connection(false);
    var response = fixture.Runtime.Question(new("Choose a framework", ["net10.0", "net8.0"], false));
    await fixture.State(id, "waiting_for_input");
    var events = await fixture.Call("agent_read_events", new { taskId = id });
    var questionEvent = events.GetProperty("events").EnumerateArray().Last(e => e.GetProperty("kind").GetString() == "question");
    using var data = JsonDocument.Parse(questionEvent.GetProperty("data").GetString()!);
    var questionId = data.RootElement.GetProperty("questionId").GetString();
    await Error("disconnected", () => fixture.Call("agent_answer_question", new { taskId = id, questionId, answer = "net10.0" }));
    Check(!response.IsCompleted, "question held offline");
    fixture.Connection(true);
    await Error("invalid_answer", () => fixture.Call("agent_answer_question", new { taskId = id, questionId, answer = "anything" }));
    await fixture.Call("agent_answer_question", new { taskId = id, questionId, answer = "net10.0" });
    Check(await response == "net10.0", "normal answer delivered");
    fixture.Runtime.Finish();
    await fixture.State(id, "completed");
}

static async Task UnknownOutcome()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    fixture.Runtime.FailAfterDispatch = true;
    var arguments = fixture.SendArguments(session, "unknown");
    var accepted = await fixture.Call("agent_send", arguments);
    var id = accepted.GetProperty("taskId").GetString()!;
    await fixture.State(id, "outcome_unknown");
    var retry = await fixture.Call("agent_send", arguments);
    Check(retry.GetProperty("taskId").GetString() == id && fixture.Runtime.Sends == 1 && fixture.Runtime.Aborts == 0, "ambiguous dispatch not repeated or blindly aborted");
    await Error("outcome_unknown", () => fixture.Send(session, "different"));
}

static async Task Recovery()
{
    await using var fixture = new Fixture();
    await fixture.Operations.DisposeAsync();
    var directory = LocalSetup.EnsurePrivateDirectory(fixture.Data);
    var session = new AgentSession("crash-session", fixture.Workspace, "peer");
    using (var store = new TaskStore(directory))
    {
        store.SaveSession(session);
        var now = DateTimeOffset.UtcNow;
        var grant = new TaskGrant(fixture.Workspace, false, false, [], true, 100, 1);
        store.Accept(new("queued", session.SessionId, "q", "fingerprint", "prompt", "accepted", grant, now, now.AddSeconds(100)));
        store.Update("queued", task => task with { State = "interrupted" });
        store.Accept(new("dispatch", session.SessionId, "d", "fingerprint", "prompt", "running", grant, now, now.AddSeconds(100), DispatchStarted: true));
        store.SaveSession(new("accepted-session", fixture.Workspace, "peer"));
        store.Accept(new("accepted", "accepted-session", "a", "fingerprint", "prompt", "accepted", grant, now, now.AddSeconds(100)));
    }
    fixture.Operations = new CopilotOperations(fixture.Consent, fixture.Data, fixture.Factory);
    await fixture.State("dispatch", "outcome_unknown");
    await fixture.State("accepted", "interrupted");
    Check(fixture.Factory.OpenCount == 0, "restart never redispatches");
    await Error("outcome_unknown", () => fixture.Call("agent_start_session", new { sessionId = "crash-session" }));
}

static async Task AtomicUnknownOutcome()
{
    await using var fixture = new Fixture();
    await fixture.Operations.DisposeAsync();
    var directory = LocalSetup.EnsurePrivateDirectory(fixture.Data);
    using var store = new TaskStore(directory);
    var session = new AgentSession("atomic-session", fixture.Workspace, "peer");
    store.SaveSession(session);
    var now = DateTimeOffset.UtcNow;
    var grant = new TaskGrant(fixture.Workspace, false, false, [], true, 100, 5);
    store.Accept(new("atomic-task", session.SessionId, "key", "fingerprint", "prompt", "accepted", grant, now, now.AddSeconds(100)));
    store.Update("atomic-task", task => task with { State = "running", DispatchStarted = true });
    StoreSql(directory, "CREATE TRIGGER fail_session_update BEFORE UPDATE ON sessions BEGIN SELECT RAISE(ABORT,'injected interruption'); END;");
    var failed = false;
    try { store.Update("atomic-task", task => task with { State = "outcome_unknown" }); }
    catch (SqliteException) { failed = true; }
    Check(failed && store.Task("atomic-task")!.State == "running" &&
        !store.Session(session.SessionId)!.RequiresReconciliation, "transaction rolled back both records");
    StoreSql(directory, "DROP TRIGGER fail_session_update;");
    store.Update("atomic-task", task => task with { State = "outcome_unknown" });
    Check(store.Task("atomic-task")!.State == "outcome_unknown" &&
        store.Session(session.SessionId)!.RequiresReconciliation, "unknown outcome commits its session requirement");
    store.SaveSession(session with { Attached = false });
    Check(store.Session(session.SessionId)!.RequiresReconciliation, "stale detach cannot clear reconciliation");
}

static async Task ReconciliationRecovery()
{
    await using var fixture = new Fixture();
    await fixture.Operations.DisposeAsync();
    var directory = LocalSetup.EnsurePrivateDirectory(fixture.Data);
    using var store = new TaskStore(directory);
    var session = new AgentSession("legacy-session", fixture.Workspace, "peer");
    store.SaveSession(session);
    var now = DateTimeOffset.UtcNow;
    var grant = new TaskGrant(fixture.Workspace, false, false, [], true, 100, 5);
    store.Accept(new("legacy-task", session.SessionId, "key", "fingerprint", "prompt", "accepted", grant, now, now.AddSeconds(100)));
    store.Update("legacy-task", task => task with { State = "outcome_unknown", DispatchStarted = true });
    StoreSql(directory, "UPDATE sessions SET payload=$payload WHERE id='legacy-session';", JsonSerializer.Serialize(session, Json.Options));
    store.Recover();
    Check(store.Session(session.SessionId)!.RequiresReconciliation, "legacy terminal unknown state repairs the missing session flag");
    StoreSql(directory, "CREATE TRIGGER fail_session_update BEFORE UPDATE ON sessions BEGIN SELECT RAISE(ABORT,'injected interruption'); END;");
    var failed = false;
    try { LocalSetup.ReconcileStoppedManagedSession(fixture.Data, session.SessionId); }
    catch (SqliteException) { failed = true; }
    Check(failed && !store.Task("legacy-task")!.ReconciliationAcknowledged &&
        store.Session(session.SessionId)!.RequiresReconciliation, "acknowledgement failure rolled back both records");
    StoreSql(directory, "DROP TRIGGER fail_session_update;");
    LocalSetup.ReconcileStoppedManagedSession(fixture.Data, session.SessionId);
    store.Recover();
    Check(!store.Session(session.SessionId)!.RequiresReconciliation &&
        store.Task("legacy-task") is { State: "outcome_unknown", ReconciliationAcknowledged: true },
        "explicit acknowledgement survives recovery without changing the historical outcome");
}

static void StoreSql(string directory, string sql, string? payload = null)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(directory, "tasks.db"), Pooling = false
    }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    if (payload is not null) command.Parameters.AddWithValue("$payload", payload);
    command.ExecuteNonQuery();
}

static async Task Retention()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    var id = await fixture.Send(session, "x'; DROP TABLE tasks;--");
    await Until(() => fixture.Runtime.Sends == 1);
    for (int index = 0; index < TaskStore.MaximumEventsPerTask + 10; index++) fixture.Runtime.Emit("tool_summary", $"step {index}");
    fixture.Runtime.Finish("result");
    await fixture.State(id, "completed");
    var page = await fixture.Call("agent_read_events", new { taskId = id, limit = 17 });
    Check(page.GetProperty("events").GetArrayLength() == 17 && page.GetProperty("hasMore").GetBoolean() &&
        page.GetProperty("historyTruncated").GetBoolean(), "bounded paging and retention signal");
    long next = page.GetProperty("nextCursor").GetInt64();
    var second = await fixture.Call("agent_read_events", new { taskId = id, afterCursor = next, limit = 17 });
    Check(second.GetProperty("events")[0].GetProperty("cursor").GetInt64() > next, "stable cursors");
    await Error("invalid_arguments", () => fixture.Call("agent_read_events", new { taskId = id, limit = 201 }));
    await fixture.Operations.DisposeAsync();
    using (var store = new TaskStore(LocalSetup.EnsurePrivateDirectory(fixture.Data)))
    {
        store.Prune(DateTimeOffset.UtcNow.AddDays(32));
        Check(store.Task(id)!.ResultsExpired && store.Task(id)!.Prompt == "" &&
            store.Find(session, "x'; DROP TABLE tasks;--")!.TaskId == id, "key tombstone survives pruning");
        Check(store.ReadEvents(id, 0, 100).Events.Count == 0, "payload retention");
    }
    fixture.Operations = new CopilotOperations(fixture.Consent, fixture.Data, fixture.Factory);
}

static async Task Registration()
{
    await using var fixture = new Fixture();
    await Error("runtime_not_registered", () => fixture.Call("agent_attach_session", new { registrationId = "absent", sessionId = "live" }));
    Register(fixture);
    await Error("invalid_registration", () => fixture.Call("agent_attach_session", new { registrationId = "human", sessionId = "other" }));
    var attached = await fixture.Call("agent_attach_session", new { registrationId = "human", sessionId = "live" });
    Check(attached.GetProperty("kind").GetString() == "live", "explicit registered selection");
    var list = await fixture.Call("agent_list_sessions", new { });
    Check(!list.GetRawText().Contains("127.0.0.1", StringComparison.Ordinal) &&
        !list.GetRawText().Contains(new string('x', 32), StringComparison.Ordinal), "no endpoint or token forwarded");
}

static async Task RegistrationJson()
{
    await using var fixture = new Fixture();
    var schema = LocalSetup.RegistrationInputSchema;
    Check(schema.GetProperty("required").GetArrayLength() == 8 &&
        schema.GetProperty("properties").GetProperty("connectionToken").GetProperty("writeOnly").GetBoolean() &&
        !schema.GetProperty("additionalProperties").GetBoolean(), "strict schema is embedded for packaged UI");
    JsonElement Input(string endpoint = "http://127.0.0.1:41235", int schemaVersion = 1, object? expiry = null) =>
        Json.Element(new
        {
            schemaVersion, registrationId = "human-json", endpoint, connectionToken = new string('x', 32),
            sessionId = "selected-live", workingDirectory = fixture.Workspace,
            runtimeVersion = OfficialSdkRuntimeFactory.RequiredRuntimeVersion, expiresAt = expiry ?? DateTimeOffset.UtcNow.AddHours(1)
        });
    LocalSetup.RegisterInteractiveRuntimeJson(fixture.Data, Input());
    using (var store = new TaskStore(LocalSetup.EnsurePrivateDirectory(fixture.Data)))
    {
        var registration = LocalSetup.ReadRegistration(store, "human-json");
        Check(registration.SessionId == "selected-live" && registration.LogonId == WindowsLogon.Current &&
            !JsonSerializer.Serialize(registration).Contains(new string('x', 32), StringComparison.Ordinal),
            "import binds local identity and stores only protected token");
    }
    foreach (var endpoint in new[] { "http://127.0.0.1", "http://[::1]", "http://192.0.2.1:41235", "http://user@127.0.0.1:41235" })
        await Error("invalid_registration", () =>
        {
            LocalSetup.RegisterInteractiveRuntimeJson(fixture.Data, Input(endpoint));
            return Task.CompletedTask;
        });
    await Error("invalid_registration", () =>
    {
        LocalSetup.RegisterInteractiveRuntimeJson(fixture.Data, Input(schemaVersion: 2));
        return Task.CompletedTask;
    });
    await Error("invalid_registration", () =>
    {
        LocalSetup.RegisterInteractiveRuntimeJson(fixture.Data, Input(expiry: DateTimeOffset.UtcNow.AddDays(2)));
        return Task.CompletedTask;
    });
    await Error("invalid_registration", () =>
    {
        LocalSetup.RegisterInteractiveRuntimeJson(fixture.Data,
            Input(expiry: DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
        return Task.CompletedTask;
    });
    var spoofed = Json.Element(new
    {
        schemaVersion = 1, registrationId = "spoof", endpoint = "http://127.0.0.1:41235",
        connectionToken = new string('x', 32), sessionId = "spoof", workingDirectory = fixture.Workspace,
        runtimeVersion = "1.0.79", expiresAt = DateTimeOffset.UtcNow.AddHours(1), userSid = "not-the-current-user"
    });
    await Error("invalid_arguments", () =>
    {
        LocalSetup.RegisterInteractiveRuntimeJson(fixture.Data, spoofed);
        return Task.CompletedTask;
    });
    await Error("unknown_method", () => fixture.Call("agent_register_runtime", Input()));
    LocalSetup.RegisterInteractiveRuntimeJson(fixture.Data, Input(endpoint: "http://[::1]:41235"));
}

static async Task RegistrationLogon()
{
    await using var fixture = new Fixture();
    Register(fixture);
    using var store = new TaskStore(LocalSetup.EnsurePrivateDirectory(fixture.Data));
    var registration = LocalSetup.ReadRegistration(store, "human");
    Check(registration.LogonId == WindowsLogon.Current &&
        registration.LogonId.StartsWith("S-1-5-5-", StringComparison.Ordinal), "shared raw TokenGroups logon SID");
    string otherLogon = registration.LogonId == "S-1-5-5-0-12345" ? "S-1-5-5-0-12346" : "S-1-5-5-0-12345";
    foreach (var stale in new[] { "00000000:00000001", otherLogon })
    {
        store.SetSetting("live:human", registration with { LogonId = stale });
        await Error("invalid_registration", () =>
        {
            LocalSetup.ReadRegistration(store, "human");
            return Task.CompletedTask;
        });
    }
    store.SetSetting("live:human", registration);
    Check(LocalSetup.ReadRegistration(store, "human").LogonId == WindowsLogon.Current, "matching local registration remains valid");
    Check(!((IConsentService)fixture.Consent).ObservationAllowed, "default interface observation permission remains denied");
}

static async Task LiveOwnership()
{
    await using var fixture = new Fixture();
    Register(fixture);
    await fixture.Call("agent_attach_session", new { registrationId = "human", sessionId = "live" });
    fixture.Runtime.ExternalBusy = true;
    await Error("session_busy", () => fixture.Send("live", "live"));
    fixture.Runtime.ExternalBusy = false;
    await fixture.Call("agent_detach_session", new { sessionId = "live" });
    Check(fixture.Runtime.Disposals == 1 && fixture.Runtime.Aborts == 0 && fixture.Runtime.Sends == 0 && fixture.Runtime.ExternalStops == 0,
        "detach preserves external CLI and unrelated turns");
}

static async Task LiveCompatibility()
{
    await using var fixture = new Fixture();
    Register(fixture);
    using var store = new TaskStore(LocalSetup.EnsurePrivateDirectory(fixture.Data));
    var registration = LocalSetup.ReadRegistration(store, "human");
    var factory = new OfficialSdkRuntimeFactory(fixture.Data);
    await Error("incompatible_runtime", async () =>
    {
        await factory.AttachAsync(registration, new((_, _) => Task.FromResult(false), (_, _) => Task.FromResult(""), _ => { }), CancellationToken.None);
    });
}

static async Task Policy()
{
    await using var fixture = new Fixture();
    await Error("invalid_path", () => fixture.Call("agent_start_session", new { workingDirectory = "relative" }));
    await Error("invalid_path", () => fixture.Call("agent_start_session", new { workingDirectory = fixture.Root }));
    var session = await fixture.Start();
    await Error("invalid_arguments", () => fixture.Call("agent_send",
        new { sessionId = session, prompt = "x", idempotencyKey = "x", timeoutSeconds = 10, approvedCommands = new[] { "dotnet build; Remove-Item C:\\*" } }));
    var grant = new TaskGrant(fixture.Workspace, true, true, ["dotnet build"], true, 10, 5);
    var privateDirectory = LocalSetup.EnsurePrivateDirectory(fixture.Data);
    Check(!PathPolicy.Preauthorized(new("read", "", Path.Combine(fixture.Workspace, "..", "outside")), grant, privateDirectory), "parent escape denied");
    Check(!PathPolicy.Preauthorized(new("read", "", fixture.Workspace + "-sibling\\file"), grant, privateDirectory), "prefix sibling denied");
    Check(!PathPolicy.Preauthorized(new("read", "", fixture.Workspace + "\\file:secret"), grant, privateDirectory), "ADS denied");
    Check(!PathPolicy.Preauthorized(new("shell", "", Command: "dotnet build", PossiblePaths: [], HasUrls: true), grant, privateDirectory), "network not preauthorized");
    Check(!PathPolicy.Preauthorized(new("write", "", Path.Combine(fixture.Workspace, "a"), SandboxBypass: true), grant, privateDirectory), "sandbox bypass never preapproved");
    Check(!PathPolicy.Preauthorized(new("desktop", "", RequiresDesktop: true), grant, privateDirectory), "desktop never preauthorized");
}

static async Task ConsentAndOwnership()
{
    await using var fixture = new Fixture();
    fixture.Consent.Allow = false;
    await Error("consent_denied", () => fixture.Start());
    Check(fixture.Factory.OpenCount == 0, "denial before runtime");
    fixture.Consent.Allow = true;
    var session = await fixture.Start();
    var id = await fixture.Send(session, "private");
    await Error("task_not_found", () => fixture.Call("agent_get_task", new { taskId = id }, owner: "other"));
    await Error("session_not_found", () => fixture.Call("agent_send", fixture.SendArguments(session, "other"), owner: "other"));
    await Until(() => fixture.Runtime.Sends == 1);
    fixture.Runtime.Finish();
    await fixture.State(id, "completed");
}

static async Task StoreLease()
{
    await using var fixture = new Fixture();
    try
    {
        await using var duplicate = new CopilotOperations(fixture.Consent, fixture.Data, fixture.Factory);
        throw new Exception("Expected exclusive store lease failure.");
    }
    catch (AgentLinkException error) when (error.Code == "broker_already_running") { }
}

static async Task Resume()
{
    await using var fixture = new Fixture();
    var session = await fixture.Start();
    await fixture.Call("agent_detach_session", new { sessionId = session });
    await fixture.Call("agent_start_session", new { sessionId = session });
    Check(fixture.Factory.ResumeCount == 1 && fixture.Consent.Requests.Count == 2, "authorized SDK resume");
    await Error("invalid_path", () => fixture.Call("agent_start_session", new { sessionId = session, workingDirectory = fixture.Data }));
}

static async Task RuntimeConfiguration()
{
    await using var fixture = new Fixture();
    await fixture.Operations.DisposeAsync();
    fixture.Operations = new CopilotOperations(fixture.Consent, fixture.Data);
    await Error("runtime_not_configured", () => fixture.Start());
}

static async Task ToolSchemas()
{
    await using var fixture = new Fixture();
    Check(fixture.Operations.Tools.Count == 9 && fixture.Operations.Tools.All(tool => tool.Capability == "agent-delegation"), "tool capability");
    await Error("invalid_arguments", () => fixture.Call("agent_attach_session",
        new { registrationId = "human", sessionId = "live", endpoint = "http://127.0.0.1:1", connectionToken = "secret" }));
    var schema = fixture.Operations.Tools.Single(tool => tool.Name == "agent_answer_question").InputSchema.GetRawText();
    Check(!schema.Contains("permission", StringComparison.Ordinal), "no permission input");
}

static void Register(Fixture fixture) => LocalSetup.RegisterInteractiveRuntime(
    fixture.Data, "human", "http://127.0.0.1:41235", new string('x', 32),
    "live", fixture.Workspace, "compatible-test-runtime", DateTimeOffset.UtcNow.AddHours(1));

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task Error(string code, Func<Task> action)
{
    try { await action(); }
    catch (AgentLinkException error) when (error.Code == code) { return; }
    throw new Exception($"Expected {code}.");
}

static async Task Until(Func<bool> condition)
{
    var timer = Stopwatch.StartNew();
    while (!condition())
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Expected offline condition.");
        await Task.Delay(10);
    }
}

sealed class Fixture : IAsyncDisposable
{
    internal Fixture()
    {
        Root = Path.Combine(Directory.GetCurrentDirectory(), "tests", "Copilot.Tests", ".test-runs", Guid.NewGuid().ToString("N"));
        Data = Path.Combine(Root, "data");
        Workspace = Path.Combine(Root, "workspace");
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Workspace);
        Operations = new CopilotOperations(Consent, Data, Factory);
    }
    internal string Root { get; }
    internal string Data { get; }
    internal string Workspace { get; }
    internal FakeConsent Consent { get; } = new();
    internal FakeFactory Factory { get; } = new();
    internal CopilotOperations Operations { get; set; }
    internal FakeRuntime Runtime => Factory.Latest!;

    internal async Task<JsonElement> Call(string method, object arguments, CancellationToken ct = default, string owner = "peer") =>
        (await Operations.InvokeAsync(new(method, Json.Element(arguments), owner), ct)).Data!.Value;
    internal async Task<string> Start() =>
        (await Call("agent_start_session", new { workingDirectory = Workspace })).GetProperty("sessionId").GetString()!;
    internal object SendArguments(string sessionId, string key, bool unattended = false) =>
        new { sessionId, prompt = "Build trusted workspace", idempotencyKey = key, timeoutSeconds = 20, unattended,
            maxAiCredits = unattended ? (double?)5 : null, allowWorkspaceRead = true, allowWorkspaceWrite = true,
            approvedCommands = new[] { "dotnet build" } };
    internal async Task<string> Send(string sessionId, string key, bool unattended = false) =>
        (await Call("agent_send", SendArguments(sessionId, key, unattended))).GetProperty("taskId").GetString()!;
    internal void Connection(bool value)
    {
        Consent.IsConnected = value;
        Operations.OnConnectionChanged(value);
    }
    internal async Task<JsonElement> State(string id, string expected)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var value = await Call("agent_get_task", new { taskId = id });
            if (value.GetProperty("state").GetString() == expected) return value;
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"Expected {expected}, got {value}");
            await Task.Delay(10);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await Operations.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

sealed class FakeConsent : IConsentService
{
    public bool IsConnected { get; set; } = true;
    public bool DeviceAccessEnabled { get; private set; } = true;
    private CancellationTokenSource _deviceEpoch = new();
    public CancellationToken DeviceAccessLifetime => DeviceAccessEnabled ? _deviceEpoch.Token :
        throw new AgentLinkException("device_access_disabled", "Synthetic device access is disabled.");
    internal void SetDeviceAccess(bool enabled)
    {
        DeviceAccessEnabled = enabled;
        if (!enabled)
        {
            var old = _deviceEpoch;
            _deviceEpoch = new();
            old.Cancel();
            old.Dispose();
        }
    }
    public bool DesktopAllowed { get; set; }
    internal bool Allow = true;
    internal Func<ConsentRequest, CancellationToken, Task<bool>>? Handler;
    internal ConcurrentQueue<ConsentRequest> Requests { get; } = new();
    public Task<bool> RequestAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Enqueue(request);
        return Handler?.Invoke(request, cancellationToken) ?? Task.FromResult(Allow);
    }
    public void RevokeDesktop() => DesktopAllowed = false;
}

sealed class FakeFactory : ICopilotRuntimeFactory
{
    internal FakeRuntime? Latest;
    internal int OpenCount;
    internal int ResumeCount;
    public Task<IAgentRuntimeSession> OpenManagedAsync(AgentSession session, bool resume, RuntimeCallbacks callbacks, CancellationToken cancellationToken)
    {
        OpenCount++;
        if (resume) ResumeCount++;
        Latest = new(callbacks, false);
        return Task.FromResult<IAgentRuntimeSession>(Latest);
    }
    public Task<IAgentRuntimeSession> AttachAsync(LiveRuntimeRegistration registration, RuntimeCallbacks callbacks, CancellationToken cancellationToken)
    {
        Latest = new(callbacks, true);
        return Task.FromResult<IAgentRuntimeSession>(Latest);
    }
}

sealed class FakeRuntime(RuntimeCallbacks callbacks, bool external) : IAgentRuntimeSession
{
    public bool IsExternal => external;
    internal int Sends;
    internal int Aborts;
    internal int Disposals;
    internal int ExternalStops => 0;
    internal string? LastAbort;
    internal bool FailAfterDispatch;
    internal bool ExternalBusy;
    internal double? PreparedBudget;
    private bool _busy;
    public Task<bool> IsIdleAsync(CancellationToken cancellationToken) => Task.FromResult(!_busy && !ExternalBusy);
    public Task PrepareAsync(double? maxAiCredits, CancellationToken cancellationToken)
    {
        PreparedBudget = maxAiCredits;
        return Task.CompletedTask;
    }
    public Task<string> SendAsync(string prompt, CancellationToken cancellationToken)
    {
        _busy = true;
        Interlocked.Increment(ref Sends);
        if (FailAfterDispatch) throw new IOException("Simulated connection loss after dispatch before message ID.");
        return Task.FromResult($"message-{Sends}");
    }
    public Task<bool> AbortOwnedTurnAsync(string messageId, CancellationToken cancellationToken)
    {
        if (messageId != $"message-{Sends}") return Task.FromResult(false);
        LastAbort = messageId;
        Interlocked.Increment(ref Aborts);
        _busy = false;
        return Task.FromResult(true);
    }
    internal Task<bool> Permission(RuntimePermission permission) => callbacks.Permission(permission, CancellationToken.None);
    internal Task<string> Question(RuntimeQuestion question) => callbacks.Question(question, CancellationToken.None);
    internal void Emit(string kind, string text) => callbacks.Event(new(kind, text));
    internal void Usage(string id, double amount) => callbacks.Event(new("usage", EventId: id, NanoAiu: amount));
    internal void BudgetExhausted(double credits) => callbacks.Event(new("budget_exhausted", UsedAiCredits: credits));
    internal void Finish(string result = "done")
    {
        _busy = false;
        Emit("final", result);
        Emit("idle", "");
    }
    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}
