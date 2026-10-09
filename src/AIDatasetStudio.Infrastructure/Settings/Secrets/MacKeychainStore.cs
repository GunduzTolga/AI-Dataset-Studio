using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AIDatasetStudio.Application.Settings;

namespace AIDatasetStudio.Infrastructure.Settings.Secrets;

/// <summary>
/// The user's login keychain on macOS: generic passwords of the service "AI Dataset Studio",
/// one account per secret, visible in Keychain Access. The keychain functions used take plain
/// lengths and buffers, so no Core Foundation dictionaries need to be built.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacKeychainStore : ISecretStore
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int Success = 0;

    private static readonly byte[] Service = Encoding.UTF8.GetBytes("AI Dataset Studio");

    public SecretStoreKind Kind => SecretStoreKind.MacKeychain;

    public bool IsAvailable => true;

    public string? Read(string name)
    {
        try
        {
            var account = Encoding.UTF8.GetBytes(name);
            if (NativeMethods.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, out var length, out var data, out var item) != Success)
            {
                return null;
            }

            try
            {
                return data == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(data, (int)length);
            }
            finally
            {
                NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, data);
                Release(item);
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
        var password = Encoding.UTF8.GetBytes(secret);
        try
        {
            var account = Encoding.UTF8.GetBytes(name);
            if (NativeMethods.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, out _, out var data, out var item) == Success)
            {
                try
                {
                    return NativeMethods.SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password) == Success;
                }
                finally
                {
                    NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, data);
                    Release(item);
                }
            }

            return NativeMethods.SecKeychainAddGenericPassword(
                IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, (uint)password.Length, password, IntPtr.Zero) == Success;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Array.Clear(password);
        }
    }

    public void Delete(string name)
    {
        try
        {
            var account = Encoding.UTF8.GetBytes(name);
            if (NativeMethods.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, out _, out var data, out var item) == Success)
            {
                NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, data);
                NativeMethods.SecKeychainItemDelete(item);
                Release(item);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing could have been stored either.
        }
    }

    private static void Release(IntPtr item)
    {
        if (item != IntPtr.Zero)
        {
            NativeMethods.CFRelease(item);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport(SecurityFramework)]
        internal static partial int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
            out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [LibraryImport(SecurityFramework)]
        internal static partial int SecKeychainAddGenericPassword(
            IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
            uint passwordLength, byte[] passwordData, IntPtr itemRef);

        [LibraryImport(SecurityFramework)]
        internal static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

        [LibraryImport(SecurityFramework)]
        internal static partial int SecKeychainItemDelete(IntPtr itemRef);

        [LibraryImport(SecurityFramework)]
        internal static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [LibraryImport(CoreFoundationFramework)]
        internal static partial void CFRelease(IntPtr cf);
    }
}
