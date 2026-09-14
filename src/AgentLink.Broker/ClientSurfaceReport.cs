using System.Buffers.Binary;
using AgentLink.Core;

namespace AgentLink.Broker;

internal sealed record ClientSurfaceReport(Guid Instance, Guid Epoch, ClientSurfaceInfo Surface)
{
    internal const int Size = 128;

    internal static async Task<ClientSurfaceReport?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[Size];
        if (await stream.ReadAsync(bytes.AsMemory(0, 1), cancellationToken) == 0)
            return null;
        await stream.ReadExactlyAsync(bytes.AsMemory(1), cancellationToken);
        return Parse(bytes);
    }

    private static ClientSurfaceReport Parse(byte[] bytes)
    {
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
        var instance = new Guid(bytes.AsSpan(8, 16));
        var epoch = new Guid(bytes.AsSpan(24, 16));
        var generation = U64(40);
        var hwnd = U64(48);
        var start = U64(56);
        var pid = U32(64);
        var session = U32(68);
        var status = U32(72);
        var result = I32(76);
        var width = I32(80);
        var height = I32(84);
        var viewWidth = I32(88);
        var viewHeight = I32(92);
        var x = I32(96);
        var y = I32(100);
        var scale = BitConverter.Int32BitsToSingle(I32(104));
        var flags = U32(108);
        var geometry = flags == 1;
        if (!bytes.AsSpan(0, 8).SequenceEqual("RALSRF1\0"u8) ||
            instance == Guid.Empty || epoch == Guid.Empty || generation == 0 ||
            status > 5 || flags > 1 || U64(112) != 0 || U64(120) != 0 ||
            (geometry && (status != 5 || result < 0 || hwnd == 0 || start == 0 || pid == 0 ||
                width is <= 0 or > 65536 || height is <= 0 or > 65536 ||
                viewWidth is <= 0 or > 65536 || viewHeight is <= 0 or > 65536 ||
                x is < 0 or > 65536 || y is < 0 or > 65536 || !float.IsFinite(scale) || scale <= 0 || scale > 32)) ||
            (!geometry && (hwnd != 0 || start != 0 || pid != 0 || session != 0 ||
                width != 0 || height != 0 || viewWidth != 0 || viewHeight != 0 || x != 0 || y != 0 || scale != 0)))
            throw new AgentLinkException("invalid_surface_report", "Invalid local RDP surface metadata record.");
        var name = status switch
        {
            0 => "pending",
            1 => "service_unavailable",
            2 => "service_error",
            3 => "unsupported_session",
            4 => "invalid_geometry",
            _ => "identity_ambiguous"
        };
        return new(instance, epoch, new(generation, name, result, geometry,
            geometry ? $"0x{hwnd:X}" : null, pid, start, session,
            width, height, viewWidth, viewHeight, x, y, scale));
    }
}
