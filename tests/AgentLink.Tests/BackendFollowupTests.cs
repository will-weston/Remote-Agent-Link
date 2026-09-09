using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Text;
using System.Text.Json;
using AgentLink.Broker;
using AgentLink.Core;
using AgentLink.Copilot;
using Microsoft.Data.Sqlite;

internal static partial class BackendTests
{
    private static async Task RunFollowupAsync(Func<string, Func<Task>, Task> test)
    {
        await test("incoming hello cannot use stale trust before revocation notification", async () =>
        {
            using var notification = new DelayedTrustNotification();
            await using var fixture = new Fixture(notification.Subscribe);
            using var certificate = Certificate();
            var fingerprint = EndpointIdentity.FingerprintOf(certificate);
            fixture.Identity.Trust(fingerprint);
            var connectedStates = 0;
            fixture.Host.StatusChanged += () =>
            {
                if (Connected(fixture.Host))
                    Interlocked.Increment(ref connectedStates);
            };
            await using var peer = await IncomingPeer.Connect(fixture, certificate);
            await Wait(() => fixture.Host.PeerConnections is [{ TrustedLocally: true, SessionId: 0 }]);
            var revocation = notification.RevokeAsync(fixture.Identity, fingerprint);
            try
            {
                await notification.WaitUntilSavedAsync();
                Check(!fixture.Identity.TrustedFingerprints.Contains(fingerprint), "The pin was not removed before notification.");
                await ThrowsAny(() => peer.Peer.CallAsync(new("protocol.hello", Json.Element(Hello(true))), default));
                await Wait(() => fixture.Host.PeerConnections.Count == 0);
                Check(connectedStates == 0 && fixture.Host.Connections.Count == 0 && !fixture.Consent.IsConnected,
                    "The stale TLS trust snapshot activated an incoming channel.");
            }
            finally
            {
                notification.Release();
                await revocation;
            }
        });
        await test("outgoing hello cannot register stale trust before revocation notification", async () =>
        {
            using var notification = new DelayedTrustNotification();
            await using var fixture = new Fixture(notification.Subscribe);
            using var certificate = Certificate();
            var fingerprint = EndpointIdentity.FingerprintOf(certificate);
            fixture.Identity.Trust(fingerprint);
            var connectedStates = 0;
            fixture.Host.StatusChanged += () =>
            {
                if (Connected(fixture.Host))
                    Interlocked.Increment(ref connectedStates);
            };
            var helloRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendHello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithLegacyServer(fixture, certificate, async () =>
            {
                await helloRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(fixture.Host.PeerConnections is [{ TrustedLocally: true, SessionId: 0 }],
                    "The test did not capture the initially trusted TLS snapshot.");
                var revocation = notification.RevokeAsync(fixture.Identity, fingerprint);
                try
                {
                    await notification.WaitUntilSavedAsync();
                    sendHello.TrySetResult();
                    await Wait(() => fixture.Host.PeerConnections.Count == 0);
                    Check(connectedStates == 0 && fixture.Host.Connections.Count == 0 && !fixture.Consent.IsConnected,
                        "The stale TLS trust snapshot registered an outgoing link.");
                }
                finally
                {
                    sendHello.TrySetResult();
                    notification.Release();
                    await revocation;
                }
            }, async () =>
            {
                helloRequested.TrySetResult();
                await sendHello.Task.WaitAsync(TimeSpan.FromSeconds(12));
            });
        });
        await test("current trust loss disables readiness even when a peer was already ready", async () =>
        {
            using var certificate = Certificate();
            foreach (var direction in new[] { PeerDirection.RemoteDesktop, PeerDirection.IncomingAgent })
            {
                using var channel = new PeerChannel(direction, default);
                channel.Authenticate(certificate, true);
                channel.AcceptHello(Hello(true));
                Check(channel.Ready, "The trusted test channel was not ready.");
                await ThrowsCode("pairing_required", () =>
                {
                    channel.ReconcileLocalTrust(false);
                    return Task.CompletedTask;
                });
                Check(!channel.Ready && !channel.Snapshot.TrustedLocally, "Current pin removal retained cached readiness.");
                await ThrowsCode("pairing_required", () =>
                {
                    channel.ReconcileLocalTrust(true);
                    return Task.CompletedTask;
                });
                Check(!channel.Ready, "Re-pinning resurrected the old channel without a new handshake.");
            }
        });
        await test("observer failures are bounded reported and isolated including ui.show delivery", async () =>
        {
            using var diagnostics = new DiagnosticListener();
            var originalError = Console.Error;
            using var errors = new StringWriter();
            const string privatePayload = "DO-NOT-LOG-PROMPT-OR-CREDENTIAL";
            Console.SetError(errors);
            try
            {
                await using var fixture = new Fixture();
                var laterStatus = 0;
                var laterConsent = 0;
                var laterTrust = 0;
                var laterLog = 0;
                var laterShow = 0;
                Exception Failure() => new InvalidOperationException(privatePayload + new string('x', 10000));
                fixture.Host.Log += _ => throw Failure();
                fixture.Host.Log += _ => laterLog++;
                fixture.Host.Start();
                await ThrowsCode("ui_unavailable", () => fixture.Client.ShowAsync());
                fixture.Host.StatusChanged += () => throw Failure();
                fixture.Host.StatusChanged += () => laterStatus++;
                fixture.Consent.Changed += () => throw Failure();
                fixture.Consent.Changed += () => laterConsent++;
                fixture.Identity.TrustChanged += () => throw Failure();
                fixture.Identity.TrustChanged += () => laterTrust++;
                fixture.Host.ShowRequested += () => throw Failure();
                fixture.Host.ShowRequested += () => laterShow++;
                await ThrowsCode("ui_unavailable", () => fixture.Client.ShowAsync());
                fixture.Identity.Trust(new string('A', 64));
                fixture.Identity.Revoke(new string('A', 64));
                await Wait(() => laterLog > 0);
                Check(laterStatus > 0 && laterConsent > 0 && laterTrust == 2 && laterShow == 1 &&
                    fixture.Identity.TrustedFingerprints.Count == 0 && !fixture.Consent.IsConnected,
                    "A failed observer skipped a later observer or interrupted revocation.");
            }
            finally { Console.SetError(originalError); }
            var events = diagnostics.Events.Where(item => item.Id == 1).ToArray();
            foreach (var observer in Enum.GetNames<BrokerObserver>())
                Check(events.Any(item => item.Fields is [var source, "InvalidOperationException"] && source == observer),
                    $"No bounded diagnostic was reported for {observer}.");
            Check(events.All(item => item.Fields.All(field => field.Length <= 64 && !field.Any(char.IsControl))) &&
                !errors.ToString().Contains(privatePayload, StringComparison.Ordinal) &&
                errors.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).All(line => line.Length <= 192),
                "Observer diagnostics leaked payloads or exceeded their bounds.");
        });
        await test("failed stderr falls back to ETW without interrupting trust observers", async () =>
        {
            using var diagnostics = new DiagnosticListener();
            await using var fixture = new Fixture();
            fixture.Identity.Trust(new string('A', 64));
            var notified = false;
            fixture.Identity.TrustChanged += () => throw new InvalidOperationException("private failure details");
            fixture.Identity.TrustChanged += () => notified = true;
            var originalError = Console.Error;
            try
            {
                Console.SetError(new FailingDiagnosticWriter());
                fixture.Identity.Revoke(new string('A', 64));
            }
            finally { Console.SetError(originalError); }
            Check(notified && fixture.Identity.TrustedFingerprints.Count == 0 &&
                diagnostics.Events.Any(item => item.Id == 1 && item.Fields is ["TrustChanged", "InvalidOperationException"]) &&
                diagnostics.Events.Any(item => item.Id == 2 && item.Fields is ["IOException"]),
                "The independent diagnostic sink did not report the failure or revocation was interrupted.");
        });
        await test("authenticated current-session metadata stays available offline without desktop permission", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            await CheckLocalOnlyTargets(fixture);
            var status = await fixture.Client.CallAsync("get", new TargetRequest(null, true), default);
            Check(!status.Data!.Value.GetProperty("connected").GetBoolean(), "Offline metadata falsely reported a connection.");
            await Throws<AgentLinkException>(() => LocalInvoke(fixture, "get_screen_size", new { }));
            await ThrowsCode("disconnected", () => LocalInvoke(fixture, "agent_start_session", new { workingDirectory = fixture.Workspace }));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Throws<OperationCanceledException>(() => LocalInvoke(fixture, "desktop.acquire", new { }, cancellation.Token));
            Check(!fixture.Consent.DesktopAllowed && !fixture.Consent.ObservationAllowed && !fixture.Consent.Requests.TryRead(out _) &&
                fixture.Host.ListLocalTasks(false).Count == 0, "Local offline access implicitly approved observation, control or agent work.");
        });
        await test("durable local agent queries and cancellation work offline and while remote pairing is pending", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            var owner = $"local:{WindowsLogon.Current}";
            var completed = SeedTask(fixture, owner, "completed");
            var queued = SeedTask(fixture, owner, "waiting_for_connection");
            var foreign = SeedTask(fixture, "remote:" + new string('B', 64), "completed");
            var sessions = await LocalInvoke(fixture, "agent_list_sessions", new { });
            Check(sessions.Data!.Value.GetProperty("sessions").GetArrayLength() == 2, "Offline session listing crossed agent ownership.");
            var read = await LocalInvoke(fixture, "agent_get_task", new { taskId = completed.TaskId });
            Check(read.Data!.Value.GetProperty("result").GetString() == "durable result", "Offline durable result retrieval failed.");
            var cancelled = await LocalInvoke(fixture, "agent_cancel_task", new { taskId = queued.TaskId });
            Check(cancelled.Data!.Value.GetProperty("cancellationRequested").GetBoolean() &&
                fixture.Host.GetLocalTask(queued.TaskId).CancellationRequested, "Offline cancellation was not persisted.");
            var events = await LocalInvoke(fixture, "agent_read_events", new { taskId = queued.TaskId, afterCursor = 0, limit = 10 });
            Check(events.Data!.Value.GetProperty("events").GetArrayLength() > 0, "Offline task event retrieval failed.");
            await ThrowsCode("task_not_found", () => LocalInvoke(fixture, "agent_get_task", new { taskId = foreign.TaskId }));
            await ThrowsCode("task_not_found", () => LocalInvoke(fixture, "agent_cancel_task", new { taskId = foreign.TaskId }));

            await using var peer = await IncomingPeer.Connect(fixture);
            await peer.Peer.CallAsync(new("protocol.hello", Json.Element(Hello(true))), default);
            await Wait(() => Pending(fixture.Host));
            var pendingTask = SeedTask(fixture, owner, "waiting_for_connection");
            Check((await LocalInvoke(fixture, "agent_get_task", new { taskId = completed.TaskId })).Data!.Value
                .GetProperty("result").GetString() == "durable result", "Pending pairing blocked local durable reads.");
            await LocalInvoke(fixture, "agent_cancel_task", new { taskId = pendingTask.TaskId });
            Check(fixture.Host.GetLocalTask(pendingTask.TaskId).CancellationRequested &&
                !fixture.Consent.IsConnected && !fixture.Consent.DesktopAllowed && !fixture.Consent.ObservationAllowed,
                "Local cancellation was blocked or pending pairing enabled consent.");
            await ThrowsAny(() => peer.Peer.CallAsync(new("agent_get_task", Json.Element(new { taskId = completed.TaskId }), "claimed-agent"), default));
        });
    }

    private static Task<OperationResult> LocalInvoke(Fixture fixture, string method, object arguments,
        CancellationToken cancellationToken = default) =>
        fixture.Client.CallAsync("invoke", new RoutedOperation(null, true,
            new(method, Json.Element(arguments), Guid.NewGuid().ToString("N"))), cancellationToken);

    private static async Task ThrowsCode(string code, Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (AgentLinkException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException($"Expected AgentLinkException with code {code}.");
    }

    private static AgentTask SeedTask(Fixture fixture, string owner, string state)
    {
        var session = new AgentSession(Guid.NewGuid().ToString("N"), fixture.Workspace, owner, Attached: false);
        var task = new AgentTask(Guid.NewGuid().ToString("N"), session.SessionId, Guid.NewGuid().ToString("N"),
            "fixture-fingerprint", "fixture prompt", state, new(fixture.Workspace, false, false, [], false, 60, null),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), Result: state == "completed" ? "durable result" : null);
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.DataDirectory, "copilot", "tasks.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        database.Open();
        using var transaction = database.BeginTransaction();
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions(id,owner,payload) VALUES($session,$owner,$sessionPayload);
            INSERT INTO tasks(id,session_id,idempotency_key,active,payload) VALUES($task,$session,$key,$active,$taskPayload);
            """;
        command.Parameters.AddWithValue("$session", session.SessionId);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$sessionPayload", JsonSerializer.Serialize(session, Json.Options));
        command.Parameters.AddWithValue("$task", task.TaskId);
        command.Parameters.AddWithValue("$key", task.IdempotencyKey);
        command.Parameters.AddWithValue("$active", state == "completed" ? 0 : 1);
        command.Parameters.AddWithValue("$taskPayload", JsonSerializer.Serialize(task, Json.Options));
        command.ExecuteNonQuery();
        transaction.Commit();
        return task;
    }

    private sealed class DelayedTrustNotification : IDisposable
    {
        private readonly ManualResetEventSlim _saved = new();
        private readonly ManualResetEventSlim _release = new();
        private int _enabled;

        internal void Subscribe(EndpointIdentity identity) => identity.TrustChanged += () =>
        {
            if (Volatile.Read(ref _enabled) == 0)
                return;
            _saved.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(12)))
                throw new TimeoutException("The delayed revocation observer was not released.");
        };

        internal Task RevokeAsync(EndpointIdentity identity, string fingerprint)
        {
            Volatile.Write(ref _enabled, 1);
            return Task.Run(() => identity.Revoke(fingerprint));
        }

        internal async Task WaitUntilSavedAsync() =>
            Check(await Task.Run(() => _saved.Wait(TimeSpan.FromSeconds(5))), "Revocation did not reach the notification barrier.");

        internal void Release() => _release.Set();
        public void Dispose()
        {
            _release.Set();
            _saved.Dispose();
            _release.Dispose();
        }
    }

    private sealed class DiagnosticListener : EventListener
    {
        private readonly ConcurrentQueue<(int Id, string[] Fields)> _events = new();
        internal IReadOnlyList<(int Id, string[] Fields)> Events => _events.ToArray();
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "RemoteAgentLink-Broker")
                EnableEvents(eventSource, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId is 1 or 2)
                _events.Enqueue((eventData.EventId, eventData.Payload?.Select(value => value?.ToString() ?? "").ToArray() ?? []));
        }
    }

    private sealed class FailingDiagnosticWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("Unusable diagnostic output.");
    }
}
