using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentLink.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}

public sealed record ToolDefinition(
    string Name, string Description, JsonElement InputSchema, bool Mutating, string Capability);

public sealed record OperationRequest(string Method, JsonElement Arguments, string? ActorId = null);

public static class OperationContext
{
    public static AsyncLocal<string?> ActorId { get; } = new();
}

public sealed record OperationResult(
    JsonElement? Data = null, byte[]? Image = null, string? MimeType = null, string? Text = null);

public sealed class AgentLinkException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IOperationHandler
{
    IReadOnlyList<ToolDefinition> Tools { get; }
    Task<OperationResult> InvokeAsync(OperationRequest request, CancellationToken cancellationToken);
}

public sealed record ConsentRequest(
    string Capability, string Summary, string? WorkingDirectory = null, bool Unattended = false);

public interface IConsentService
{
    bool IsConnected { get; }
    bool DesktopAllowed { get; }
    bool ObservationAllowed => false;
    Task<bool> RequestAsync(ConsentRequest request, CancellationToken cancellationToken);
    void RevokeDesktop();
}

public static class Arguments
{
    public static T Read<T>(JsonElement value)
    {
        try
        {
            return value.Deserialize<T>(Json.Options)
                ?? throw new AgentLinkException("invalid_arguments", "Arguments cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new AgentLinkException("invalid_arguments", exception.Message);
        }
    }

    public static string Required(string? value, string name, int maximumLength = 32768)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new AgentLinkException("invalid_arguments", $"{name} is required and must fit within {maximumLength} characters.");
        return value;
    }
}
