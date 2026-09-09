using AgentLink.Core;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AgentLink.Copilot;

public sealed record LiveRuntimeRegistration(
    string RegistrationId, string Endpoint, string SessionId, string WorkingDirectory,
    string RuntimeVersion, string UserSid, string LogonId, DateTimeOffset ExpiresAt,
    string ProtectedConnectionToken);

internal sealed record ManagedRuntimeSettings(string ExecutablePath);

/// <summary>Local-human setup only. None of these methods is an MCP operation.</summary>
public static class LocalSetup
{
    private static readonly Lazy<JsonElement> RegistrationSchema = new(() =>
    {
        using var stream = typeof(LocalSetup).Assembly.GetManifestResourceStream(
            "AgentLink.Copilot.live-runtime-registration.schema.json")
            ?? throw new InvalidOperationException("The embedded registration schema is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });

    public static JsonElement RegistrationInputSchema => RegistrationSchema.Value;

    /// <summary>Imports a strict local-human registration document. Never expose this method through MCP.</summary>
    public static void RegisterInteractiveRuntimeJson(string dataDirectory, JsonElement registration)
    {
        if (registration.ValueKind != JsonValueKind.Object || registration.GetRawText().Length > 65536)
            throw new AgentLinkException("invalid_registration", "Expected a bounded local registration object.");
        var input = Arguments.Read<RegistrationDocument>(registration);
        if (input.SchemaVersion != 1)
            throw new AgentLinkException("invalid_registration", "Unsupported registration schema version.");
        if (!registration.TryGetProperty("expiresAt", out var expiryValue) ||
            expiryValue.ValueKind != JsonValueKind.String || !HasExplicitOffset(expiryValue.GetString()!))
            throw new AgentLinkException("invalid_registration", "Registration expiry must include an explicit UTC offset.");
        RegisterInteractiveRuntime(dataDirectory, input.RegistrationId, input.Endpoint, input.ConnectionToken,
            input.SessionId, input.WorkingDirectory, input.RuntimeVersion, input.ExpiresAt);
    }

    public static string EnsurePrivateDirectory(string dataDirectory)
    {
        var parent = PathPolicy.Canonical(dataDirectory);
        var path = PathPolicy.Canonical(Path.Combine(parent, "copilot"));
        Directory.CreateDirectory(path);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("A Windows user is required.");
        if (!user.Equals(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))))
            throw new AgentLinkException("invalid_data_directory", "Copilot private storage must be owned by the current Windows user.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
        return path;
    }

    public static void ConfigureManagedRuntime(string dataDirectory, string installedExecutablePath)
    {
        var path = PathPolicy.Canonical(installedExecutablePath);
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new AgentLinkException("invalid_runtime", "Choose an explicitly installed compatible runtime .exe.");
        using var store = new TaskStore(EnsurePrivateDirectory(dataDirectory));
        store.SetSetting("managed-runtime", new ManagedRuntimeSettings(path));
    }

    public static void RegisterInteractiveRuntime(
        string dataDirectory, string registrationId, string endpoint, string connectionToken,
        string sessionId, string workingDirectory, string runtimeVersion, DateTimeOffset expiresAt)
    {
        Arguments.Required(registrationId, nameof(registrationId), 128);
        Arguments.Required(sessionId, nameof(sessionId), 128);
        Arguments.Required(runtimeVersion, nameof(runtimeVersion), 128);
        Arguments.Required(connectionToken, nameof(connectionToken), 4096);
        Arguments.Required(endpoint, nameof(endpoint), 4096);
        if (connectionToken.Length < 32 || expiresAt <= DateTimeOffset.UtcNow || expiresAt > DateTimeOffset.UtcNow.AddDays(1))
            throw new AgentLinkException("invalid_registration", "Use a strong runtime-issued connection token and expiry within 24 hours.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address) ||
            uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || endpoint != endpoint.Trim() || !HasExplicitPort(uri))
            throw new AgentLinkException("invalid_registration", "Only an explicit literal loopback TCP endpoint is supported.");
        var directory = EnsurePrivateDirectory(dataDirectory);
        var workspace = PathPolicy.Workspace(workingDirectory, directory);
        var tokenBytes = Encoding.UTF8.GetBytes(connectionToken);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(tokenBytes,
                Encoding.UTF8.GetBytes("AgentLink:" + registrationId), DataProtectionScope.CurrentUser);
        }
        finally { CryptographicOperations.ZeroMemory(tokenBytes); }
        using var store = new TaskStore(directory);
        store.SetSetting("live:" + registrationId, new LiveRuntimeRegistration(
            registrationId, uri.GetLeftPart(UriPartial.Authority), sessionId, workspace, runtimeVersion,
            WindowsIdentity.GetCurrent().User!.Value, WindowsLogon.Current, expiresAt, Convert.ToBase64String(encrypted)));
    }

    private static bool HasExplicitPort(Uri uri)
    {
        var authority = uri.OriginalString[(uri.Scheme.Length + 3)..].TrimEnd('/');
        return authority.LastIndexOf(':') > authority.LastIndexOf(']');
    }

    private static bool HasExplicitOffset(string value) =>
        value.EndsWith('Z') || value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':';

    private sealed record RegistrationDocument(int SchemaVersion, string RegistrationId, string Endpoint,
        string ConnectionToken, string SessionId, string WorkingDirectory, string RuntimeVersion, DateTimeOffset ExpiresAt);

    internal static LiveRuntimeRegistration ReadRegistration(TaskStore store, string registrationId)
    {
        var registration = store.GetSetting<LiveRuntimeRegistration>("live:" + registrationId)
            ?? throw new AgentLinkException("runtime_not_registered", "A local human must register the interactive runtime first.");
        if (registration.UserSid != WindowsIdentity.GetCurrent().User?.Value ||
            registration.LogonId != WindowsLogon.Current || registration.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new AgentLinkException("invalid_registration", "Runtime registration is expired, belongs to another user/logon, or requires local re-registration.");
        return registration;
    }

    public static void ReconcileStoppedManagedSession(string dataDirectory, string sessionId)
    {
        var directory = EnsurePrivateDirectory(dataDirectory);
        using var writerLease = new FileStream(Path.Combine(directory, "writer.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var store = new TaskStore(directory);
        store.ReconcileStoppedManagedSession(sessionId);
    }

}
