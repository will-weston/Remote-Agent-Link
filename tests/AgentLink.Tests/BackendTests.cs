using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AgentLink.Broker;
using AgentLink.Core;

internal static partial class BackendTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("channel TLS validation does not relax strict pin validation", async () =>
        {
            await using var fixture = new Fixture();
            using var other = Certificate();
            Check(fixture.Identity.ValidateChannelPeer(null!, other, null, SslPolicyErrors.RemoteCertificateChainErrors),
                "A current unknown certificate could not establish a pending channel.");
            Check(!fixture.Identity.ValidatePeer(null!, other, null, SslPolicyErrors.None), "Strict validation accepted an unknown peer.");
            fixture.Identity.Trust(EndpointIdentity.FingerprintOf(other));
            Check(fixture.Identity.ValidatePeer(null!, other, null, SslPolicyErrors.RemoteCertificateChainErrors), "Strict validation lost a valid pin.");
            using var expired = Certificate(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
            using var future = Certificate(DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2));
            Check(!fixture.Identity.ValidateChannelPeer(null!, expired, null, SslPolicyErrors.None) &&
                  !fixture.Identity.ValidateChannelPeer(null!, future, null, SslPolicyErrors.None) &&
                  !fixture.Identity.ValidateChannelPeer(null!, null, null, SslPolicyErrors.None), "Invalid pending certificate accepted.");
        });
        await test("single-pin revocation preserves other pins and notifies every observer", async () =>
        {
            await using var fixture = new Fixture();
            var first = new string('A', 64);
            var second = new string('B', 64);
            fixture.Identity.Trust(first);
            fixture.Identity.Trust(second);
            var events = 0;
            fixture.Identity.TrustChanged += () => throw new InvalidOperationException("test observer");
            fixture.Identity.TrustChanged += () => events++;
            fixture.Identity.Revoke(string.Join(':', Enumerable.Repeat("aa", 32)));
            Check(fixture.Identity.TrustedFingerprints.SequenceEqual([second]) && events == 1, "Revocation removed another pin or skipped observers.");
            fixture.Identity.Revoke(first);
            Check(events == 1, "A no-op revocation raised a spurious event.");
            await Throws<ArgumentException>(() => { fixture.Identity.Revoke("invalid"); return Task.CompletedTask; });
            using var anotherProcess = new EndpointIdentity(fixture.IdentityDirectory, fixture.Identity.Certificate);
            anotherProcess.Revoke(second);
            fixture.Identity.RefreshTrust();
            Check(fixture.Identity.TrustedFingerprints.Count == 0 && events == 2, "External revocation was not reconciled.");
        });
        await test("pending admission is atomic and never converts an absent pin into trust", async () =>
        {
            using var certificate = Certificate();
            var gate = new object();
            string[] pins = [];
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var mutationStarted = new ManualResetEventSlim();
            bool? registeredTrust = null;
            var admission = Task.Run(() => PeerAdmission.AdmitChannel(gate, () => pins, certificate, trusted =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                registeredTrust = trusted;
            }));
            Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "Admission did not start.");
            var mutation = Task.Run(() =>
            {
                mutationStarted.Set();
                lock (gate)
                {
                    Check(registeredTrust == false, "Trust mutation overtook pending registration.");
                    pins = [EndpointIdentity.FingerprintOf(certificate)];
                }
            });
            Check(await Task.Run(() => mutationStarted.Wait(TimeSpan.FromSeconds(5))), "Mutation did not start.");
            release.Set();
            await Task.WhenAll(admission, mutation);
            Check(registeredTrust == false, "An unpinned certificate was authorized.");
        });
        await test("malformed and impersonating channel metadata is rejected", async () =>
        {
            var good = Hello();
            Check(ChannelProtocol.ReadHello(Json.Element(good)) == good, "Valid bounded metadata was rejected.");
            var invalid = new[]
            {
                good with { DisplayName = new string('a', 64) },
                good with { DisplayName = "device\nAPPROVED" },
                good with { DisplayName = "device\u202eexe" },
                good with { DisplayName = "DOMAIN\\Administrator" },
                good with { DisplayName = "device.example.com" },
                good with { DisplayName = " name" },
                good with { SessionId = 0 },
                good with { SessionId = -1 },
                good with { LogonId = "S-1-5-5-impostor" },
                good with { LogonId = "S-1-5-5-4294967296-1" },
                good with { LogonId = new string('x', 2049) },
                good with { TrustedPeer = null },
                good with { SupportsPendingPairing = false },
                good with { ProtocolVersion = "2" }
            };
            foreach (var value in invalid)
                await Throws<AgentLinkException>(() => { ChannelProtocol.ReadHello(Json.Element(value)); return Task.CompletedTask; });
            foreach (var json in new[]
            {
                """{"protocolVersion":"1","sessionId":1,"sessionId":2,"logonId":"S-1-5-5-1-2"}""",
                """{"protocolVersion":"1","sessionId":1,"logonId":"S-1-5-5-1-2","approved":true}""",
                """{"protocolVersion":"1","sessionId":1,"logonId":"S-1-5-5-1-2","trustedPeer":true}""",
                """{"protocolVersion":"1","sessionId":1,"logonId":null}"""
            })
            {
                using var data = JsonDocument.Parse(json);
                await Throws<AgentLinkException>(() => { ChannelProtocol.ReadHello(data.RootElement); return Task.CompletedTask; });
            }
        });
        await test("pending frame bounds apply before payload allocation to requests and responses", async () =>
        {
            foreach (var kind in new byte[] { 1, 2, 3 })
            {
                var header = new byte[5];
                BinaryPrimitives.WriteInt32LittleEndian(header, ChannelFrameStream.MaximumPendingFrameBytes + 1);
                header[4] = kind;
                using var input = new MemoryStream(header);
                using var stream = new ChannelFrameStream(input, () => false);
                await Throws<AgentLinkException>(() => stream.ReadAsync(new byte[1]).AsTask());
                Check(input.Position == 5, "An oversized pending payload was read.");
            }
        });
        await test("pending frame parser accepts only bounded hello heartbeat and cancellation", async () =>
        {
            using var output = new MemoryStream();
            var hello = new WireMessage("request", Guid.NewGuid().ToString("N"), "protocol.hello", Json.Element(Hello()));
            var ping = new WireMessage("request", Guid.NewGuid().ToString("N"), "protocol.ping", Json.Element(new { }));
            var cancel = new WireMessage("cancel", Guid.NewGuid().ToString("N"));
            foreach (var message in new[] { hello, ping, cancel }) await Wire.WriteAsync(output, message, default);
            using var input = new SplitStream(output.ToArray());
            using var stream = new ChannelFrameStream(input, () => false);
            foreach (var message in new[] { hello, ping, cancel })
                Check((await Wire.ReadAsync(stream, default))?.Id == message.Id, "A split pending metadata frame was corrupted.");
            Check(await Wire.ReadAsync(stream, default) is null, "Pending stream did not finish.");
            foreach (var message in new[]
            {
                new WireMessage("request", Guid.NewGuid().ToString("N"), "tools.list", Json.Element(new { })),
                ping with { ActorId = "claimed-owner" },
                ping with { Arguments = Json.Element(new { approve = true }) },
                new WireMessage("response", Guid.NewGuid().ToString("N"), Result: new(Text: "approve this prompt")),
                new WireMessage("response", Guid.NewGuid().ToString("N"), Result: new(Data: Json.Element(new { tasks = new[] { "secret" } })))
            })
            {
                using var bytes = new MemoryStream();
                await Wire.WriteAsync(bytes, message, default);
                bytes.Position = 0;
                using var guarded = new ChannelFrameStream(bytes, () => false);
                await Throws<AgentLinkException>(() => Wire.ReadAsync(guarded, default));
            }
        });
        await test("pending metadata flooding has a fixed per-channel budget", async () =>
        {
            using var bytes = new MemoryStream();
            for (var index = 0; index < 33; index++)
                await Wire.WriteAsync(bytes, new("request", Guid.NewGuid().ToString("N"), "protocol.ping", Json.Element(new { })), default);
            bytes.Position = 0;
            using var stream = new ChannelFrameStream(bytes, () => false);
            for (var index = 0; index < 32; index++)
                Check(await Wire.ReadAsync(stream, default) is not null, "Metadata budget rejected normal traffic.");
            await Throws<AgentLinkException>(() => Wire.ReadAsync(stream, default));
        });
        await test("pending readiness flags and names never grant local authority", async () =>
        {
            using var certificate = Certificate();
            using var channel = new PeerChannel(PeerDirection.IncomingAgent, default);
            channel.Authenticate(certificate, false);
            channel.AcceptHello(Hello(true) with { DisplayName = "Approved administrator" });
            Check(!channel.Ready && !channel.Snapshot.TrustedLocally && channel.Snapshot.TrustedRemotely &&
                channel.Snapshot.Status == PeerConnectionStatus.AwaitingApproval, "A peer authorized itself.");
            await Throws<AgentLinkException>(() => { channel.RequireReady(); return Task.CompletedTask; });
            channel.Close();
            await Throws<OperationCanceledException>(() => { channel.AcceptHello(Hello(true)); return Task.CompletedTask; });
        });
        await test("ready channels retain normal framing limits without relaxing pending limits", async () =>
        {
            using var bytes = new MemoryStream();
            await Wire.WriteAsync(bytes, new("request", Guid.NewGuid().ToString("N"), "agent_send",
                Json.Element(new { prompt = new string('x', 8192) })), default);
            bytes.Position = 0;
            using var stream = new ChannelFrameStream(bytes, () => true);
            Check((await Wire.ReadAsync(stream, default))?.Method == "agent_send", "Ready framing retained the pending-only payload bound.");
        });
        await test("concurrent channel revocation and disposal preserve cancellation", async () =>
        {
            using var certificate = Certificate();
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var channel = new PeerChannel(PeerDirection.IncomingAgent, default);
                channel.Authenticate(certificate, true);
                channel.AcceptHello(Hello(true));
                var lifetime = channel.Lifetime;
                await Task.WhenAll(Task.Run(() => channel.Close()), Task.Run(channel.Dispose));
                Check(lifetime.IsCancellationRequested && !channel.Ready, "A revocation/disposal race lost cancellation.");
                channel.Dispose();
            }
        });
        await test("automatic mutual TLS shows both pending directions without exposing tools", async () =>
        {
            await using var local = new Fixture();
            await using var remote = new Fixture();
            local.Host.Start();
            remote.Host.Start();
            await using var pair = await HostPair.Connect(local.Host, remote.Host);
            await Wait(() => Pending(local.Host) && Pending(remote.Host));
            var outgoing = local.Host.PeerConnections.Single();
            var incoming = remote.Host.PeerConnections.Single();
            Check(outgoing.Direction == PeerDirection.RemoteDesktop && incoming.Direction == PeerDirection.IncomingAgent,
                "Channel directions were reversed.");
            Check(outgoing.Fingerprint == remote.Identity.Fingerprint && incoming.Fingerprint == local.Identity.Fingerprint,
                "Displayed fingerprints were not TLS identities.");
            Check(!outgoing.TrustedLocally && !outgoing.TrustedRemotely && local.Host.Connections.Count == 0 &&
                remote.Host.Connections.Count == 0 && !local.Consent.IsConnected && !remote.Consent.IsConnected,
                "A pending channel became an operation link.");
            await CheckLocalOnlyTargets(local);
            await CheckLocalOnlyTargets(remote);
        });
        await test("one-sided pairing stays pending and mutation cancels both channel lifetimes", async () =>
        {
            await using var local = new Fixture();
            await using var remote = new Fixture();
            local.Identity.Trust(remote.Identity.Fingerprint);
            await using var pair = await HostPair.Connect(local.Host, remote.Host);
            await Wait(() => Pending(local.Host) && Pending(remote.Host));
            Check(local.Host.PeerConnections.Single() is { TrustedLocally: true, TrustedRemotely: false } &&
                remote.Host.PeerConnections.Single() is { TrustedLocally: false, TrustedRemotely: true },
                "One-sided pairing was not represented truthfully.");
            remote.Identity.Trust(local.Identity.Fingerprint);
            Check(remote.Host.PeerConnections.Count == 0 && !remote.Consent.IsConnected, "Trust mutation retained the pending channel.");
            await Wait(() => local.Host.PeerConnections.Count == 0);
        });
        await test("an idle incoming retry does not misreport a healthy outgoing channel", async () =>
        {
            await using var local = new Fixture();
            await using var remote = new Fixture();
            Pair(local, remote);
            local.Host.Start();
            await using var pair = await HostPair.Connect(local.Host, remote.Host);
            await Wait(() => Connected(local.Host) && Connected(remote.Host));
            var attempts = local.RemoteOpenAttempts;
            await Wait(() => local.RemoteOpenAttempts > attempts);
            Check(local.Host.IsRunning && local.Host.LastTransportError is null && local.Host.Connections.Count == 1,
                "An expected incoming retry reported failure for a healthy outgoing channel.");
        });
        await test("paired channels expose only outgoing MCP links and no implicit permissions", async () =>
        {
            await using var local = new Fixture();
            await using var remote = new Fixture();
            Pair(local, remote);
            local.Host.Start();
            remote.Host.Start();
            await using var pair = await HostPair.Connect(local.Host, remote.Host);
            await Wait(() => Connected(local.Host) && Connected(remote.Host));
            Check(local.Host.Connections.Count == 1 && remote.Host.Connections.Count == 0, "Incoming channel leaked into the MCP target list.");
            Check(!local.Consent.IsConnected && remote.Consent.IsConnected, "Outgoing channel made the own-session desktop available.");
            Check(!local.Consent.DesktopAllowed && !remote.Consent.DesktopAllowed && !remote.Consent.ObservationAllowed,
                "Pairing silently granted a desktop permission.");
            var id = local.Host.Connections.Single().ConnectionId;
            var catalog = await local.Client.CallAsync("describe", new TargetRequest(id), default);
            Check(catalog.Data!.Value.GetArrayLength() > 0, "Ready catalog forwarding failed.");
            Check((await local.Client.CallAsync("describe", new TargetRequest(null, true), default)).Data!.Value.GetArrayLength() > 0,
                "The authenticated local catalog became unavailable without an incoming channel.");
            var acquire = local.Client.CallAsync("invoke", new RoutedOperation(id, false,
                new("desktop.acquire", Json.Element(new { }), Guid.NewGuid().ToString("N"))), default);
            var approval = await remote.Consent.Requests.ReadAsync();
            Check(approval.Request.Capability == "desktop-control" && !acquire.IsCompleted, "Desktop control did not await a human.");
            remote.Identity.Revoke(local.Identity.Fingerprint);
            Check(!remote.Consent.IsConnected && !remote.Consent.DesktopAllowed && approval.Revoked,
                "Revocation did not synchronously revoke incoming consent.");
            approval.Answer.TrySetResult(true);
            await ThrowsAny(() => acquire);
            await Wait(() => local.Host.Connections.Count == 0 && local.Host.PeerConnections.Count == 0);
            await Throws<AgentLinkException>(() => local.Client.CallAsync("describe", new TargetRequest(id), default));
        });
        await test("unknown peers cannot call catalog desktop agent task or approval RPC", async () =>
        {
            await using var fixture = new Fixture();
            foreach (var method in new[]
            {
                "tools.list", "desktop.acquire", "desktop.release", "take_screenshot", "agent_list_sessions",
                "agent_start_session", "agent_get_task", "agent_cancel_task", "ui.show", "trust", "approve"
            })
            {
                await using var connection = await IncomingPeer.Connect(fixture);
                await connection.Peer.CallAsync(new("protocol.hello", Json.Element(Hello(true))), default);
                await ThrowsAny(() => connection.Peer.CallAsync(new(method, Json.Element(new { }), "claimed-actor"), default));
                Check(!fixture.Consent.IsConnected && !fixture.Consent.Requests.TryRead(out _) &&
                    fixture.Host.ListLocalTasks(false).Count == 0, $"Pending {method} reached a privileged operation.");
            }
        });
        await test("pairing reconnect authorizes the channel but never restores a desktop grant", async () =>
        {
            await using var local = new Fixture();
            await using var remote = new Fixture();
            await using (var pending = await HostPair.Connect(local.Host, remote.Host))
            {
                await Wait(() => Pending(local.Host) && Pending(remote.Host));
                Pair(local, remote);
                await Wait(() => local.Host.PeerConnections.Count == 0 && remote.Host.PeerConnections.Count == 0);
            }
            await using (var paired = await HostPair.Connect(local.Host, remote.Host))
            {
                await Wait(() => Connected(local.Host) && Connected(remote.Host));
                Check(remote.Consent.IsConnected && !remote.Consent.DesktopAllowed && !remote.Consent.ObservationAllowed,
                    "Completing pairing silently approved desktop operations.");
            }
            await using (var reconnected = await HostPair.Connect(local.Host, remote.Host))
            {
                await Wait(() => Connected(local.Host) && Connected(remote.Host));
                Check(!remote.Consent.DesktopAllowed && !remote.Consent.ObservationAllowed, "Reconnect restored an old permission.");
            }
        });
        await test("paired agent creation still requires a human delegation decision", async () =>
        {
            await using var fixture = new Fixture();
            using var certificate = Certificate();
            fixture.Identity.Trust(EndpointIdentity.FingerprintOf(certificate));
            await using var connection = await IncomingPeer.Connect(fixture, certificate);
            await connection.Peer.CallAsync(new("protocol.hello", Json.Element(Hello(true))), default);
            var creation = connection.Peer.CallAsync(new("agent_start_session",
                Json.Element(new { workingDirectory = fixture.Workspace }), "agent-a"), default);
            var pending = await fixture.Consent.Requests.ReadAsync();
            Check(pending.Request.Capability == "agent-delegation" && !creation.IsCompleted &&
                  fixture.Host.ListLocalTasks(false).Count == 0, "Pairing launched agent work without a human decision.");
            pending.Answer.TrySetResult(false);
            await Throws<AgentLinkException>(() => creation);
            Check(fixture.Host.ListLocalTasks(false).Count == 0 && !fixture.Consent.DesktopAllowed,
                "Rejected delegation created work or a desktop grant.");
        });
        await test("changing readiness metadata closes an authorized channel immediately", async () =>
        {
            await using var fixture = new Fixture();
            using var certificate = Certificate();
            fixture.Identity.Trust(EndpointIdentity.FingerprintOf(certificate));
            await using var connection = await IncomingPeer.Connect(fixture, certificate);
            await connection.Peer.CallAsync(new("protocol.hello", Json.Element(Hello(true))), default);
            Check(fixture.Consent.IsConnected, "Paired channel did not activate.");
            await ThrowsAny(() => connection.Peer.CallAsync(new("protocol.hello", Json.Element(Hello(false))), default));
            await Wait(() => fixture.Host.PeerConnections.Count == 0);
            Check(!fixture.Consent.IsConnected, "Withdrawn peer readiness retained incoming authorization.");
        });
        await test("throwing presentation observers cannot prevent backend revocation", async () =>
        {
            await using var local = new Fixture();
            await using var remote = new Fixture();
            Pair(local, remote);
            local.Host.StatusChanged += () => throw new InvalidOperationException("test view");
            remote.Consent.Changed += () => throw new InvalidOperationException("test consent view");
            await using var connection = await HostPair.Connect(local.Host, remote.Host);
            await Wait(() => Connected(local.Host) && Connected(remote.Host));
            remote.Identity.Revoke(local.Identity.Fingerprint);
            Check(!remote.Consent.IsConnected && remote.Host.PeerConnections.Count == 0, "A view interrupted revocation.");
            await Wait(() => local.Host.Connections.Count == 0);
        });
        await test("connecting channel state is bounded and cancels before TLS admission", async () =>
        {
            await using var fixture = new Fixture();
            var transports = Enumerable.Range(0, 17).Select(_ => new WaitingStream()).ToArray();
            var connections = transports.Select(stream => fixture.Host.HandleOutgoingChannelAsync(stream)).ToArray();
            Check(fixture.Host.PeerConnections.Count == 17 && fixture.Host.PeerConnections.All(peer =>
                peer.Status == PeerConnectionStatus.Connecting && peer.Fingerprint.Length == 0), "Connecting channel state was incorrect.");
            using var extra = new WaitingStream();
            await Throws<AgentLinkException>(() => fixture.Host.HandleOutgoingChannelAsync(extra));
            fixture.Identity.RevokeAll();
            await Drain(connections);
            Check(fixture.Host.PeerConnections.Count == 0 && transports.All(stream => stream.Disposed),
                "Pre-TLS channels survived cancellation or leaked transports.");
        });
        await test("session unavailability closes pending incoming channels without promoting outgoing ones", async () =>
        {
            await using var local = new Fixture();
            await using var remote = new Fixture();
            await using var connection = await HostPair.Connect(local.Host, remote.Host);
            await Wait(() => Pending(local.Host) && Pending(remote.Host));
            remote.Host.OnSessionUnavailable();
            Check(remote.Host.PeerConnections.Count == 0 && !remote.Consent.IsConnected, "Pending incoming channel survived session loss.");
            await Wait(() => local.Host.PeerConnections.Count == 0);
            Check(!local.Consent.IsConnected, "Outgoing channel promoted own-session availability after session loss.");
        });
        await test("authenticated pending peers still need private-key possession", async () =>
        {
            await using var fixture = new Fixture();
            using var key = Certificate();
            using var publicOnly = X509CertificateLoader.LoadCertificate(key.RawData);
            await ThrowsAny(async () =>
            {
                await using var connection = await IncomingPeer.Connect(fixture, publicOnly);
                await connection.Peer.CallAsync(new("protocol.hello", Json.Element(Hello(true))), default);
            });
            Check(!fixture.Consent.IsConnected && fixture.Host.Connections.Count == 0, "A certificate without its private key authenticated.");
        });
        await test("expired peers fail pending TLS before hello", async () =>
        {
            await using var fixture = new Fixture();
            using var expired = Certificate(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
            await ThrowsAny(async () =>
            {
                await using var connection = await IncomingPeer.Connect(fixture, expired);
                await connection.Peer.CallAsync(new("protocol.hello", Json.Element(Hello())), default);
            });
            Check(!fixture.Consent.IsConnected, "Expired certificate enabled the session.");
        });
        await test("legacy incoming hello remains compatible only when locally paired", async () =>
        {
            await using var fixture = new Fixture();
            using var certificate = Certificate();
            fixture.Identity.Trust(EndpointIdentity.FingerprintOf(certificate));
            await using (var connection = await IncomingPeer.Connect(fixture, certificate))
            {
                var result = await connection.Peer.CallAsync(new("protocol.hello", Json.Element(new { })), default);
                var legacy = Arguments.Read<LegacyHello>(result.Data!.Value);
                using var original = OriginalCore.TryOpen();
                original?.ReadHello(result.Data.Value);
                Check(legacy.ProtocolVersion == Wire.ProtocolVersion && result.Data.Value.EnumerateObject().Count() == 3,
                    "Legacy hello gained incompatible fields.");
                await Wait(() => fixture.Consent.IsConnected);
                Check((await connection.Peer.CallAsync(new("tools.list", Json.Element(new { })), default)).Data!.Value.GetArrayLength() > 0,
                    "A paired legacy catalog request was rejected.");
            }
            fixture.Identity.RevokeAll();
            await using (var connection = await IncomingPeer.Connect(fixture, certificate))
            {
                await ThrowsAny(() => connection.Peer.CallAsync(new("protocol.hello", Json.Element(new { })), default));
                Check(!fixture.Consent.IsConnected, "An unpaired legacy endpoint was admitted.");
            }
        });
        await test("original-shape strict hello rejects every unnegotiated metadata field", async () =>
        {
            var legacy = Json.Element(new LegacyHello(Wire.ProtocolVersion, 42, "S-1-5-5-1-2"));
            Check(Arguments.Read<LegacyHello>(legacy).SessionId == 42, "The original three-field DTO could not deserialize.");
            await Throws<AgentLinkException>(() =>
            {
                Arguments.Read<LegacyHello>(Json.Element(Hello(true)));
                return Task.CompletedTask;
            });
            using var explicitNull = JsonDocument.Parse(
                """{"protocolVersion":"1","sessionId":42,"logonId":"S-1-5-5-1-2","displayName":null}""");
            await Throws<AgentLinkException>(() =>
            {
                Arguments.Read<LegacyHello>(explicitNull.RootElement);
                return Task.CompletedTask;
            });
            using var original = OriginalCore.TryOpen();
            if (original is not null)
            {
                original.ReadHello(legacy);
                original.RequireRejected(Json.Element(Hello(true)));
                original.RequireRejected(explicitNull.RootElement);
                Console.WriteLine("VERIFIED archived 0.1.0.1 AgentLink.Core.dll: Arguments.Read<EndpointHello> rejects unknown members, including null metadata.");
            }
        });
        await test("legacy outgoing TLS and three-field hello remain compatible and fail closed without pin", async () =>
        {
            await using var fixture = new Fixture();
            using var certificate = Certificate();
            fixture.Identity.Trust(EndpointIdentity.FingerprintOf(certificate));
            await WithLegacyServer(fixture, certificate, async () =>
            {
                await Wait(() => fixture.Host.Connections.Count == 1);
                Check(fixture.Host.PeerConnections.Single().Status == PeerConnectionStatus.Connected &&
                      !fixture.Consent.IsConnected, "Legacy outgoing state granted own-session availability.");
            });
            fixture.Identity.RevokeAll();
            await WithLegacyServer(fixture, certificate, async () =>
            {
                await Wait(() => fixture.Host.PeerConnections.Count == 0);
                Check(fixture.Host.Connections.Count == 0, "Missing local pin was bypassed by a legacy response.");
            });
        });
        await test("ui.show is isolated same-session control IPC and never grants permission", async () =>
        {
            await using var fixture = new Fixture();
            var shown = 0;
            fixture.Host.ShowRequested += () => shown++;
            fixture.Host.Start();
            Check(fixture.Host.IsRunning, "Isolated listener did not start.");
            await fixture.Client.ShowAsync();
            Check(shown == 1 && !fixture.Consent.IsConnected && !fixture.Consent.DesktopAllowed, "Show did not notify, or changed authorization.");
            foreach (var method in new[] { "approve", "trust", "revoke", "consent.approve" })
                await Throws<AgentLinkException>(() => fixture.Client.CallAsync(method, new { }, default));
            await Throws<AgentLinkException>(() => fixture.Client.CallAsync("ui.show", new { approve = true }, default));
            Check(shown == 1, "Invalid show arguments raised the UI event.");
        });
        await test("fatal control-listener failure is visible and never reports running", async () =>
        {
            await using var fixture = new Fixture();
            await using var blocker = PipeSecurity.CreateServer(fixture.ControlPipe, true);
            var statuses = 0;
            fixture.Host.StatusChanged += () => statuses++;
            fixture.Host.Log += _ => throw new InvalidOperationException("test log view");
            fixture.Host.Start();
            await Wait(() => !fixture.Host.IsRunning && fixture.Host.LastTransportError is not null);
            Check(statuses > 0 && fixture.Host.LastTransportError!.Contains("restart", StringComparison.OrdinalIgnoreCase),
                "Listener failure lacked an actionable status.");
        });
        await test("disconnect drains pending approvals without granting delegation or unattended work", async () =>
        {
            var consent = new HumanConsent();
            consent.SetConnected(true);
            var approvals = Enumerable.Range(0, 16).Select(index =>
                consent.RequestAsync(new("agent-delegation", $"test {index}", Unattended: true), default)).ToArray();
            Check(approvals.All(approval => !approval.IsCompleted), "Unattended delegation bypassed consent.");
            consent.SetConnected(false);
            Check((await Task.WhenAll(approvals)).All(answer => !answer) && !consent.Requests.TryRead(out _),
                "Disconnect left stale approvals queued.");
            consent.SetConnected(true);
            var next = consent.RequestAsync(new("agent-delegation", "next"), default);
            var pending = await consent.Requests.ReadAsync();
            Check(!next.IsCompleted, "Reconnect implicitly approved agent delegation.");
            pending.Answer.TrySetResult(false);
            Check(!await next, "A rejected delegation was granted.");
        });
        await RunFollowupAsync(test);
        await RunDesktopAvailabilityAsync(test);
    }

    private static EndpointHello Hello(bool trusted = false) => new(Wire.ProtocolVersion, 42, "S-1-5-5-1-2", "Test device", trusted, true);
    private static bool Pending(BrokerHost host) => host.PeerConnections is [ { Status: PeerConnectionStatus.AwaitingApproval, SessionId: > 0 } ];
    private static bool Connected(BrokerHost host) => host.PeerConnections is [ { Status: PeerConnectionStatus.Connected } ];
    private static void Pair(Fixture first, Fixture second)
    {
        first.Identity.Trust(second.Identity.Fingerprint);
        second.Identity.Trust(first.Identity.Fingerprint);
    }
    private static async Task CheckLocalOnlyTargets(Fixture fixture)
    {
        Check((await fixture.Client.CallAsync("describe", new TargetRequest(null, true), default)).Data!.Value.GetArrayLength() > 0,
            "Authenticated local catalog access was gated on a remote channel.");
        await Throws<AgentLinkException>(() => fixture.Client.CallAsync("describe", new TargetRequest("not-ready"), default));
        var sessions = await fixture.Client.CallAsync("invoke", new RoutedOperation(null, true,
            new("agent_list_sessions", Json.Element(new { }), Guid.NewGuid().ToString("N"))), default);
        Check(sessions.Data!.Value.GetProperty("sessions").GetArrayLength() == 0 &&
            !fixture.Consent.IsConnected && !fixture.Consent.DesktopAllowed && !fixture.Consent.ObservationAllowed,
            "Local metadata access implicitly granted permissions.");
    }
    private static async Task Wait(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate())
            await Task.Delay(10, deadline.Token);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static async Task ThrowsAny(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception exception) when (exception is IOException or AuthenticationException or AgentLinkException or OperationCanceledException or ObjectDisposedException)
        { return; }
        throw new InvalidOperationException("Expected the unauthorised operation or TLS exchange to fail.");
    }
    private static async Task Drain(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (Exception exception) when (exception is IOException or AuthenticationException or AgentLinkException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    private static X509Certificate2 Certificate(DateTimeOffset? from = null, DateTimeOffset? until = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=RemoteAgentLink", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, true));
        using var generated = request.CreateSelfSigned(from ?? DateTimeOffset.UtcNow.AddMinutes(-1), until ?? DateTimeOffset.UtcNow.AddMinutes(10));
        var pfx = generated.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine("artifacts", "backend-tests", Guid.NewGuid().ToString("N")));
        private int _remoteOpenAttempts;
        internal string IdentityDirectory => Path.Combine(_root, "identity");
        internal string DataDirectory => Path.Combine(_root, "tasks");
        internal string Workspace => Path.Combine(_root, "workspace");
        internal string ControlPipe { get; } = $"RemoteAgentLink.Test.Control.{Guid.NewGuid():N}";
        internal EndpointIdentity Identity { get; }
        internal HumanConsent Consent { get; } = new();
        internal BrokerHost Host { get; }
        internal AgentLink.Broker.Shell.BrokerSession Session { get; }
        internal BrokerClient Client => new(ControlPipe);
        internal int RemoteOpenAttempts => Volatile.Read(ref _remoteOpenAttempts);
        internal Fixture(Action<EndpointIdentity>? beforeHost = null)
        {
            using var certificate = Certificate();
            Identity = new EndpointIdentity(IdentityDirectory, certificate);
            Directory.CreateDirectory(Workspace);
            beforeHost?.Invoke(Identity);
            Host = new(Identity, Consent, DataDirectory, "both",
                new(ControlPipe, $"RemoteAgentLink.Test.Transport.{Guid.NewGuid():N}", () =>
                {
                    Interlocked.Increment(ref _remoteOpenAttempts);
                    throw new IOException("Isolated test transport is idle.");
                }));
            Session = new(Identity, Consent, Host, DataDirectory, "both");
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Host.DisposeAsync();
            }
            finally
            {
                Identity.Dispose();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(_root, true);
            }
        }
    }

    private sealed class HostPair(TcpClient first, TcpClient second, Task outgoing, Task incoming) : IAsyncDisposable
    {
        internal static async Task<HostPair> Connect(BrokerHost local, BrokerHost remote)
        {
            var (first, second) = await SocketPair();
            return new(first, second, local.HandleOutgoingChannelAsync(first.GetStream()), remote.HandleIncomingChannelAsync(second.GetStream()));
        }
        public async ValueTask DisposeAsync()
        {
            first.Dispose();
            second.Dispose();
            await Drain(outgoing, incoming);
        }
    }

    private sealed class IncomingPeer(TcpClient socket, TcpClient accepted, SslStream tls, Task server, X509Certificate2? owned) : IAsyncDisposable
    {
        internal RpcPeer Peer { get; } = new(tls);
        internal static async Task<IncomingPeer> Connect(Fixture fixture, X509Certificate2? certificate = null)
        {
            var owned = certificate is null ? Certificate() : null;
            certificate ??= owned!;
            var (socket, accepted) = await SocketPair();
            var server = fixture.Host.HandleIncomingChannelAsync(accepted.GetStream());
            var tls = new SslStream(socket.GetStream(), false, (_, peer, _, _) =>
                peer is not null && EndpointIdentity.MatchesPinnedCertificate(peer, [fixture.Identity.Fingerprint], DateTime.UtcNow));
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await tls.AuthenticateAsClientAsync(new()
                {
                    TargetHost = "RemoteAgentLink", ClientCertificates = new() { certificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token);
                return new(socket, accepted, tls, server, owned);
            }
            catch
            {
                tls.Dispose();
                socket.Dispose();
                accepted.Dispose();
                owned?.Dispose();
                await Drain(server);
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            await Peer.DisposeAsync();
            socket.Dispose();
            accepted.Dispose();
            await Drain(server);
            owned?.Dispose();
        }
    }

    private static async Task<(TcpClient, TcpClient)> SocketPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var first = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await first.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            return (first, await accept);
        }
        finally { listener.Stop(); }
    }

    private static async Task WithLegacyServer(Fixture fixture, X509Certificate2 certificate, Func<Task> test,
        Func<Task>? beforeHelloReply = null)
    {
        var (socket, accepted) = await SocketPair();
        using var first = socket;
        using var second = accepted;
        using var tls = new SslStream(accepted.GetStream(), false, (_, peer, _, _) =>
            peer is not null && EndpointIdentity.MatchesPinnedCertificate(peer, [fixture.Identity.Fingerprint], DateTime.UtcNow));
        var outgoing = fixture.Host.HandleOutgoingChannelAsync(socket.GetStream());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await tls.AuthenticateAsServerAsync(new()
        {
            ServerCertificate = certificate, ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, deadline.Token);
        using var original = OriginalCore.TryOpen();
        var originalHello = original?.CreateHello() ?? Json.Element(new LegacyHello(Wire.ProtocolVersion, 42, "S-1-5-5-1-2"));
        Arguments.Read<LegacyHello>(originalHello);
        await using var peer = new RpcPeer(tls, async (request, _) =>
        {
            if (request.Method == "protocol.hello")
            {
                if (beforeHelloReply is not null)
                    await beforeHelloReply();
                return new OperationResult(Data: originalHello);
            }
            return new OperationResult(Data: Json.Element(new { protocolVersion = Wire.ProtocolVersion }));
        });
        try { await test(); }
        finally
        {
            socket.Dispose();
            accepted.Dispose();
            await Drain(outgoing);
        }
    }

    private sealed record LegacyHello(string ProtocolVersion, int SessionId, string LogonId);

    private sealed class OriginalCore : IDisposable
    {
        private readonly AssemblyLoadContext _context = new($"OriginalAgentLinkCore-{Guid.NewGuid():N}", isCollectible: true);
        private readonly Type _hello;
        private readonly MethodInfo _read;
        private readonly MethodInfo _element;

        private OriginalCore(Stream assemblyBytes)
        {
            var assembly = _context.LoadFromStream(assemblyBytes);
            _hello = assembly.GetType("AgentLink.Core.EndpointHello", throwOnError: true)!;
            Check(_hello.GetProperties().Select(property => property.Name).Order().SequenceEqual(
                new[] { "LogonId", "ProtocolVersion", "SessionId" }), "Archived hello is not the original three-field type.");
            _read = assembly.GetType("AgentLink.Core.Arguments", throwOnError: true)!.GetMethod("Read")!.MakeGenericMethod(_hello);
            _element = assembly.GetType("AgentLink.Core.Json", throwOnError: true)!.GetMethod("Element")!.MakeGenericMethod(_hello);
        }

        internal static OriginalCore? TryOpen()
        {
            var path = Path.Combine("artifacts", "packages", "RemoteAgentLink_0.1.0.1_x64.msix");
            if (!File.Exists(path))
                return null;
            using var package = ZipFile.OpenRead(path);
            using var entry = (package.GetEntry("Broker/AgentLink.Core.dll") ??
                throw new InvalidDataException("The original package is missing its core assembly.")).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            bytes.Position = 0;
            return new(bytes);
        }

        internal void ReadHello(JsonElement data) => _read.Invoke(null, [data]);

        internal void RequireRejected(JsonElement data)
        {
            try { ReadHello(data); }
            catch (TargetInvocationException exception) when (
                exception.InnerException?.GetType().FullName == "AgentLink.Core.AgentLinkException" &&
                exception.InnerException.GetType().GetProperty("Code")?.GetValue(exception.InnerException) as string == "invalid_arguments")
            { return; }
            throw new InvalidOperationException("The archived original deserializer accepted unnegotiated metadata.");
        }

        internal JsonElement CreateHello()
        {
            var hello = Activator.CreateInstance(_hello, [Wire.ProtocolVersion, 42, "S-1-5-5-1-2"])!;
            return (JsonElement)_element.Invoke(null, [hello])!;
        }

        public void Dispose() => _context.Unload();
    }

    private sealed class WaitingStream : Stream
    {
        internal bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanWrite => !Disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
