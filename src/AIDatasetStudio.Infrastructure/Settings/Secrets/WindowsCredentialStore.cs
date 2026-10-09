using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AIDatasetStudio.Application.Settings;

namespace AIDatasetStudio.Infrastructure.Settings.Secrets;

/// <summary>
/// Windows Credential Manager: generic credentials of the signed-in user, kept on this computer
/// (not roamed) and protected by the user's login. They show in Control Panel › Credential
/// Manager › Windows Credentials as "AIDatasetStudio:ai-connection/…".
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialStore : ISecretStore
{
    private const string Prefix = "AIDatasetStudio:";
    private const uint GenericType = 1;
    private const uint PersistLocalMachine = 2;

    public SecretStoreKind Kind => SecretStoreKind.WindowsCredentialManager;

    public bool IsAvailable => true;

    public string? Read(string name)
    {
        try
        {
            if (!NativeMethods.CredReadW(Prefix + name, GenericType, 0, out var pointer))
            {
                return null;
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                {
                    return null;
                }

                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                NativeMethods.CredFree(pointer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool Write(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var bytes = Encoding.UTF8.GetBytes(secret);
        var target = Marshal.StringToHGlobalUni(Prefix + name);
        var user = Marshal.StringToHGlobalUni(Environment.UserName);
        var blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = user,
            };
            return NativeMethods.CredWriteW(in credential, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            // The key leaves no copy in unmanaged memory.
            for (var i = 0; i < bytes.Length; i++)
            {
                Marshal.WriteByte(blob, i, 0);
            }

            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(user);
            Marshal.FreeHGlobal(target);
        }
    }

    public void Delete(string name)
    {
        try
        {
            NativeMethods.CredDeleteW(Prefix + name, GenericType, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing could have been stored either.
        }
    }

    /// <summary>
    /// CREDENTIALW; FILETIME is two DWORDs, so the layout matches on 32 and 64 bits.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredReadW(string targetName, uint type, uint flags, out IntPtr credential);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredWriteW(in Credential credential, uint flags);

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredDeleteW(string targetName, uint type, uint flags);

        [LibraryImport("advapi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial void CredFree(IntPtr buffer);
    }
}
