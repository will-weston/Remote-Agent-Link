using AgentLink.Core;
using System.Text.RegularExpressions;

namespace AgentLink.Copilot;

internal static partial class PathPolicy
{
    internal static string Canonical(string value, bool directory = false)
    {
        Arguments.Required(value, "path", 4096);
        if (!Path.IsPathFullyQualified(value) || value.Length < 3 || value[1] != ':' ||
            value[2] != '\\' || value.AsSpan(2).Contains(':'))
            throw new AgentLinkException("invalid_path", "A fully qualified local Windows path is required.");
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentLinkException("invalid_path", "Invalid Windows path.");
        }
        for (string? item = full; item is not null; item = Path.GetDirectoryName(item))
        {
            if ((Directory.Exists(item) || File.Exists(item)) &&
                (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new AgentLinkException("invalid_path", "Reparse points are not authorized workspace paths.");
            var name = Path.GetFileName(item);
            if (name.EndsWith('.') || name.EndsWith(' '))
                throw new AgentLinkException("invalid_path", "Ambiguous Windows path component.");
        }
        if (directory && !Directory.Exists(full))
            throw new AgentLinkException("invalid_path", "The working directory must exist.");
        return full;
    }

    internal static bool Within(string candidate, string root) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + "\\", StringComparison.OrdinalIgnoreCase);

    internal static string Workspace(string path, string privateDirectory)
    {
        var full = Canonical(path, directory: true);
        if (Within(full, privateDirectory) || Within(privateDirectory, full))
            throw new AgentLinkException("invalid_path", "The workspace must not overlap broker private data.");
        return full;
    }

    internal static bool SafeCommand(string value) =>
        value.Length is > 0 and <= 1024 && CommandCharacters().IsMatch(value) &&
        new[] { "dotnet build", "dotnet test", "dotnet --info", "git status", "git diff", "git log", "git show" }
            .Any(prefix => value == prefix || value.StartsWith(prefix + " ", StringComparison.Ordinal));

    [GeneratedRegex(@"^[a-zA-Z0-9 ._:\\/=\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandCharacters();

    internal static bool Preauthorized(RuntimePermission permission, TaskGrant grant, string privateDirectory)
    {
        if (permission.ManagedApprovalRequired || permission.SandboxBypass ||
            permission.RequiresDesktop || permission.HasUrls || permission.HasRedirection)
            return false;
        try
        {
            bool AllowedPath(string path)
            {
                var full = Canonical(path);
                return Within(full, grant.WorkingDirectory) && !Within(full, privateDirectory);
            }
            return permission.Kind switch
            {
                "read" => grant.AllowWorkspaceRead && permission.Path is not null && AllowedPath(permission.Path),
                "write" => grant.AllowWorkspaceWrite && permission.Path is not null && AllowedPath(permission.Path),
                "shell" => permission.Command is not null && SafeCommand(permission.Command) &&
                    grant.ApprovedCommands.Contains(permission.Command, StringComparer.Ordinal) &&
                    permission.PossiblePaths is not null && permission.PossiblePaths.All(AllowedPath),
                _ => false
            };
        }
        catch (Exception ex) when (ex is AgentLinkException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
