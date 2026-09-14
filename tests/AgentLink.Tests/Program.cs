using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AgentLink.Broker;
using AgentLink.Core;

var passed = 0;
await Test("frame round trip and byte-at-a-time reads", async () =>
{
    using var output = new MemoryStream();
    var expected = new WireMessage("request", Guid.NewGuid().ToString("N"), "test", Json.Element(new { text = "example" }), ActorId: "actor");
    await Wire.WriteAsync(output, expected, default);
    using var input = new SplitStream(output.ToArray());
    var actual = await Wire.ReadAsync(input, default);
    Check(actual?.Id == expected.Id && actual.ActorId == "actor", "Frame identity was not preserved.");
    Check(await Wire.ReadAsync(input, default) is null, "Expected clean EOF.");
});
await Test("truncated headers and bodies are not clean EOF", async () =>
{
    using var header = new MemoryStream([1]);
    await Throws<EndOfStreamException>(() => Wire.ReadAsync(header, default));
    using var body = new MemoryStream([20, 0, 0, 0, 1, 123]);
    await Throws<EndOfStreamException>(() => Wire.ReadAsync(body, default));
});
await Test("negative and oversized frames rejected before payload allocation", async () =>
{
    foreach (var length in new[] { -1, 0, Wire.MaximumRequestBytes + 1, int.MaxValue })
    {
        var bytes = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        bytes[4] = 1;
        using var stream = new MemoryStream(bytes);
        await Throws<AgentLinkException>(() => Wire.ReadAsync(stream, default));
    }
});
await Test("kind mismatch, malformed JSON and invalid envelopes rejected", async () =>
{
    foreach (var json in new[]
    {
        "not json",
        """{"kind":"response","id":"01234567890123456789012345678901","result":{}}""",
        """{"kind":"request","id":"not-a-guid","method":"test","arguments":{}}""",
        """{"kind":"request","id":"01234567890123456789012345678901","method":"test","arguments":{},"extra":1}"""
    })
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var frame = new byte[bytes.Length + 5];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length);
        frame[4] = 1;
        bytes.CopyTo(frame, 5);
        using var stream = new MemoryStream(frame);
        await Throws<AgentLinkException>(() => Wire.ReadAsync(stream, default));
    }
});
await Test("concurrent RPC replies preserve identities", async () =>
{
    await WithPeers(async (client, _) =>
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(number => client.CallAsync(new("echo", Json.Element(new { number })), default)));
        Check(results.Select(result => result.Data!.Value.GetProperty("number").GetInt32()).SequenceEqual(Enumerable.Range(0, 12)), "Responses were reordered.");
    }, async (request, ct) =>
    {
        await Task.Delay(Random.Shared.Next(1, 8), ct);
        return new(Data: request.Arguments);
    });
});
await Test("cancellation interrupts handler without poisoning connection", async () =>
{
    await WithPeers(async (client, _) =>
    {
        using var cancellation = new CancellationTokenSource(30);
        await Throws<OperationCanceledException>(() => client.CallAsync(new("wait", Json.Element(new { })), cancellation.Token));
        var result = await client.CallAsync(new("echo", Json.Element(new { okay = true })), default);
        Check(result.Data!.Value.GetProperty("okay").GetBoolean(), "Connection failed after cancelling one request.");
    }, async (request, ct) =>
    {
        if (request.Method == "wait")
            await Task.Delay(10000, ct);
        return new(Data: request.Arguments);
    });
});
await Test("peer closure reports unacknowledged operation", async () =>
{
    await WithPeers(async (client, server) =>
    {
        var call = client.CallAsync(new("wait", Json.Element(new { })), default);
        await server.DisposeAsync();
        await Throws<Exception>(async () => await call);
    }, async (_, ct) =>
    {
        await Task.Delay(10000, ct);
        return new(Text: "not reached");
    });
});
await Test("oversized handler response fails explicitly without corrupting the link", async () =>
{
    await WithPeers(async (client, _) =>
    {
        await Throws<AgentLinkException>(() => client.CallAsync(new("oversized", Json.Element(new { })), default));
        var result = await client.CallAsync(new("echo", Json.Element(new { })), default);
        Check(result.Text == "okay", "An over-limit response corrupted later records.");
    }, (request, _) => Task.FromResult(request.Method == "oversized"
        ? new OperationResult(Image: new byte[Wire.MaximumImageBytes + 1], MimeType: "image/png")
        : new OperationResult(Text: "okay")));
});
await Test("handler faults preserve indeterminate outcomes without leaking private details", async () =>
{
    await WithPeers(async (client, _) =>
    {
        try
        {
            await client.CallAsync(new("partial", Json.Element(new { })), default);
            throw new InvalidOperationException("A failed handler returned success.");
        }
        catch (AgentLinkException exception)
        {
            Check(exception.Code == "operation_failed" &&
                exception.Message.Contains("may have executed", StringComparison.Ordinal) &&
                !exception.Message.Contains("PRIVATE-PAYLOAD-SENTINEL", StringComparison.Ordinal),
                "A handler fault hid its indeterminate outcome or exposed private details.");
        }
        Check((await client.CallAsync(new("echo", Json.Element(new { })), default)).Text == "okay",
            "A handler fault corrupted later requests.");
    }, (request, _) => request.Method == "partial"
        ? throw new IOException("PRIVATE-PAYLOAD-SENTINEL")
        : Task.FromResult(new OperationResult(Text: "okay")));
});
await Test("excessively nested handler data returns an error rather than hanging", async () =>
{
    using var document = System.Text.Json.JsonDocument.Parse(new string('[', 40) + "0" + new string(']', 40));
    await WithPeers(async (client, _) =>
    {
        await Throws<AgentLinkException>(() => client.CallAsync(new("deep", Json.Element(new { })), default));
        Check((await client.CallAsync(new("echo", Json.Element(new { })), default)).Text == "okay", "Invalid output poisoned the connection.");
    }, (request, _) => Task.FromResult(request.Method == "deep"
        ? new OperationResult(Data: document.RootElement.Clone())
        : new OperationResult(Text: "okay")));
});
await Test("certificate pins enforce identity and validity without installing certificates", () =>
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=AgentLink test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
    var fingerprint = EndpointIdentity.FingerprintOf(certificate);
    Check(EndpointIdentity.MatchesPinnedCertificate(certificate, [fingerprint], DateTime.UtcNow), "Correct pin rejected.");
    Check(!EndpointIdentity.MatchesPinnedCertificate(certificate, [new string('0', 64)], DateTime.UtcNow), "Wrong pin accepted.");
    Check(!EndpointIdentity.MatchesPinnedCertificate(certificate, [fingerprint], DateTime.UtcNow.AddDays(1)), "Expired certificate accepted.");
    return Task.CompletedTask;
});
await Test("revocation after TLS validation prevents connection admission", async () =>
{
    using var certificate = CreateTlsCertificate();
    var gate = new object();
    string[] pins = [EndpointIdentity.FingerprintOf(certificate)];
    Check(EndpointIdentity.MatchesPinnedCertificate(certificate, pins, DateTime.UtcNow), "Initial certificate validation failed.");
    lock (gate) pins = [];
    var registered = false;
    await Throws<AuthenticationException>(() =>
    {
        PeerAdmission.Admit(gate, () => pins, certificate, () => registered = true);
        return Task.CompletedTask;
    });
    Check(!registered, "A handshake admitted a peer after its pin was revoked.");
});
await Test("peer registration is atomic with trust mutation", async () =>
{
    using var certificate = CreateTlsCertificate();
    var gate = new object();
    string[] pins = [EndpointIdentity.FingerprintOf(certificate)];
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    using var revocationStarted = new ManualResetEventSlim();
    var registered = false;
    var revocationSawRegistration = false;
    var admission = Task.Run(() => PeerAdmission.Admit(gate, () => pins, certificate, () =>
    {
        entered.Set();
        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        registered = true;
    }));
    Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "Admission did not begin.");
    var revocation = Task.Run(() =>
    {
        revocationStarted.Set();
        lock (gate)
        {
            pins = [];
            revocationSawRegistration = registered;
            registered = false;
        }
    });
    Check(await Task.Run(() => revocationStarted.Wait(TimeSpan.FromSeconds(5))), "Revocation did not begin.");
    release.Set();
    await Task.WhenAll(admission, revocation);
    Check(revocationSawRegistration && !registered, "Revocation missed an in-flight registration.");
});
await Test("paired mutual TLS carries a real framed RPC exchange", async () =>
{
    using var serverCertificate = CreateTlsCertificate();
    using var clientCertificate = CreateTlsCertificate();
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        using var socket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var accepted = await accept;
        using var serverTls = new SslStream(accepted.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && EndpointIdentity.MatchesPinnedCertificate(certificate,
                [EndpointIdentity.FingerprintOf(clientCertificate)], DateTime.UtcNow));
        using var clientTls = new SslStream(socket.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && EndpointIdentity.MatchesPinnedCertificate(certificate,
                [EndpointIdentity.FingerprintOf(serverCertificate)], DateTime.UtcNow));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            serverTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token),
            clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "RemoteAgentLink",
                ClientCertificates = new X509CertificateCollection { clientCertificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token));
        Check(clientTls.IsMutuallyAuthenticated && serverTls.IsMutuallyAuthenticated, "TLS did not authenticate both endpoints.");
        await using var server = new RpcPeer(serverTls, (request, _) => Task.FromResult(new OperationResult(Data: request.Arguments)));
        await using var client = new RpcPeer(clientTls);
        var result = await client.CallAsync(new("echo", Json.Element(new { paired = true })), timeout.Token);
        Check(result.Data!.Value.GetProperty("paired").GetBoolean(), "RPC failed over authenticated TLS.");
    }
    finally { listener.Stop(); }
});
await Test("device access grants immediately and isolates desktop owners", async () =>
{
    var consent = new HumanConsent();
    consent.SetConnected(true);
    OperationContext.ActorId.Value = "actor-a";
    var request = consent.RequestAsync(new("desktop-control", "test"), default);
    Check(await request && consent.DesktopAllowed && consent.ObservationAllowed && !consent.Requests.TryRead(out _),
        "Enabled device did not authorize without an approval.");
    consent.SetPrompting(true);
    Check(!consent.DesktopAllowed && !consent.ObservationAllowed, "Desktop access enabled during human approval UI.");
    consent.SetPrompting(false);
    OperationContext.ActorId.Value = "actor-b";
    Check(!consent.DesktopAllowed && consent.ObservationAllowed, "Another actor inherited input control or could not observe.");
    await Throws<AgentLinkException>(() => { consent.ReleaseDesktop(); return Task.CompletedTask; });
    await Throws<AgentLinkException>(() => consent.RequestAsync(new("desktop-control", "test"), default));
    consent.SetConnected(false);
    consent.SetConnected(true);
    OperationContext.ActorId.Value = "actor-a";
    Check(!consent.DesktopAllowed, "Reconnect restored an old desktop lease.");
});
await Test("disconnect invalidates authorization epochs without queuing approval", async () =>
{
    var consent = new HumanConsent();
    consent.SetConnected(true);
    OperationContext.ActorId.Value = "pending-actor";
    Check(await consent.RequestAsync(new("observe", "test"), default), "Observation was not automatic.");
    var epoch = consent.DesktopAccessLifetime;
    consent.SetConnected(false);
    consent.SetConnected(true);
    Check(epoch.IsCancellationRequested && !consent.Requests.TryRead(out _), "Old work survived connection replacement.");
});
await Test("secured pipe validates the actual connecting Windows session", async () =>
{
    var name = $"RemoteAgentLink.Test.{Guid.NewGuid():N}";
    await using var server = PipeSecurity.CreateServer(name, true);
    await using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
        System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
    var accept = server.WaitForConnectionAsync();
    await client.ConnectAsync(1000);
    await accept;
    Check(PipeSecurity.VerifyLocalSession(server) == (uint)Environment.ProcessId, "Wrong IPC process identity.");
});
await BackendTests.RunAsync(Test);
await BackendTests.RunDesktopAvailabilityAsync(Test);
Console.WriteLine($"All {passed} core/security tests passed.");
return 0;

async Task Test(string name, Func<Task> test)
{
    await test().WaitAsync(TimeSpan.FromSeconds(20));
    passed++;
    Console.WriteLine($"PASS {name}");
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task WithPeers(Func<RpcPeer, RpcPeer, Task> test,
    Func<OperationRequest, CancellationToken, Task<OperationResult>> handler)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        using var socket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var accepted = await accept;
        await using var server = new RpcPeer(accepted.GetStream(), handler);
        await using var client = new RpcPeer(socket.GetStream());
        await test(client, server);
    }
    finally { listener.Stop(); }
}

static X509Certificate2 CreateTlsCertificate()
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=RemoteAgentLink", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, true));
    using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
    var pfx = generated.Export(X509ContentType.Pfx);
    try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
    finally { CryptographicOperations.ZeroMemory(pfx); }
}

internal sealed class SplitStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}
