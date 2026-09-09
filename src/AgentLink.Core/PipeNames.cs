using System.Diagnostics;
using System.Security.Principal;

namespace AgentLink.Core;

public static class PipeNames
{
    private static string Suffix => $"{WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("A Windows user identity is required.")}.{Process.GetCurrentProcess().SessionId}";
    public static string Transport => $"RemoteAgentLink.Transport.{Suffix}";
    public static string Control => $"RemoteAgentLink.Control.{Suffix}";
}
