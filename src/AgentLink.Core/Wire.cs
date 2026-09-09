using System.Buffers.Binary;
using System.Text.Json;

namespace AgentLink.Core;

public sealed record WireError(string Code, string Message);

public sealed record WireMessage(
    string Kind, string Id, string? Method = null, JsonElement? Arguments = null,
    OperationResult? Result = null, WireError? Error = null, string? ActorId = null);

public static class Wire
{
    public const int MaximumRequestBytes = 256 * 1024;
    public const int MaximumMessageBytes = 24 * 1024 * 1024;
    public const int MaximumImageBytes = 16 * 1024 * 1024;
    public const string ProtocolVersion = "1";

    public static async Task WriteAsync(Stream stream, WireMessage message, CancellationToken cancellationToken)
    {
        ValidateImage(message);
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json.Options); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new AgentLinkException("invalid_payload", "The operation payload cannot be encoded within the supported JSON limits.");
        }
        var maximum = message.Kind == "response" ? MaximumMessageBytes : MaximumRequestBytes;
        if (bytes.Length > maximum)
            throw new AgentLinkException("payload_too_large", $"Message exceeds {maximum} bytes.");
        var header = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        header[4] = message.Kind switch
        {
            "request" => 1,
            "response" => 2,
            "cancel" => 3,
            _ => throw new AgentLinkException("invalid_frame", "Unknown message kind.")
        };
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<WireMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (first == 0)
            return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var kind = header[4] switch
        {
            1 => "request", 2 => "response", 3 => "cancel",
            _ => throw new AgentLinkException("invalid_frame", "Unknown frame kind.")
        };
        var maximum = kind == "response" ? MaximumMessageBytes : MaximumRequestBytes;
        if (length <= 0 || length > maximum)
            throw new AgentLinkException("invalid_frame", "Invalid message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        WireMessage message;
        try
        {
            message = JsonSerializer.Deserialize<WireMessage>(bytes, Json.Options)
                ?? throw new AgentLinkException("invalid_frame", "Message cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new AgentLinkException("invalid_frame", exception.Message);
        }
        if (!Guid.TryParseExact(message.Id, "N", out _) ||
            message.Kind != kind)
            throw new AgentLinkException("invalid_frame", "Invalid message kind or request identity.");
        if (message.ActorId?.Length > 512)
            throw new AgentLinkException("invalid_frame", "Actor identity is too long.");
        if (kind == "cancel" && (message.Method is not null || message.Arguments is not null ||
            message.Result is not null || message.Error is not null || message.ActorId is not null))
            throw new AgentLinkException("invalid_frame", "Invalid cancellation envelope.");
        if (message.Kind == "request" && (length > MaximumRequestBytes ||
            string.IsNullOrWhiteSpace(message.Method) || message.Arguments is null ||
            message.Method.Length > 128 || message.Method.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.')) ||
            message.Arguments.Value.ValueKind != JsonValueKind.Object || message.Result is not null || message.Error is not null))
            throw new AgentLinkException("invalid_frame", "Invalid request envelope.");
        if (message.Kind == "response" &&
            (message.Method is not null || message.Arguments is not null || message.ActorId is not null ||
             (message.Result is null) == (message.Error is null)))
            throw new AgentLinkException("invalid_frame", "Invalid response envelope.");
        ValidateImage(message);
        return message;
    }

    private static void ValidateImage(WireMessage message)
    {
        if (message.Result?.Image is not { } image)
            return;
        if (image.Length > MaximumImageBytes)
            throw new AgentLinkException("payload_too_large", "Image exceeds the 16 MiB limit.");
        if (message.Result.MimeType != "image/png" || image.Length < 8 ||
            !image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new AgentLinkException("invalid_image", "Only PNG image artifacts are supported.");
    }

    public static async Task<OperationResult> CallAsync(
        Stream stream, string method, JsonElement arguments, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        await WriteAsync(stream, new WireMessage("request", id, method, arguments), cancellationToken);
        var response = await ReadAsync(stream, cancellationToken)
            ?? throw new EndOfStreamException("Peer disconnected before acknowledging the operation.");
        if (response.Kind != "response" || response.Id != id)
            throw new AgentLinkException("invalid_response", "Response does not match the outstanding operation.");
        if (response.Error is { } error)
            throw new AgentLinkException(error.Code, error.Message);
        return response.Result!;
    }
}
