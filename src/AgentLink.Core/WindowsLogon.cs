using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AgentLink.Core;

public static class WindowsLogon
{
    public static string Current
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return FromToken(identity.AccessToken);
        }
    }

    public static string FromToken(SafeAccessTokenHandle token)
    {
        // WindowsIdentity.Groups filters SE_GROUP_LOGON_ID entries. Read the
        // unfiltered token groups instead of confusing a session ID with a logon.
        if (!GetTokenInformation(token, 2, IntPtr.Zero, 0, out var length) && Marshal.GetLastWin32Error() != 122)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("Invalid Windows token group data length.");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, 2, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var count = Marshal.ReadInt32(buffer);
            var offset = Marshal.OffsetOf<TokenGroups>(nameof(TokenGroups.FirstGroup)).ToInt32();
            var stride = Marshal.SizeOf<SidAndAttributes>();
            if (count < 0 || (long)offset + (long)count * stride > length)
                throw new InvalidDataException("Invalid Windows token group count.");
            for (var index = 0; index < count; index++)
            {
                var group = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(buffer, offset + index * stride));
                if ((group.Attributes & 0xC0000000u) != 0xC0000000u)
                    continue;
                var sid = new SecurityIdentifier(group.Sid).Value;
                if (!sid.StartsWith("S-1-5-5-", StringComparison.Ordinal))
                    throw new InvalidDataException("Unexpected Windows logon SID format.");
                return sid;
            }
            throw new InvalidOperationException("An interactive Windows logon SID is required.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroups
    {
        public uint Count;
        public SidAndAttributes FirstGroup;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
}
