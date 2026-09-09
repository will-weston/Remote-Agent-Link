using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using AgentLink.Core;
using Microsoft.Win32.SafeHandles;

namespace AgentLink.Broker;

internal static class PipeSecurity
{
    public static NamedPipeServerStream CreateServer(string name, bool firstInstance)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Missing Windows identity.");
        var security = new System.IO.Pipes.PipeSecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var allocation = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, allocation, descriptor.Length);
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = allocation };
            // Reject network pipe clients in the kernel, not using client-supplied names.
            var handle = CreateNamedPipeW($@"\\.\pipe\{name}",
                0x40000003u | (firstInstance ? 0x00080000u : 0), 0x00000008, 16, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }

    public static uint VerifyLocalSession(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) ||
            !ProcessIdToSessionId(processId, out var sessionId) ||
            sessionId != (uint)Process.GetCurrentProcess().SessionId)
            throw new AgentLinkException("unauthorized_pipe", "Only local processes in this Windows logon session may connect.");
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token))
            throw new AgentLinkException("unauthorized_pipe", "The pipe client's Windows logon could not be verified.");
        using (token)
        using (var client = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
        {
            if (WindowsLogon.FromToken(client.AccessToken) != WindowsLogon.FromToken(current.AccessToken) ||
                !Equals(client.User, current.User))
                throw new AgentLinkException("unauthorized_pipe", "The pipe client belongs to a different Windows logon.");
        }
        return processId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes attributes);
}
