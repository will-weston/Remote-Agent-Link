using System.Reflection;
using System.Text.Json;
using AgentLink.Broker;
using AgentLink.Broker.Shell;
using AgentLink.Core;
using AgentLink.Copilot;

internal static partial class BackendTests
{
    internal static async Task RunDesktopAvailabilityAsync(Func<string, Func<Task>, Task> test)
    {
        await test("device access defaults on and automatically permits observation and scoped coding", async () =>
        {
            var consent = new HumanConsent();
            OperationContext.ActorId.Value = "agent";
            Check(consent.DeviceAccessEnabled && !consent.ObservationAllowed, "Unauthenticated context was admitted.");
            await ThrowsCode("disconnected", () => consent.RequestAsync(new("observe", "test"), default));
            consent.SetConnected(true);
            foreach (var capability in new[] { "observe", "agent-delegation", "unattended-execution" })
                Check(await consent.RequestAsync(new(capability, "test"), default), "Enabled capability required approval.");
            Check(consent.ObservationAllowed && !consent.HasDesktopLease && !consent.Requests.TryRead(out _),
                "Observation acquired input or populated an approval queue.");
        });
        await test("device off revokes all access and epochs without replay or input preemption", async () =>
        {
            var consent = new HumanConsent();
            consent.SetConnected(true);
            OperationContext.ActorId.Value = "first";
            await consent.RequestAsync(new("desktop-control", "test"), default);
            var desktop = consent.DesktopAccessLifetime;
            var device = consent.DeviceAccessLifetime;
            OperationContext.ActorId.Value = "second";
            await ThrowsCode("desktop_busy", () => consent.RequestAsync(new("desktop-control", "test"), default));
            await ThrowsCode("not_control_owner", () => { consent.ReleaseDesktop(); return Task.CompletedTask; });
            consent.SetDesktopAccessEnabled(false);
            Check(desktop.IsCancellationRequested && device.IsCancellationRequested && !consent.HasDesktopLease &&
                !consent.ObservationAllowed && consent.IsConnected, "Off retained access or disconnected transport.");
            foreach (var capability in new[] { "observe", "desktop-control", "agent-delegation", "unattended-execution" })
                await ThrowsCode("device_access_disabled", () => consent.RequestAsync(new(capability, "test"), default));
            consent.SetDesktopAccessEnabled(true);
            Check(device.IsCancellationRequested && desktop.IsCancellationRequested && !consent.HasDesktopLease &&
                !consent.DeviceAccessLifetime.IsCancellationRequested, "Re-enable replayed a revoked epoch or lease.");
            await consent.RequestAsync(new("desktop-control", "fresh"), default);
            Check(consent.DesktopAllowed, "Fresh input acquisition failed.");
            consent.SetPrompting(true);
            Check(!consent.DesktopAllowed && !consent.ObservationAllowed, "Protected dialog guard was bypassed.");
            consent.SetPrompting(false);
            Check(!consent.Requests.TryRead(out _), "An approval was queued.");
        });
        await test("revocation cancels a lock-blocked acquisition without restoring input ownership", async () =>
        {
            foreach (var reconnect in new[] { false, true })
            {
                var consent = new HumanConsent();
                consent.SetConnected(true);
                var lifetime = consent.DesktopAccessLifetime;
                var gate = typeof(HumanConsent).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(consent)!;
                var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var worker = new Thread(() =>
                {
                    OperationContext.ActorId.Value = "stale";
                    try
                    {
                        consent.RequestAsync(new("desktop-control", "stale acquisition"), lifetime).GetAwaiter().GetResult();
                        completed.SetResult(null);
                    }
                    catch (Exception exception) { completed.SetResult(exception); }
                }) { IsBackground = true };
                try
                {
                    lock (gate)
                    {
                        worker.Start();
                        // Hold acquisition at its lock, after its initial cancellation check.
                        Check(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                            TimeSpan.FromSeconds(5)), "Acquisition did not wait on the consent lock.");
                        if (reconnect)
                        {
                            consent.SetConnected(false);
                            consent.SetConnected(true);
                        }
                        else consent.RevokeDesktop();
                    }
                    var failure = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(failure is OperationCanceledException && !consent.HasDesktopLease,
                        "A revoked acquisition restored ownership instead of cancelling.");
                    OperationContext.ActorId.Value = "fresh";
                    Check(await consent.RequestAsync(new("desktop-control", "fresh acquisition"), consent.DesktopAccessLifetime) &&
                        consent.DesktopAllowed, "Revoked acquisition blocked a fresh actor.");
                }
                finally
                {
                    Check(worker.Join(TimeSpan.FromSeconds(5)), "Acquisition worker did not stop.");
                }
            }
        });
        await test("off rejects every desktop and coding category locally and remotely while metadata survives", async () =>
        {
            await using var local = new Fixture();
            await using var remote = new Fixture();
            local.Host.Start();
            remote.Host.Start();
            await using var pair = await HostPair.Connect(local.Host, remote.Host);
            await Wait(() => Connected(local.Host) && Connected(remote.Host));
            var binding = local.Host.Connections.Single();
            local.Session.SetDesktopAccessEnabled(false);
            await local.Client.CallAsync("invoke", new RoutedOperation(binding.ConnectionId, false,
                new("desktop.acquire", Json.Element(new { }), Guid.NewGuid().ToString("N"))), default);
            Check(remote.Consent.HasDesktopLease, "Local Off blocked an independently enabled remote endpoint.");
            var epoch = DesktopOperationEpoch(remote.Host);
            remote.Session.SetDesktopAccessEnabled(false);
            Check(epoch.IsCancellationRequested && !remote.Consent.HasDesktopLease, "Off retained input state.");
            foreach (var method in DesktopToolCatalog.All.Concat(AgentToolCatalog.All).Select(tool => tool.Name)
                         .Concat(["desktop.acquire", "desktop.release"]))
            {
                await ThrowsCode("device_access_disabled", () => LocalInvoke(remote, method, new { }));
                await ThrowsCode("device_access_disabled", () => local.Client.CallAsync("invoke",
                    new RoutedOperation(binding.ConnectionId, false,
                        new(method, Json.Element(new { }), Guid.NewGuid().ToString("N"))), default));
            }
            Check((await local.Client.CallAsync("describe", new TargetRequest(binding.ConnectionId), default))
                .Data!.Value.GetArrayLength() == DesktopToolCatalog.All.Count + AgentToolCatalog.All.Count,
                "Off hid or changed the backend catalog.");
            Check(local.Host.Connections.Single() == binding && Connected(remote.Host) && remote.Consent.IsConnected,
                "Off disconnected or retargeted the RDP channel.");
            Check(remote.Host.ListLocalTasks(false).Count == 0 && !remote.Consent.Requests.TryRead(out _),
                "Off blocked local human metadata or queued approvals.");
            remote.Session.SetDesktopAccessEnabled(true);
            Check(epoch.IsCancellationRequested && !remote.Consent.HasDesktopLease, "On replayed input.");
        });
        await test("device preference survives restarts and new logon directories", async () =>
        {
            await using var fixture = new Fixture();
            Check(fixture.Consent.DeviceAccessEnabled, "Missing preference did not default to Yes.");
            fixture.Session.SetDesktopAccessEnabled(false);
            Check(!ReadSavedDesktopAccess(fixture.IdentityDirectory), "No was not saved in per-user device storage.");
            await fixture.Session.StopAsync();
            var restored = new HumanConsent();
            await using var restarted = CreatePolicyHost(fixture, restored, fixture.DataDirectory);
            Check(!restored.DeviceAccessEnabled, "Restart ignored No.");
            var another = new HumanConsent();
            await using var nextLogon = CreatePolicyHost(fixture, another, Path.Combine(fixture.DataDirectory, "other-logon"));
            Check(!another.DeviceAccessEnabled, "New logon forgot the device preference.");
            restarted.SetDesktopAccessEnabled(true);
            Check(restored.DeviceAccessEnabled && !restored.HasDesktopLease &&
                ReadSavedDesktopAccess(fixture.IdentityDirectory), "Yes failed to persist or restored a lease.");
        });
        await test("broker switch cancels its accepted unattended coding work without replay", async () =>
        {
            var factory = new PolicyRuntimeFactory();
            await using var fixture = new Fixture(runtimeFactory: factory);
            fixture.Host.Start();
            var session = (await LocalInvoke(fixture, "agent_start_session", new { workingDirectory = fixture.Workspace }))
                .Data!.Value.GetProperty("sessionId").GetString()!;
            var arguments = new { sessionId = session, prompt = "synthetic test", idempotencyKey = "one",
                timeoutSeconds = 30, unattended = true, maxAiCredits = 1 };
            var task = (await LocalInvoke(fixture, "agent_send", arguments)).Data!.Value.GetProperty("taskId").GetString()!;
            await Wait(() => fixture.Host.GetLocalTask(task).MessageId is not null);
            fixture.Session.SetDesktopAccessEnabled(false);
            await Wait(() => fixture.Host.GetLocalTask(task).State is "cancelled" or "outcome_unknown" or "failed");
            var stopped = fixture.Host.GetLocalTask(task);
            Check(stopped.State == "cancelled" && stopped.ErrorCode == "device_access_disabled" &&
                stopped.CancellationRequested && factory.Runtime.Aborts == 1 && !fixture.Consent.HasDesktopLease,
                "Broker No did not request owned coding cancellation.");
            fixture.Session.SetDesktopAccessEnabled(true);
            await LocalInvoke(fixture, "agent_send", arguments);
            Check(factory.Runtime.Sends == 1 && !fixture.Consent.Requests.TryRead(out _),
                "Re-enabling or idempotent retry replayed work or queued approval.");
        });
        await test("running logons reconcile shared No and cancel owned work without disconnecting", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            var consent = new HumanConsent();
            var factory = new PolicyRuntimeFactory();
            await using var other = CreatePolicyHost(fixture, consent, Path.Combine(fixture.DataDirectory, "another-logon"), factory);
            other.Start();
            Task<OperationResult> Invoke(string method, object arguments) => other.HandleLocalRequestAsync(new("invoke",
                Json.Element(new RoutedOperation(null, true, new(method, Json.Element(arguments), Guid.NewGuid().ToString("N"))))),
                "shared-test:1", default);
            var session = (await Invoke("agent_start_session", new { workingDirectory = fixture.Workspace }))
                .Data!.Value.GetProperty("sessionId").GetString()!;
            var task = (await Invoke("agent_send", new { sessionId = session, prompt = "synthetic shared preference",
                idempotencyKey = "shared", timeoutSeconds = 30, unattended = true, maxAiCredits = 1 }))
                .Data!.Value.GetProperty("taskId").GetString()!;
            await Wait(() => other.GetLocalTask(task).MessageId is not null);
            fixture.Session.SetDesktopAccessEnabled(false);
            await ThrowsCode("device_access_disabled", () => Invoke("agent_list_sessions", new { }));
            await Wait(() => other.GetLocalTask(task).State == "cancelled");
            Check(!consent.DeviceAccessEnabled && other.IsRunning && consent.IsConnected &&
                factory.Runtime.Aborts == 1, "A running logon ignored shared No or stopped its broker.");
            fixture.Session.SetDesktopAccessEnabled(true);
            await Wait(() => consent.DeviceAccessEnabled);
            Check(factory.Runtime.Sends == 1 && !consent.HasDesktopLease, "Shared Yes replayed work or input ownership.");
        });
        await test("first device preference safely migrates prior logons and explicit No wins", async () =>
        {
            await using var fixture = new Fixture();
            await fixture.Session.StopAsync();
            File.Delete(Path.Combine(fixture.IdentityDirectory, DesktopAccessSettings.FileName));
            var first = Path.Combine(fixture.IdentityDirectory, "sessions", "old-logon-1");
            var second = Path.Combine(fixture.IdentityDirectory, "sessions", "old-logon-2");
            new DesktopAccessSettings(first).Save(true);
            new DesktopAccessSettings(second).Save(false);
            var restored = new HumanConsent();
            await using var host = CreatePolicyHost(fixture, restored, fixture.DataDirectory);
            Check(!restored.DeviceAccessEnabled && !ReadSavedDesktopAccess(fixture.IdentityDirectory),
                "Migration discarded an explicit old No.");
            host.SetDesktopAccessEnabled(true);
            await host.DisposeAsync();
            var updated = new HumanConsent();
            await using var again = CreatePolicyHost(fixture, updated, fixture.DataDirectory);
            Check(updated.DeviceAccessEnabled && !new DesktopAccessSettings(second).Read(),
                "Old preferences overrode a later explicit device choice or were destructively rewritten.");
        });
        await test("settings fail closed for corrupt unreadable and invalid legacy preferences", async () =>
        {
            await using var fixture = new Fixture();
            await fixture.Session.StopAsync();
            var path = Path.Combine(fixture.IdentityDirectory, DesktopAccessSettings.FileName);
            foreach (var malformed in new[]
            {
                "", "null", "{}", "[]", """{"version":1}""", """{"enabled":true}""",
                """{"version":2,"enabled":true}""", """{"version":1,"enabled":"true"}""",
                """{"version":1,"enabled":null}""", """{"version":1,"enabled":false,"enabled":true}""",
                """{"version":1,"version":1,"enabled":true}""", """{"version":1,"enabled":true,"extra":1}""",
                """{"Version":1,"enabled":true}""", new string(' ', DesktopAccessSettings.MaximumBytes + 1)
            })
            {
                File.WriteAllText(path, malformed);
                var consent = new HumanConsent();
                await ThrowsCode("desktop_access_settings_invalid", async () =>
                {
                    await using var unexpected = CreatePolicyHost(fixture, consent, fixture.DataDirectory);
                });
                Check(!consent.DeviceAccessEnabled && !consent.IsConnected, "Malformed preferences enabled access.");
            }
            File.WriteAllText(path, """{"version":1,"enabled":false}""");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var consent = new HumanConsent();
                await ThrowsCode("desktop_access_settings_unavailable", async () =>
                {
                    await using var unexpected = CreatePolicyHost(fixture, consent, fixture.DataDirectory);
                });
                Check(!consent.DeviceAccessEnabled, "Unreadable preference enabled access.");
            }
            File.Delete(path);
            File.WriteAllText(Path.Combine(fixture.DataDirectory, DesktopAccessSettings.FileName), "malformed legacy");
            await ThrowsCode("desktop_access_settings_invalid", async () =>
            {
                await using var unexpected = CreatePolicyHost(fixture, new(), fixture.DataDirectory);
            });
        });
        await test("failed persistence keeps No effective and cannot enable an unsaved Yes", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            fixture.Session.SetDesktopAccessEnabled(true);
            OperationContext.ActorId.Value = "save-failure";
            await fixture.Consent.RequestAsync(new("desktop-control", "test"), default);
            var path = Path.Combine(fixture.IdentityDirectory, DesktopAccessSettings.FileName);
            var epoch = DesktopOperationEpoch(fixture.Host);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await ThrowsCode("desktop_access_persistence_failed", () =>
                {
                    fixture.Session.SetDesktopAccessEnabled(false);
                    return Task.CompletedTask;
                });
                Check(!fixture.Consent.DeviceAccessEnabled && !fixture.Consent.HasDesktopLease && epoch.IsCancellationRequested,
                    "Failed save rolled back No.");
            }
            Check(ReadSavedDesktopAccess(fixture.IdentityDirectory), "Failed save changed the original durable value.");
            await ThrowsCode("device_access_disabled", () => LocalInvoke(fixture, "agent_list_sessions", new { }));
            Check(!fixture.Consent.DeviceAccessEnabled, "Refreshing the old saved Yes undid a failed-save No.");
            fixture.Session.SetDesktopAccessEnabled(false);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await ThrowsCode("desktop_access_persistence_failed", () =>
                {
                    fixture.Session.SetDesktopAccessEnabled(true);
                    return Task.CompletedTask;
                });
                Check(!fixture.Consent.DeviceAccessEnabled, "Unsaved Yes enabled access.");
            }
            Check(!ReadSavedDesktopAccess(fixture.IdentityDirectory) &&
                !Directory.EnumerateFiles(fixture.IdentityDirectory, "*.pending").Any(), "Failed save left stale staging files.");
        });
        await test("Stop and Quit preserve preference and reentrant observers see committed settings", async () =>
        {
            await using var fixture = new Fixture();
            var reenabled = false;
            fixture.Consent.Changed += () =>
            {
                if (reenabled || fixture.Consent.DeviceAccessEnabled) return;
                Check(!ReadSavedDesktopAccess(fixture.IdentityDirectory), "No was announced before its save.");
                reenabled = true;
                fixture.Session.SetDesktopAccessEnabled(true);
            };
            fixture.Session.SetDesktopAccessEnabled(false);
            Check(reenabled && fixture.Consent.DeviceAccessEnabled && ReadSavedDesktopAccess(fixture.IdentityDirectory),
                "A reentrant observer split saved and effective preference.");
            fixture.Host.StopDesktopControl();
            await fixture.Session.StopAsync();
            Check(ReadSavedDesktopAccess(fixture.IdentityDirectory), "Stop or Quit changed the preference.");
            await ThrowsCode("broker_stopping", () => { fixture.Session.SetDesktopAccessEnabled(false); return Task.CompletedTask; });
        });
        await test("presence heartbeat TTL and channel disconnect remove only matching agents", () =>
        {
            var clock = new PresenceClock();
            var registry = new AgentPresenceRegistry(clock);
            registry.Update("local:a", "This computer", null, true);
            registry.Update("remote:a", "Other computer", "channel-a", true);
            registry.Update("remote:b", "Other computer", "channel-b", true);
            clock.Advance(TimeSpan.FromSeconds(44));
            Check(registry.Snapshot().Count == 3, "A current heartbeat expired early.");
            registry.Update("local:a", "This computer", null, true);
            registry.Disconnect("channel-a");
            Check(registry.Snapshot().Count == 2, "Channel disconnect removed unrelated presence.");
            clock.Advance(TimeSpan.FromSeconds(1));
            Check(registry.Snapshot() is [{ Id: "local:a", IsLocal: true }], "Missing heartbeat survived 45 seconds.");
            registry.Update("local:a", "This computer", null, false);
            Check(registry.Snapshot().Count == 0, "Clean exit retained local presence.");
            return Task.CompletedTask;
        });
        await test("channel identities are immutable and peer presence cannot impersonate a local agent", async () =>
        {
            using var certificate = Certificate();
            using var other = Certificate();
            using (var channel = new PeerChannel(PeerDirection.IncomingAgent, default))
            {
                channel.Authenticate(certificate, true);
                await ThrowsCode("identity_changed", () => { channel.Authenticate(other, true); return Task.CompletedTask; });
                Check(channel.Snapshot.Fingerprint == EndpointIdentity.FingerprintOf(certificate),
                    "A channel's authenticated endpoint identity was replaced.");
            }
            await using var fixture = new Fixture();
            await using var peer = await IncomingPeer.Connect(fixture, certificate);
            await peer.Peer.CallAsync(new("protocol.hello", Json.Element(Hello(true))), default);
            var forged = new McpPresenceRequest("local:123:456:administrator", null, false, true);
            await ThrowsCode("invalid_actor", () => peer.Peer.CallAsync(new("mcp.presence",
                Json.Element(forged), "different-actor"), default));
            await peer.Peer.CallAsync(new("mcp.presence", Json.Element(forged), forged.ActorId), default);
            Check(fixture.Host.GetAgentConnections() is [{ IsLocal: false, Id: var remoteId }] &&
                remoteId.StartsWith("remote:", StringComparison.Ordinal) &&
                remoteId.Contains(EndpointIdentity.FingerprintOf(certificate), StringComparison.Ordinal),
                "A peer label or forged PID became local authority.");
            Check(!fixture.Consent.HasDesktopLease && !fixture.Consent.Requests.TryRead(out _), "Presence gained input permission.");
        });
        await test("presence uses caller stamping, never executes operations, and forwards only selected channels", async () =>
        {
            await using var local = new Fixture();
            await using var remote = new Fixture();
            local.Host.Start();
            remote.Host.Start();
            await using var pair = await HostPair.Connect(local.Host, remote.Host);
            await Wait(() => Connected(local.Host) && Connected(remote.Host));
            local.Session.SetDesktopAccessEnabled(false);
            remote.Session.SetDesktopAccessEnabled(false);
            var actor = Guid.NewGuid().ToString("N");
            Task<OperationResult> Presence(McpPresenceRequest request, string caller = "123:456") =>
                local.Host.HandleLocalRequestAsync(new("mcp.presence", Json.Element(request)), caller, default);
            await Presence(new(actor, null, false, true));
            Check(local.Host.GetAgentConnections() is [{ IsLocal: true }] && remote.Host.GetAgentConnections().Count == 0,
                "Unbound presence selected an arbitrary computer.");
            await Presence(new(actor, null, true, true), "999:456");
            Check(local.Host.GetAgentConnections().Count == 2, "Different authenticated callers collided on the same claimed actor.");
            await Presence(new(actor, null, false, false), "999:456");
            Check(local.Host.GetAgentConnections() is [{ Id: var id }] && id.Contains("123:456"),
                "Another caller deleted presence it did not own.");
            var connection = local.Host.Connections.Single().ConnectionId;
            await Presence(new(actor, connection, false, true));
            var received = remote.Host.GetAgentConnections().Single();
            Check(!received.IsLocal && received.Id.StartsWith("remote:", StringComparison.Ordinal) &&
                received.Id.Contains("123:456:" + actor, StringComparison.Ordinal), "Remote presence did not retain authenticated actor scope.");
            await ThrowsCode("invalid_actor", () => Presence(new("spoof:123", connection, false, true)));
            await ThrowsCode("invalid_arguments", () => Presence(new(actor, connection, true, true)));
            await ThrowsCode("disconnected", () => Presence(new(actor, "stale-connection", false, true)));
            Check(!remote.Consent.HasDesktopLease && remote.Host.ListLocalTasks(false).Count == 0 &&
                !remote.Consent.Requests.TryRead(out _), "Presence executed actions or requested approval.");
            await pair.DisposeAsync();
            await Wait(() => remote.Host.GetAgentConnections().Count == 0);
            Check(local.Host.GetAgentConnections().Count == 1, "Remote loss erased local registration.");
            await Presence(new(actor, null, true, false));
            Check(local.Host.GetAgentConnections().Count == 0, "Clean local exit retained presence.");
        });
    }

    private static BrokerHost CreatePolicyHost(Fixture fixture, HumanConsent consent, string directory, ICopilotRuntimeFactory? runtimeFactory = null) =>
        new(fixture.Identity, consent, directory, "both",
            new($"RemoteAgentLink.Test.Control.{Guid.NewGuid():N}", $"RemoteAgentLink.Test.Transport.{Guid.NewGuid():N}",
                () => throw new IOException("Isolated policy test transport is idle.")), runtimeFactory);

    private static bool ReadSavedDesktopAccess(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, DesktopAccessSettings.FileName)));
        return document.RootElement.GetProperty("enabled").GetBoolean();
    }

    private static CancellationToken DesktopOperationEpoch(BrokerHost host)
    {
        var desktop = typeof(BrokerHost).GetField("_desktop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        return ((CancellationTokenSource)desktop.GetType().GetField("authorizationEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(desktop)!).Token;
    }

    private sealed class PresenceClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan value) => _now += value;
    }

    private sealed class PolicyRuntimeFactory : ICopilotRuntimeFactory
    {
        internal PolicyRuntime Runtime { get; } = new();
        public Task<IAgentRuntimeSession> OpenManagedAsync(AgentSession session, bool resume, RuntimeCallbacks callbacks,
            CancellationToken cancellationToken) => Task.FromResult<IAgentRuntimeSession>(Runtime);
        public Task<IAgentRuntimeSession> AttachAsync(LiveRuntimeRegistration registration, RuntimeCallbacks callbacks,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PolicyRuntime : IAgentRuntimeSession
    {
        public bool IsExternal => false;
        internal int Sends;
        internal int Aborts;
        public Task<bool> IsIdleAsync(CancellationToken cancellationToken) => Task.FromResult(Sends == Aborts);
        public Task PrepareAsync(double? maxAiCredits, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> SendAsync(string prompt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Sends);
            return Task.FromResult("owned-message");
        }
        public Task<bool> AbortOwnedTurnAsync(string messageId, CancellationToken cancellationToken)
        {
            Check(messageId == "owned-message", "Cancellation targeted an unowned turn.");
            Interlocked.Increment(ref Aborts);
            return Task.FromResult(true);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
