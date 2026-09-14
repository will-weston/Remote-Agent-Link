using System.Buffers.Binary;
using AgentLink.Core;

namespace AgentLink.Broker;

// Separate from the raw TLS transport: this describes a local plugin instance,
// never an authenticated remote endpoint or permission to use a local HWND.
internal sealed record ClientConnectionReport(Guid Instance, Guid Epoch, uint State)
{
    internal const int Size = 48;

    internal static async Task<ClientConnectionReport?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[Size];
        if (await stream.ReadAsync(bytes.AsMemory(0, 1), cancellationToken) == 0)
            return null;
        await stream.ReadExactlyAsync(bytes.AsMemory(1), cancellationToken);
        var instance = new Guid(bytes.AsSpan(8, 16));
        var epoch = new Guid(bytes.AsSpan(24, 16));
        var state = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));
        if (!bytes.AsSpan(0, 8).SequenceEqual("RALCLT1\0"u8) ||
            instance == Guid.Empty || epoch == Guid.Empty || state > 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(44)) != 0)
            throw new AgentLinkException("invalid_client_report", "Invalid local RDP plugin lifecycle record.");
        return new(instance, epoch, state);
    }

    internal ConnectionInfo ToConnection() => new(
        $"rdp-client:{Instance:N}", null, null, null, Epoch.ToString("N"),
        $"RDP client instance {Instance:N}", "localRdpClient",
        State switch { 0 => "initialized", 1 => "connected", _ => "disconnected" },
        "unsupported_public_contract");
}
