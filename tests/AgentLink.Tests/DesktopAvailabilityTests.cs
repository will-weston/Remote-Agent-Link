using System.Reflection;
using System.Text.Json;
using AgentLink.Broker;
using AgentLink.Broker.Shell;
using AgentLink.Core;

internal static partial class BackendTests
{
    private static async Task RunDesktopAvailabilityAsync(Func<string, Func<Task>, Task> test)
    {
        await test("desktop availability defaults on but never grants approval or bypasses connection gates", async () =>
        {
            var consent = new HumanConsent();
            OperationContext.ActorId.Value = "desktop-default";
            Check(consent.DesktopAccessEnabled && !consent.DesktopAllowed && !consent.ObservationAllowed,
                "The default availability state granted permissions or changed compatibility.");
            var approval = consent.RequestAsync(new("observe", "test"), default);
            Check(!approval.IsCompleted && !consent.Requests.TryRead(out _), "Disconnected desktop approval bypassed connection readiness.");
            consent.SetConnected(true);
            var pending = await consent.Requests.ReadAsync();
            Check(!approval.IsCompleted && !consent.ObservationAllowed, "Connecting implicitly approved observation.");
            pending.Answer.TrySetResult(true);
            Check(await approval && consent.ObservationAllowed && !consent.DesktopAllowed, "Explicit observation approval was not isolated from control.");
        });
        await test("desktop off immediately revokes observation and control without changing the connection", async () =>
        {
            var consent = new HumanConsent();
            consent.SetConnected(true);
            OperationContext.ActorId.Value = "desktop-owner";
            var approval = consent.RequestAsync(new("desktop-control", "test"), default);
            (await consent.Requests.ReadAsync()).Answer.TrySetResult(true);
            Check(await approval && consent.DesktopAllowed && consent.ObservationAllowed, "The desktop grant was not established.");
            var oldLifetime = consent.DesktopAccessLifetime;
            consent.SetDesktopAccessEnabled(false);
            Check(!consent.DesktopAccessEnabled && !consent.DesktopAllowed && !consent.ObservationAllowed &&
                !consent.HasDesktopLease && consent.IsConnected, "Off retained desktop authority or disconnected the channel.");
            foreach (var capability in new[] { "observe", "desktop-control" })
                await ThrowsCode("desktop_access_disabled", () => consent.RequestAsync(new(capability, "test"), default));
            consent.SetDesktopAccessEnabled(true);
            using var delayedRequest = CancellationTokenSource.CreateLinkedTokenSource(default, oldLifetime);
            Check(delayedRequest.IsCancellationRequested && !consent.DesktopAccessLifetime.IsCancellationRequested,
                "An operation captured before Off reused the new On epoch.");
            Check(!consent.DesktopAllowed && !consent.ObservationAllowed && !consent.HasDesktopLease,
                "Re-enabling resurrected a prior lease.");
            var fresh = consent.RequestAsync(new("desktop-control", "fresh"), default);
            var pending = await consent.Requests.ReadAsync();
            Check(!fresh.IsCompleted, "Re-enable skipped fresh human approval.");
            pending.Answer.TrySetResult(true);
            Check(await fresh && consent.DesktopAllowed, "Fresh human approval did not work after re-enable.");
            consent.SetPrompting(true);
            Check(!consent.DesktopAllowed && !consent.ObservationAllowed, "Availability bypassed the prompting gate.");
            consent.SetPrompting(false);
            OperationContext.ActorId.Value = "different-agent";
            Check(!consent.DesktopAllowed && !consent.ObservationAllowed, "Availability bypassed actor ownership.");
        });
        await test("off then on invalidates queued answered and connection-waiting desktop approvals", async () =>
        {
            var consent = new HumanConsent();
            OperationContext.ActorId.Value = "desktop-race";
            var waiting = consent.RequestAsync(new("observe", "waiting"), default);
            consent.SetDesktopAccessEnabled(false);
            consent.SetDesktopAccessEnabled(true);
            consent.SetConnected(true);
            Check(!await waiting.WaitAsync(TimeSpan.FromSeconds(2)) && !consent.Requests.TryRead(out _),
                "A pre-Off connection-waiting request resumed after On.");
            for (var iteration = 0; iteration < 40; iteration++)
            {
                var approval = consent.RequestAsync(new("desktop-control", "test"), default);
                var pending = await consent.Requests.ReadAsync();
                if ((iteration & 1) == 0)
                    pending.Answer.TrySetResult(true);
                consent.SetDesktopAccessEnabled(false);
                consent.SetDesktopAccessEnabled(true);
                pending.Answer.TrySetResult(true);
                await approval;
                Check(pending.Revoked && !consent.DesktopAllowed && !consent.ObservationAllowed && !consent.HasDesktopLease,
                    "An old approval survived an Off/On epoch change.");
            }
        });
        await test("desktop off preserves non-UI and unattended approvals and queue capacity", async () =>
        {
            var consent = new HumanConsent();
            consent.SetConnected(true);
            OperationContext.ActorId.Value = "mixed-agent";
            var desktop = Enumerable.Range(0, 15)
                .Select(_ => consent.RequestAsync(new("observe", "desktop"), default)).ToArray();
            var delegation = consent.RequestAsync(new("agent-delegation", "non-UI delegation", Unattended: true), default);
            consent.SetDesktopAccessEnabled(false);
            Check((await Task.WhenAll(desktop)).All(answer => !answer), "Off did not invalidate queued desktop approvals.");
            var retained = await consent.Requests.ReadAsync();
            Check(retained.Request.Capability == "agent-delegation" && retained.Request.Unattended &&
                !retained.Revoked && !delegation.IsCompleted && !consent.Requests.TryRead(out _),
                "Off cancelled non-UI approval or retained stale desktop queue entries.");
            retained.Answer.TrySetResult(true);
            Check(await delegation && !consent.DesktopAllowed && !consent.ObservationAllowed,
                "Off altered an independently approved non-UI capability.");
            consent.SetDesktopAccessEnabled(true);
            var fresh = consent.RequestAsync(new("observe", "fresh"), default);
            var pending = await consent.Requests.ReadAsync();
            Check(!fresh.IsCompleted, "Re-enable inherited observation from non-UI approval.");
            pending.Answer.TrySetResult(false);
            Check(!await fresh, "A declined fresh approval was granted.");
        });
        await test("desktop off preserves non-UI requests waiting for a connection", async () =>
        {
            var consent = new HumanConsent();
            var delegation = consent.RequestAsync(new("agent-delegation", "waiting for connection"), default);
            consent.SetDesktopAccessEnabled(false);
            Check(!delegation.IsCompleted, "Off cancelled a non-UI connection wait.");
            consent.SetConnected(true);
            var pending = await consent.Requests.ReadAsync();
            Check(pending.Request.Capability == "agent-delegation" && !delegation.IsCompleted, "Off approved non-UI work implicitly.");
            pending.Answer.TrySetResult(true);
            Check(await delegation && !consent.DesktopAccessEnabled, "Non-UI approval changed desktop availability.");
        });
        await test("desktop off keeps paired TLS and non-UI agent approvals alive while denying local and remote desktop calls", async () =>
        {
            await using var local = new Fixture();
            await using var remote = new Fixture();
            Pair(local, remote);
            local.Host.Start();
            remote.Host.Start();
            await using var pair = await HostPair.Connect(local.Host, remote.Host);
            await Wait(() => Connected(local.Host) && Connected(remote.Host));
            var binding = local.Host.Connections.Single();
            local.Session.SetDesktopAccessEnabled(false);
            var outgoingApproval = local.Client.CallAsync("invoke", new RoutedOperation(binding.ConnectionId, false,
                new("desktop.acquire", Json.Element(new { }), Guid.NewGuid().ToString("N"))), default);
            var remoteApproval = await remote.Consent.Requests.ReadAsync();
            Check(remoteApproval.Request.Capability == "desktop-control" && !outgoingApproval.IsCompleted,
                "Turning off THIS desktop prevented independent approval on the other desktop.");
            remoteApproval.Answer.TrySetResult(false);
            await ThrowsCode("permission_denied", () => outgoingApproval);
            var previousEpoch = DesktopOperationEpoch(remote.Host);
            var delegation = LocalInvoke(remote, "agent_start_session", new { workingDirectory = remote.Workspace });
            var nonUiApproval = await remote.Consent.Requests.ReadAsync();
            Check(nonUiApproval.Request.Capability == "agent-delegation", "The non-UI request did not reach its own consent gate.");
            var durable = SeedTask(remote, $"local:{WindowsLogon.Current}", "waiting_for_connection");
            remote.Session.SetDesktopAccessEnabled(false);
            Check(previousEpoch.IsCancellationRequested && remote.Consent.IsConnected && !remote.Consent.DesktopAccessEnabled &&
                !nonUiApproval.Revoked && !delegation.IsCompleted && !remote.Host.GetLocalTask(durable.TaskId).CancellationRequested,
                "Off failed to cancel the desktop epoch or cancelled independent agent work.");
            Check(Connected(local.Host) && Connected(remote.Host) && local.Host.Connections.Single().Epoch == binding.Epoch &&
                remote.Identity.TrustedFingerprints.Contains(local.Identity.Fingerprint), "Off changed TLS or pairing.");
            foreach (var method in new[] { "get_screen_size", "take_screenshot", "move_mouse", "desktop.acquire" })
            {
                await ThrowsCode("desktop_access_disabled", () => LocalInvoke(remote, method, new { }));
                await ThrowsCode("desktop_access_disabled", () => local.Client.CallAsync("invoke",
                    new RoutedOperation(binding.ConnectionId, false, new(method, Json.Element(new { }), Guid.NewGuid().ToString("N"))), default));
            }
            Check((await remote.Client.CallAsync("describe", new TargetRequest(null, true), default)).Data!.Value.GetArrayLength() > 0,
                "Off hid the authenticated local metadata catalog.");
            nonUiApproval.Answer.TrySetResult(false);
            await ThrowsCode("consent_denied", () => delegation);
            remote.Session.SetDesktopAccessEnabled(true);
            Check(previousEpoch.IsCancellationRequested && !remote.Consent.HasDesktopLease && !remote.Consent.ObservationAllowed,
                "On restored a cancelled desktop epoch or prior grant.");
        });
        await test("desktop off does not close an unpaired pending TLS channel", async () =>
        {
            await using var fixture = new Fixture();
            await using var peer = await IncomingPeer.Connect(fixture);
            await peer.Peer.CallAsync(new("protocol.hello", Json.Element(Hello())), default);
            await Wait(() => Pending(fixture.Host));
            var channel = fixture.Host.PeerConnections.Single().Id;
            fixture.Session.SetDesktopAccessEnabled(false);
            var ping = await peer.Peer.CallAsync(new("protocol.ping", Json.Element(new { })), default);
            Check(ping.Data!.Value.GetProperty("protocolVersion").GetString() == Wire.ProtocolVersion &&
                fixture.Host.PeerConnections.Single().Id == channel && !fixture.Consent.IsConnected &&
                !fixture.Consent.DesktopAccessEnabled, "Off changed the pending channel or admitted an unknown peer.");
        });
        await test("saved desktop availability survives restart and is scoped to its private session directory", async () =>
        {
            await using var fixture = new Fixture();
            Check(fixture.Consent.DesktopAccessEnabled, "A new broker did not preserve the default On policy.");
            fixture.Session.SetDesktopAccessEnabled(false);
            Check(!ReadSavedDesktopAccess(fixture.DataDirectory), "Off was not durably saved.");
            await fixture.Session.StopAsync();
            var restored = new HumanConsent();
            await using var restarted = CreatePolicyHost(fixture, restored, fixture.DataDirectory);
            Check(!restored.DesktopAccessEnabled && !restarted.IsRunning && !restored.DesktopAllowed,
                "A direct/headless host did not load Off before startup.");
            restarted.Start();
            Check(!restored.DesktopAccessEnabled, "Starting the broker overwrote the saved choice.");
            var session = new BrokerSession(fixture.Identity, restored, restarted, fixture.DataDirectory, "both");
            session.SetDesktopAccessEnabled(true);
            Check(restored.DesktopAccessEnabled && ReadSavedDesktopAccess(fixture.DataDirectory) &&
                !restored.HasDesktopLease && !restored.ObservationAllowed, "Persisted On created a grant.");
            await session.StopAsync();
            var onAgain = new HumanConsent();
            await using var next = CreatePolicyHost(fixture, onAgain, fixture.DataDirectory);
            Check(onAgain.DesktopAccessEnabled && !onAgain.DesktopAllowed, "A saved On choice failed to survive restart.");
            next.SetDesktopAccessEnabled(false);
            var other = new HumanConsent();
            await using var otherLogon = CreatePolicyHost(fixture, other, Path.Combine(fixture.DataDirectory, "other-logon"));
            Check(other.DesktopAccessEnabled, "One logon's Off choice changed another session directory's default.");
        });
        await test("normal Stop disconnect and Quit revoke grants without changing saved desktop availability", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Session.SetDesktopAccessEnabled(false);
            fixture.Session.SetDesktopAccessEnabled(true);
            fixture.Consent.SetConnected(true);
            OperationContext.ActorId.Value = "normal-stop";
            var approval = fixture.Consent.RequestAsync(new("desktop-control", "test"), default);
            (await fixture.Consent.Requests.ReadAsync()).Answer.TrySetResult(true);
            Check(await approval && fixture.Consent.DesktopAllowed, "The test grant was not established.");
            fixture.Host.StopDesktopControl();
            Check(fixture.Consent.DesktopAccessEnabled && !fixture.Consent.HasDesktopLease && ReadSavedDesktopAccess(fixture.DataDirectory),
                "Normal Stop silently changed availability.");
            fixture.Consent.SetConnected(false);
            Check(fixture.Consent.DesktopAccessEnabled && ReadSavedDesktopAccess(fixture.DataDirectory),
                "Disconnect changed the saved switch.");
            await fixture.Session.StopAsync();
            Check(fixture.Consent.DesktopAccessEnabled && !fixture.Consent.DesktopAllowed && ReadSavedDesktopAccess(fixture.DataDirectory),
                "Quit changed the saved switch rather than just revoking grants.");
            await ThrowsCode("broker_stopping", () => { fixture.Session.SetDesktopAccessEnabled(false); return Task.CompletedTask; });
        });
        await test("desktop settings reject malformed unsupported duplicate oversized and unreadable data before host startup", async () =>
        {
            await using var fixture = new Fixture();
            await fixture.Session.StopAsync();
            var path = Path.Combine(fixture.DataDirectory, DesktopAccessSettings.FileName);
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
                Check(!consent.DesktopAccessEnabled && !consent.IsConnected, "Malformed settings left the consent service permissive.");
            }
            File.WriteAllText(path, """{"version":1,"enabled":false}""");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var consent = new HumanConsent();
                await ThrowsCode("desktop_access_settings_unavailable", async () =>
                {
                    await using var unexpected = CreatePolicyHost(fixture, consent, fixture.DataDirectory);
                });
                Check(!consent.DesktopAccessEnabled, "Unreadable settings enabled desktop access.");
            }
            File.Delete(path);
            var missing = new HumanConsent();
            await using var defaultHost = CreatePolicyHost(fixture, missing, fixture.DataDirectory);
            Check(missing.DesktopAccessEnabled, "A genuinely missing setting did not preserve the original default.");
        });
        await test("desktop persistence failure keeps Off fail-closed and cannot enable an unsaved On choice", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Session.SetDesktopAccessEnabled(false);
            fixture.Session.SetDesktopAccessEnabled(true);
            fixture.Consent.SetConnected(true);
            OperationContext.ActorId.Value = "save-failure";
            var grant = fixture.Consent.RequestAsync(new("desktop-control", "test"), default);
            (await fixture.Consent.Requests.ReadAsync()).Answer.TrySetResult(true);
            Check(await grant, "The test grant was not established.");
            var path = Path.Combine(fixture.DataDirectory, DesktopAccessSettings.FileName);
            var epoch = DesktopOperationEpoch(fixture.Host);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await ThrowsCode("desktop_access_persistence_failed", () =>
                {
                    fixture.Session.SetDesktopAccessEnabled(false);
                    return Task.CompletedTask;
                });
                Check(!fixture.Consent.DesktopAccessEnabled && !fixture.Consent.DesktopAllowed && !fixture.Consent.HasDesktopLease &&
                    epoch.IsCancellationRequested, "Failed persistence rolled back the human's immediate Off choice.");
            }
            Check(ReadSavedDesktopAccess(fixture.DataDirectory), "A failed atomic save unexpectedly changed the old durable value.");
            fixture.Session.SetDesktopAccessEnabled(false);
            Check(!ReadSavedDesktopAccess(fixture.DataDirectory), "Retrying Off did not persist the failed choice.");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await ThrowsCode("desktop_access_persistence_failed", () =>
                {
                    fixture.Session.SetDesktopAccessEnabled(true);
                    return Task.CompletedTask;
                });
                Check(!fixture.Consent.DesktopAccessEnabled && !fixture.Consent.DesktopAllowed, "Failed On persistence enabled desktop access.");
            }
            Check(!ReadSavedDesktopAccess(fixture.DataDirectory) &&
                !Directory.EnumerateFiles(fixture.DataDirectory, "*.pending").Any(), "A failed save left a misleading setting or staging file.");
        });
        await test("reentrant availability observers see committed storage and cannot split policy from persistence", async () =>
        {
            await using var fixture = new Fixture();
            var reenabled = false;
            fixture.Consent.Changed += () =>
            {
                if (reenabled || fixture.Consent.DesktopAccessEnabled)
                    return;
                Check(!ReadSavedDesktopAccess(fixture.DataDirectory), "Off was announced before its successful save.");
                reenabled = true;
                fixture.Session.SetDesktopAccessEnabled(true);
            };
            fixture.Session.SetDesktopAccessEnabled(false);
            Check(reenabled && fixture.Consent.DesktopAccessEnabled && ReadSavedDesktopAccess(fixture.DataDirectory) &&
                !fixture.Consent.HasDesktopLease, "A reentrant change split the saved and effective policies.");
        });
    }

    private static BrokerHost CreatePolicyHost(Fixture fixture, HumanConsent consent, string directory) =>
        new(fixture.Identity, consent, directory, "both",
            new($"RemoteAgentLink.Test.Control.{Guid.NewGuid():N}", $"RemoteAgentLink.Test.Transport.{Guid.NewGuid():N}",
                () => throw new IOException("Isolated policy test transport is idle.")));

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
}
