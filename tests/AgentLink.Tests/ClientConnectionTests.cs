using System.Buffers.Binary;
using System.IO.Pipes;
using AgentLink.Broker;
using AgentLink.Core;

internal static partial class BackendTests
{
    private static async Task RunClientConnectionsAsync(Func<string, Func<Task>, Task> test)
    {
        await test("surface metadata is versioned bounded and cannot authorize operations", async () =>
        {
            var record = SurfaceRecord(Guid.NewGuid(), Guid.NewGuid(), 1);
            using var split = new SplitStream(record);
            var parsed = await ClientSurfaceReport.ReadAsync(split, default);
            Check(parsed?.Surface is { Status: "identity_ambiguous", ProcessOwnershipVerified: true,
                ConnectionIdentityVerified: false, BackgroundInputSupported: false, FreshMinimizedCaptureSupported: false,
                RemoteWidth: 1920, RemoteHeight: 1080, ViewWidth: 960, Scale: 0.5f } &&
                await ClientSurfaceReport.ReadAsync(split, default) is null, "Metadata was promoted to an operation capability.");
            foreach (var offset in new[] { 0, 72, 108, 112, 120 })
            {
                var invalid = record.ToArray();
                invalid[offset] = 255;
                await Throws<AgentLinkException>(() => ClientSurfaceReport.ReadAsync(new MemoryStream(invalid), default));
            }
            foreach (var (offset, size) in new[] { (8, 16), (24, 16), (40, 8), (48, 8), (56, 8), (64, 4), (80, 4) })
            {
                var invalid = record.ToArray();
                Array.Clear(invalid, offset, size);
                await Throws<AgentLinkException>(() => ClientSurfaceReport.ReadAsync(new MemoryStream(invalid), default));
            }
            foreach (var scale in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, 33f })
            {
                var invalid = record.ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(104), BitConverter.SingleToInt32Bits(scale));
                await Throws<AgentLinkException>(() => ClientSurfaceReport.ReadAsync(new MemoryStream(invalid), default));
            }
            foreach (var count in new[] { 1, 7, 48, 127 })
                await Throws<EndOfStreamException>(() => ClientSurfaceReport.ReadAsync(new MemoryStream(record[..count]), default));
            for (uint status = 0; status <= 5; status++)
            {
                var unavailable = SurfaceRecord(Guid.NewGuid(), Guid.NewGuid(), 1, status, geometry: false);
                var report = await ClientSurfaceReport.ReadAsync(new MemoryStream(unavailable), default);
                Check(report is not null && !report.Surface.ProcessOwnershipVerified && report.Surface.Hwnd is null,
                    "Failed or pending metadata retained a stale HWND.");
            }
        });
        await test("surface metadata belongs to its lifecycle owner and epoch without widening supported tools", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            var instance = Guid.NewGuid();
            var epoch = Guid.NewGuid();
            await using var lifecycle = await OpenClientReporter(fixture);
            await lifecycle.WriteAsync(ClientRecord(instance, epoch, 1));
            await Wait(() => fixture.Host.Connections.Count == 1);
            await using var surface = await OpenSurfaceReporter(fixture);
            await surface.WriteAsync(SurfaceRecord(instance, epoch, 1));
            await Wait(() => fixture.Host.Connections.Single().LocalSurface is not null);
            var connection = fixture.Host.Connections.Single();
            var diagnosticResult = await fixture.Client.CallAsync("get", new TargetRequest(connection.ConnectionId), default);
            var diagnostic = diagnosticResult.Data!.Value.GetProperty("localSurface");
            Check(!diagnostic.GetProperty("backgroundInputSupported").GetBoolean() &&
                !diagnostic.GetProperty("freshMinimizedCaptureSupported").GetBoolean() &&
                diagnostic.GetProperty("backgroundInputUnsupportedReason").GetString()!.Contains("existing connection") &&
                diagnostic.GetProperty("freshMinimizedCaptureUnsupportedReason").GetString()!.Contains("output suppression"),
                "Read-only connection diagnostics omitted explicit operation capability blockers.");
            Check(connection.LocalSurfaceStatus == "identity_ambiguous" &&
                (await ClientInvokeError(fixture, connection.ConnectionId, "take_screenshot")).Code == "unsupported_on_endpoint",
                "A diagnostic HWND enabled capture.");
            Check((await fixture.Client.CallAsync("describe", new TargetRequest(connection.ConnectionId), default)).Data!.Value.GetArrayLength() == 0,
                "Surface metadata added supported tools.");
            await lifecycle.WriteAsync(ClientRecord(instance, epoch, 1));
            await surface.WriteAsync(SurfaceRecord(instance, epoch, 2));
            await Wait(() => fixture.Host.Connections.Single().LocalSurface?.Generation == 2);
            await using var duplicate = await OpenSurfaceReporter(fixture);
            await duplicate.WriteAsync(SurfaceRecord(instance, epoch, 3));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Check(await duplicate.ReadAsync(new byte[1], deadline.Token) == 0 &&
                fixture.Host.Connections.Single().LocalSurface?.Generation == 2, "A duplicate surface stream replaced its owner.");
            await lifecycle.WriteAsync(ClientRecord(instance, epoch, 2));
            await Wait(() => fixture.Host.Connections.Single().LocalSurface is null);
            var nextEpoch = Guid.NewGuid();
            await lifecycle.WriteAsync(ClientRecord(instance, nextEpoch, 1));
            await Wait(() => fixture.Host.Connections.Single().Epoch == nextEpoch.ToString("N"));
            await surface.WriteAsync(SurfaceRecord(instance, epoch, 3));
            await surface.WriteAsync(SurfaceRecord(instance, nextEpoch, 4, status: 0, geometry: false));
            await Wait(() => fixture.Host.Connections.Single().LocalSurface?.Generation == 4);
            Check(fixture.Host.Connections.Single().LocalSurface is { Status: "pending", Hwnd: null },
                "Reconnect retained an old HWND/generation.");
            await surface.DisposeAsync();
            await Wait(() => fixture.Host.Connections.Single().LocalSurface is null);
            Check(fixture.Host.Connections.Count == 1 && fixture.Host.IsRunning, "Surface loss removed lifecycle discovery.");
        });
        await test("surface generation retargeting and foreign reporter identities fail closed independently", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            var instance = Guid.NewGuid();
            var epoch = Guid.NewGuid();
            await using var lifecycle = await OpenClientReporter(fixture);
            await lifecycle.WriteAsync(ClientRecord(instance, epoch, 1));
            await Wait(() => fixture.Host.Connections.Count == 1);
            await Throws<AgentLinkException>(() => fixture.Host.HandleClientSurfaceAsync(
                new MemoryStream(SurfaceRecord(instance, epoch, 1)), "different-process:start-time"));
            Check(fixture.Host.Connections.Single().LocalSurface is null, "Foreign reporter published surface evidence.");
            await using var surface = await OpenSurfaceReporter(fixture);
            await surface.WriteAsync(SurfaceRecord(instance, epoch, 2));
            await Wait(() => fixture.Host.Connections.Single().LocalSurface is not null);
            await surface.WriteAsync(SurfaceRecord(instance, epoch, 1));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Check(await surface.ReadAsync(new byte[1], deadline.Token) == 0, "Regressing metadata was accepted.");
            await Wait(() => fixture.Host.Connections.Single().LocalSurface is null);
            await using var retarget = await OpenSurfaceReporter(fixture);
            await retarget.WriteAsync(SurfaceRecord(instance, epoch, 3));
            await Wait(() => fixture.Host.Connections.Single().LocalSurface is not null);
            await retarget.WriteAsync(SurfaceRecord(Guid.NewGuid(), epoch, 4));
            Check(await retarget.ReadAsync(new byte[1], deadline.Token) == 0, "Surface stream retargeting was accepted.");
            await Wait(() => fixture.Host.Connections.Single().LocalSurface is null);
        });
        await test("same-process surface candidates never merge plugin identities or prove HWND reuse", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var epoch = Guid.NewGuid();
            await using var firstLife = await OpenClientReporter(fixture);
            await using var secondLife = await OpenClientReporter(fixture);
            await firstLife.WriteAsync(ClientRecord(first, epoch, 1));
            await secondLife.WriteAsync(ClientRecord(second, epoch, 1));
            await Wait(() => fixture.Host.Connections.Count == 2);
            await using var firstSurface = await OpenSurfaceReporter(fixture);
            await using var secondSurface = await OpenSurfaceReporter(fixture);
            await firstSurface.WriteAsync(SurfaceRecord(first, epoch, 1));
            await secondSurface.WriteAsync(SurfaceRecord(second, epoch, 1));
            await Wait(() => fixture.Host.Connections.All(item => item.LocalSurface is not null));
            Check(fixture.Host.Connections.All(item => item.LocalSurface is
                { Status: "identity_ambiguous", ConnectionIdentityVerified: false, Hwnd: "0x1234" }),
                "A shared HWND/PID was taken as connection proof.");
            var nextEpoch = Guid.NewGuid();
            await firstLife.WriteAsync(ClientRecord(first, nextEpoch, 1));
            await Wait(() => fixture.Host.Connections.Single(item => item.ConnectionId == $"rdp-client:{first:N}").LocalSurface is null);
            Check(fixture.Host.Connections.Single(item => item.ConnectionId == $"rdp-client:{second:N}").LocalSurface?.Generation == 1,
                "One plugin epoch invalidated another instance.");
            await firstSurface.WriteAsync(SurfaceRecord(first, nextEpoch, 2));
            await Wait(() => fixture.Host.Connections.Single(item => item.ConnectionId == $"rdp-client:{first:N}").LocalSurface?.Generation == 2);
            Check(fixture.Host.Connections.All(item => item.LocalSurface?.ConnectionIdentityVerified == false),
                "Reusing a numeric HWND after reconnect granted connection identity.");
        });
        await test("optional surface listener failure preserves lifecycle and MCP control", async () =>
        {
            await using var fixture = new Fixture();
            using var occupied = AgentLink.Broker.PipeSecurity.CreateServer(fixture.TransportPipe + ".Clients.Surfaces", true);
            var diagnosed = false;
            fixture.Host.Log += message => diagnosed |= message.StartsWith("Local RDP surface listener unavailable:", StringComparison.Ordinal);
            fixture.Host.Start();
            await using var lifecycle = await OpenClientReporter(fixture);
            await lifecycle.WriteAsync(ClientRecord(Guid.NewGuid(), Guid.NewGuid(), 1));
            await Wait(() => fixture.Host.Connections.Count == 1);
            Check(diagnosed && fixture.Host.IsRunning && fixture.Host.Connections.Single().LocalSurface is null &&
                (await fixture.Client.CallAsync("list", new { }, default)).Data!.Value.GetArrayLength() == 1,
                "An unavailable optional diagnostic pipe took down lifecycle or control IPC.");
        });
        await test("local plugin lifecycle is discoverable without DVC or remote identity and cannot operate a desktop", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            await using var first = await OpenClientReporter(fixture);
            var instance = Guid.NewGuid();
            var epoch = Guid.NewGuid();
            var id = $"rdp-client:{instance:N}";
            await first.WriteAsync(ClientRecord(instance, epoch, 0));
            await Wait(() => fixture.Host.Connections.Count == 1);
            var connection = fixture.Host.Connections.Single();
            Check(connection.ConnectionId == id && connection.TargetKind == "localRdpClient" &&
                connection.State == "initialized" && connection.PeerFingerprint is null &&
                connection.RemoteSessionId is null && connection.LogonId is null &&
                connection.LocalSurfaceStatus == "unsupported_public_contract", "Client report fabricated endpoint identity or support.");
            Check(fixture.Host.PeerConnections.Count == 0, "Plugin discovery created a DVC channel.");
            var listed = await fixture.Client.CallAsync("list", new { }, default);
            Check(listed.Data!.Value.GetArrayLength() == 1, "MCP discovery omitted the local plugin.");
            var target = new TargetRequest(id);
            Check((await fixture.Client.CallAsync("get", target, default)).Data!.Value.GetProperty("connectionId").GetString() == id,
                "Explicit local-plugin selection changed identity.");
            Check((await fixture.Client.CallAsync("describe", target, default)).Data!.Value.GetArrayLength() == 0,
                "Client instance advertised unimplemented surface or remote tools.");
            foreach (var method in new[] { "desktop.acquire", "desktop.release", "take_screenshot", "press_keys", "agent_list_sessions" })
            {
                var error = await ClientInvokeError(fixture, id, method);
                Check(error.Code == "unsupported_on_endpoint", "Client-only operations did not fail closed.");
            }
            Check(!fixture.Consent.HasDesktopLease, "Client-only invocation acquired the unrestricted local desktop.");
            await fixture.Client.CallAsync("mcp.presence", new McpPresenceRequest(Guid.NewGuid().ToString("N"), id, false, true), default);
            Check(fixture.Host.GetAgentConnections().Count == 1, "Client-only presence required a remote broker.");
            fixture.Host.SetDesktopAccessEnabled(false);
            Check((await ClientInvokeError(fixture, id, "desktop.acquire")).Code == "device_access_disabled",
                "Client-only routing bypassed the local access switch.");
            Check(fixture.Host.Connections.Count == 1, "The access switch hid connection discovery.");

            await first.WriteAsync(ClientRecord(instance, epoch, 1));
            await Wait(() => fixture.Host.Connections.Single().State == "connected");
            await using var second = await OpenClientReporter(fixture);
            await second.WriteAsync(ClientRecord(Guid.NewGuid(), Guid.NewGuid(), 1));
            await Wait(() => fixture.Host.Connections.Count == 2);
            Check(fixture.Host.Connections.Select(item => item.ConnectionId).Distinct().Count() == 2,
                "Two plugin instances were merged by label or process.");
            await first.WriteAsync(ClientRecord(instance, epoch, 2));
            await Wait(() => fixture.Host.Connections.Single(item => item.ConnectionId == id).State == "disconnected");
            var nextEpoch = Guid.NewGuid();
            await first.WriteAsync(ClientRecord(instance, nextEpoch, 1));
            await Wait(() => fixture.Host.Connections.Single(item => item.ConnectionId == id).Epoch == nextEpoch.ToString("N"));
            Check(!fixture.Consent.HasDesktopLease, "Reconnect restored input ownership.");
            await first.DisposeAsync();
            await Wait(() => fixture.Host.Connections.Count == 1);
            await Throws<AgentLinkException>(() => fixture.Client.CallAsync("get", target, default));
            await second.DisposeAsync();
            await Wait(() => fixture.Host.Connections.Count == 0);
        });
        await test("lifecycle reporter cannot replace another owner or retarget an open stream", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            var instance = Guid.NewGuid();
            var epoch = Guid.NewGuid();
            await using var owner = await OpenClientReporter(fixture);
            await owner.WriteAsync(ClientRecord(instance, epoch, 1));
            await Wait(() => fixture.Host.Connections.Count == 1);
            await using var duplicate = await OpenClientReporter(fixture);
            await duplicate.WriteAsync(ClientRecord(instance, Guid.NewGuid(), 2));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Check(await duplicate.ReadAsync(new byte[1], deadline.Token) == 0, "Duplicate reporter was not closed.");
            Check(fixture.Host.Connections.Single().Epoch == epoch.ToString("N"), "Duplicate removed or replaced the existing owner.");
            await owner.WriteAsync(ClientRecord(Guid.NewGuid(), epoch, 1));
            Check(await owner.ReadAsync(new byte[1], deadline.Token) == 0, "Retargeted stream was not closed.");
            await Wait(() => fixture.Host.Connections.Count == 0);
        });
        await test("lifecycle records reject unknown protocol state zero identities and partial frames", async () =>
        {
            var record = ClientRecord(Guid.NewGuid(), Guid.NewGuid(), 1);
            using var split = new SplitStream(record);
            var report = await ClientConnectionReport.ReadAsync(split, default);
            Check(report?.State == 1 && await ClientConnectionReport.ReadAsync(split, default) is null, "Split reads or clean EOF failed.");
            foreach (var offset in new[] { 0, 40, 44 })
            {
                var invalid = record.ToArray();
                invalid[offset] = 255;
                using var stream = new MemoryStream(invalid);
                await Throws<AgentLinkException>(() => ClientConnectionReport.ReadAsync(stream, default));
            }
            foreach (var offset in new[] { 8, 24 })
            {
                var invalid = record.ToArray();
                Array.Clear(invalid, offset, 16);
                using var stream = new MemoryStream(invalid);
                await Throws<AgentLinkException>(() => ClientConnectionReport.ReadAsync(stream, default));
            }
            foreach (var count in new[] { 1, 7, 24, 47 })
            {
                using var stream = new MemoryStream(record[..count]);
                await Throws<EndOfStreamException>(() => ClientConnectionReport.ReadAsync(stream, default));
            }
        });
        await test("client lifecycle capacity cannot exhaust MCP control pipe slots", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            var reporters = new List<NamedPipeClientStream>();
            try
            {
                for (var index = 0; index < 16; index++)
                {
                    var reporter = await OpenClientReporter(fixture);
                    reporters.Add(reporter);
                    await reporter.WriteAsync(ClientRecord(Guid.NewGuid(), Guid.NewGuid(), 1));
                }
                await Wait(() => fixture.Host.Connections.Count == 16);
                Check(fixture.Host.IsRunning &&
                    (await fixture.Client.CallAsync("list", new { }, default)).Data!.Value.GetArrayLength() == 16,
                    "Full lifecycle capacity failed the broker or starved control IPC.");
            }
            finally
            {
                foreach (var reporter in reporters)
                    await reporter.DisposeAsync();
            }
            await Wait(() => fixture.Host.Connections.Count == 0);
        });
        await test("stalled lifecycle records expire and remove only their client instance", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Host.Start();
            await using var reporter = await OpenClientReporter(fixture);
            await reporter.WriteAsync(ClientRecord(Guid.NewGuid(), Guid.NewGuid(), 1));
            await Wait(() => fixture.Host.Connections.Count == 1);
            await reporter.WriteAsync(new byte[] { (byte)'R' });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Check(await reporter.ReadAsync(new byte[1], deadline.Token) == 0, "A partial lifecycle record retained discovery indefinitely.");
            await Wait(() => fixture.Host.Connections.Count == 0);
            Check(fixture.Host.IsRunning &&
                (await fixture.Client.CallAsync("list", new { }, default)).Data!.Value.GetArrayLength() == 0,
                "Reporter expiry stopped control IPC.");
        });
    }

    private static byte[] ClientRecord(Guid instance, Guid epoch, uint state)
    {
        var bytes = new byte[ClientConnectionReport.Size];
        "RALCLT1\0"u8.CopyTo(bytes);
        instance.TryWriteBytes(bytes.AsSpan(8, 16));
        epoch.TryWriteBytes(bytes.AsSpan(24, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), state);
        return bytes;
    }

    private static byte[] SurfaceRecord(Guid instance, Guid epoch, ulong generation, uint status = 5, bool geometry = true)
    {
        var bytes = new byte[ClientSurfaceReport.Size];
        "RALSRF1\0"u8.CopyTo(bytes);
        instance.TryWriteBytes(bytes.AsSpan(8, 16));
        epoch.TryWriteBytes(bytes.AsSpan(24, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), generation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), status);
        if (geometry)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), 0x1234);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), 123456);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(80), 1920);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(84), 1080);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(88), 960);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(92), 540);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(104), BitConverter.SingleToInt32Bits(0.5f));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108), 1);
        }
        return bytes;
    }

    private static async Task<NamedPipeClientStream> OpenSurfaceReporter(Fixture fixture)
    {
        var pipe = new NamedPipeClientStream(".", fixture.TransportPipe + ".Clients.Surfaces", PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(5000); return pipe; }
        catch { pipe.Dispose(); throw; }
    }

    private static async Task<NamedPipeClientStream> OpenClientReporter(Fixture fixture)
    {
        var pipe = new NamedPipeClientStream(".", fixture.TransportPipe + ".Clients", PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(5000); return pipe; }
        catch { pipe.Dispose(); throw; }
    }

    private static async Task<AgentLinkException> ClientInvokeError(Fixture fixture, string id, string method)
    {
        try
        {
            await fixture.Client.CallAsync("invoke", new RoutedOperation(id, false,
                new(method, Json.Element(new { }), Guid.NewGuid().ToString("N"))), default);
        }
        catch (AgentLinkException exception) { return exception; }
        throw new InvalidOperationException("A discovery-only client executed an operation.");
    }
}
