using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ContinuityBridge.Windows;

// Only this application's exact target is accessed; no credential enumeration.
public static partial class DeviceCredentialStore
{
    public static void Save(string origin, string token)
    {
        if (token.Length != 64 || !token.All(char.IsAsciiHexDigit)) throw new ArgumentException("invalid_device_token");
        nint target = Marshal.StringToCoTaskMemUni(Target(origin));
        nint user = Marshal.StringToCoTaskMemUni("ContinuityBridge");
        byte[] bytes = Encoding.Unicode.GetBytes(token);
        nint blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = target, BlobSize = (uint)bytes.Length,
                Blob = blob, Persist = 2, UserName = user };
            if (!CredWrite(ref credential, 0)) throw new IOException("credential_store_failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (int i = 0; i < token.Length * 2; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeCoTaskMem(blob); Marshal.FreeCoTaskMem(user); Marshal.FreeCoTaskMem(target);
        }
    }

    public static string? Read(string origin)
    {
        if (!CredRead(Target(origin), 1, 0, out var pointer))
        {
            if (Marshal.GetLastPInvokeError() == 1168) return null;
            throw new IOException("credential_read_failed");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize != 128) throw new IOException("invalid_credential");
            return Marshal.PtrToStringUni(credential.Blob, 64);
        }
        finally { CredFree(pointer); }
    }

    public static void Delete(string origin)
    {
        if (!CredDelete(Target(origin), 1, 0) && Marshal.GetLastPInvokeError() != 1168) throw new IOException("credential_delete_failed");
    }

    private static string Target(string origin) => "ContinuityBridge.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)));

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredWrite(ref Credential credential, uint flags);
    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredRead(string target, uint type, uint flags, out nint credential);
    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredDelete(string target, uint type, uint flags);
    [LibraryImport("advapi32.dll")] private static partial void CredFree(nint buffer);
}
