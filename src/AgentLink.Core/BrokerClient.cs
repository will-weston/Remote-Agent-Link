using System.IO.Pipes;
using System.Text.Json.Serialization;

namespace AgentLink.Core;

public sealed record ConnectionInfo(
    string ConnectionId, string PeerFingerprint, int RemoteSessionId, string LogonId, string Epoch,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName = null);

public sealed record EndpointHello(string ProtocolVersion, int SessionId, string LogonId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? TrustedPeer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SupportsPendingPairing = null);
public sealed record RoutedOperation(string? ConnectionId, bool CurrentSession, OperationRequest Request);
public sealed record TargetRequest(string? ConnectionId, bool CurrentSession = false);

public sealed class BrokerClient(string? pipeName = null)
{
    public Task<OperationResult> ShowAsync(CancellationToken cancellationToken = default) =>
        CallAsync("ui.show", new { }, cancellationToken);

    public async Task<OperationResult> CallAsync(
        string method, object arguments, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName ?? PipeNames.Control, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(5000, cancellationToken);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
        await using var peer = new RpcPeer(pipe);
        return await peer.CallAsync(new(method, Json.Element(arguments)), cancellationToken);
    }
}
