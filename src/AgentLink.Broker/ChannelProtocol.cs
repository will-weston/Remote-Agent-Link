using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentLink.Core;

namespace AgentLink.Broker;

internal static class ChannelProtocol
{
    internal const int MaximumMetadataBytes = 2048;
    internal const int MaximumDisplayNameLength = 63;

    internal static EndpointHello LocalHello(bool trusted, bool modern) =>
        new(Wire.ProtocolVersion, System.Diagnostics.Process.GetCurrentProcess().SessionId, WindowsLogon.Current,
            modern ? LocalDisplayName : null, modern ? trusted : null, modern ? true : null);

    private static string LocalDisplayName => ValidDisplayName(Environment.MachineName) ? Environment.MachineName : "Windows device";

    internal static bool ValidDisplayName(string? value) =>
        value is { Length: > 0 and <= MaximumDisplayNameLength } && value.Trim() == value &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' ');

    internal static EndpointHello ReadHello(JsonElement data)
    {
        ValidateObject(data);
        EndpointHello hello;
        try { hello = Arguments.Read<EndpointHello>(data); }
        catch (AgentLinkException) { throw InvalidMetadata(); }
        if (hello.ProtocolVersion != Wire.ProtocolVersion || hello.SessionId <= 0 || !ValidLogonId(hello.LogonId))
            throw InvalidMetadata();
        var modern = hello.SupportsPendingPairing == true;
        if (modern ? hello.TrustedPeer is null || !ValidDisplayName(hello.DisplayName) :
            hello.SupportsPendingPairing is not null || hello.TrustedPeer is not null || hello.DisplayName is not null)
            throw InvalidMetadata();
        return hello;
    }

    internal static bool IsEmpty(JsonElement data)
    {
        ValidateObject(data);
        return !data.EnumerateObject().Any();
    }

    internal static void RequireEmpty(JsonElement data)
    {
        if (!IsEmpty(data))
            throw InvalidMetadata();
    }

    internal static void ValidateHeartbeat(OperationResult result)
    {
        RequireDataOnly(result);
        var data = result.Data!.Value;
        ValidateObject(data);
        if (data.EnumerateObject().Count() != 1 || !data.TryGetProperty("protocolVersion", out var version) ||
            version.ValueKind != JsonValueKind.String || version.GetString() != Wire.ProtocolVersion)
            throw InvalidMetadata();
    }

    internal static EndpointHello ReadHelloResult(OperationResult result)
    {
        RequireDataOnly(result);
        return ReadHello(result.Data!.Value);
    }

    internal static void ValidateMetadataRequest(OperationRequest request)
    {
        if (request.ActorId is not null)
            throw InvalidMetadata();
        if (request.Method == "protocol.hello")
        {
            if (!IsEmpty(request.Arguments))
                ReadHello(request.Arguments);
        }
        else if (request.Method == "protocol.ping")
            RequireEmpty(request.Arguments);
        else
            throw new AgentLinkException("channel_not_ready", "Exchange authenticated endpoint metadata before using operations.");
    }

    internal static void ValidatePendingFrame(WireMessage message)
    {
        if (message.Kind == "request")
            ValidateMetadataRequest(new(message.Method!, message.Arguments!.Value, message.ActorId));
        else if (message.Kind == "response")
        {
            if (message.Error is { } error)
            {
                if (error.Code is not { Length: > 0 and <= 64 } || error.Message is not { Length: <= 256 } ||
                    error.Code.Any(char.IsControl) || error.Message.Any(char.IsControl))
                    throw InvalidMetadata();
                return;
            }
            RequireDataOnly(message.Result!);
            var data = message.Result!.Data!.Value;
            if (data.TryGetProperty("sessionId", out _))
                ReadHello(data);
            else
                ValidateHeartbeat(message.Result);
        }
    }

    private static void RequireDataOnly(OperationResult result)
    {
        if (result.Data is null || result.Image is not null || result.MimeType is not null || result.Text is not null)
            throw InvalidMetadata();
        ValidateObject(result.Data.Value);
    }

    private static void ValidateObject(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(data.GetRawText()) > MaximumMetadataBytes)
            throw InvalidMetadata();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
            if (!names.Add(property.Name))
                throw InvalidMetadata();
    }

    private static bool ValidLogonId(string? value)
    {
        if (value is null || value.Length > 40 || !value.StartsWith("S-1-5-5-", StringComparison.Ordinal))
            return false;
        var parts = value[8..].Split('-');
        return parts.Length == 2 && parts.All(part => part.Length is > 0 and <= 10 &&
            part.All(char.IsAsciiDigit) && uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static AgentLinkException InvalidMetadata() =>
        new("invalid_metadata", "The peer sent unsupported or malformed channel metadata. Update both endpoints and reconnect.");
}
