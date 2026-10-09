using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIDatasetStudio.Application.Settings;

namespace AIDatasetStudio.Infrastructure.Settings.Secrets;

/// <summary>
/// The freedesktop Secret Service through libsecret: GNOME Keyring, KWallet (5.97 and later) or
/// KeePassXC, whichever serves the desktop session. Only the non-variadic entry points are used,
/// so the calls are plain P/Invoke on every architecture.
/// </summary>
/// <remarks>
/// Without a session bus, libsecret or a service that answers within a few seconds, the store is
/// unavailable for the session; that is common on minimal and headless desktops (ADR-041).
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("freebsd")]
internal sealed partial class LibSecretStore : ISecretStore
{
    private const string SecretLibrary = "libsecret-1.so.0";
    private const string GLibLibrary = "libglib-2.0.so.0";
    private const string SchemaName = "app.aidatasetstudio.Secret";
    private const string NameAttribute = "name";

    /// <summary>
    /// How long the first call may wait for the Secret Service. A service that D-Bus has to start
    /// and that never answers would otherwise hold the window for the bus's own 25 seconds.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly Lazy<bool> _available = new(ProbeInTime, LazyThreadSafetyMode.ExecutionAndPublication);

    public SecretStoreKind Kind => SecretStoreKind.SecretService;

    public bool IsAvailable => _available.Value;

    public string? Read(string name)
    {
        if (!IsAvailable)
        {
            return null;
        }

        return Call(name, (schema, attributes) =>
        {
            var secret = NativeMethods.secret_password_lookupv_sync(schema, attributes, IntPtr.Zero, out var error);
            if (TakeError(error) is not null || secret == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUTF8(secret);
            }
            finally
            {
                // Clears the memory before freeing it.
                NativeMethods.secret_password_free(secret);
            }
        });
    }

    public bool Write(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return IsAvailable && Call(name, (schema, attributes) =>
        {
            var stored = NativeMethods.secret_password_storev_sync(schema, attributes, null, "AI Dataset Studio: " + name, secret, IntPtr.Zero, out var error);
            return TakeError(error) is null && stored != 0;
        });
    }

    public void Delete(string name)
    {
        if (IsAvailable)
        {
            Call(name, (schema, attributes) =>
            {
                NativeMethods.secret_password_clearv_sync(schema, attributes, IntPtr.Zero, out var error);
                return TakeError(error) is null;
            });
        }
    }

    private static bool ProbeInTime()
    {
        var probe = Task.Run(Probe);
        return probe.Wait(ProbeTimeout) && probe.Result;
    }

    /// <summary>
    /// Whether a Secret Service answers: a session bus is announced, the libraries load, and a
    /// lookup that matches nothing comes back without an error. It unlocks nothing and asks nothing.
    /// </summary>
    private static bool Probe()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
            && (string.IsNullOrEmpty(runtime) || !File.Exists(Path.Combine(runtime, "bus"))))
        {
            return false;
        }

        try
        {
            if (!NativeLibrary.TryLoad(SecretLibrary, out _) || !NativeLibrary.TryLoad(GLibLibrary, out _))
            {
                return false;
            }

            return Call(".probe", (schema, attributes) =>
            {
                var secret = NativeMethods.secret_password_lookupv_sync(schema, attributes, IntPtr.Zero, out var error);
                if (secret != IntPtr.Zero)
                {
                    NativeMethods.secret_password_free(secret);
                }

                return TakeError(error) is null;
            });
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs one libsecret call with the app's schema and the attributes that name a secret.
    /// </summary>
    private static T Call<T>(string name, Func<IntPtr, IntPtr, T> call)
    {
        var glib = NativeLibrary.Load(GLibLibrary);
        var hash = NativeLibrary.GetExport(glib, "g_str_hash");
        var equal = NativeLibrary.GetExport(glib, "g_str_equal");
        var attributeName = Marshal.StringToCoTaskMemUTF8(NameAttribute);
        var attributeValue = Marshal.StringToCoTaskMemUTF8(name);
        var types = NativeMethods.g_hash_table_new_full(hash, equal, IntPtr.Zero, IntPtr.Zero);
        var attributes = NativeMethods.g_hash_table_new_full(hash, equal, IntPtr.Zero, IntPtr.Zero);
        var schema = IntPtr.Zero;
        try
        {
            // The attribute is a string (SECRET_SCHEMA_ATTRIBUTE_STRING, 0).
            NativeMethods.g_hash_table_insert(types, attributeName, IntPtr.Zero);
            schema = NativeMethods.secret_schema_newv(SchemaName, 0, types);
            NativeMethods.g_hash_table_insert(attributes, attributeName, attributeValue);
            return call(schema, attributes);
        }
        finally
        {
            if (schema != IntPtr.Zero)
            {
                NativeMethods.secret_schema_unref(schema);
            }

            NativeMethods.g_hash_table_unref(attributes);
            NativeMethods.g_hash_table_unref(types);
            Marshal.FreeCoTaskMem(attributeValue);
            Marshal.FreeCoTaskMem(attributeName);
        }
    }

    /// <summary>
    /// The message of a GError, which is freed; <c>null</c> when there was none.
    /// </summary>
    private static string? TakeError(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return null;
        }

        var message = Marshal.PtrToStringUTF8(Marshal.PtrToStructure<GError>(error).Message) ?? "error";
        NativeMethods.g_error_free(error);
        return message;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public IntPtr Message;
    }

    private static partial class NativeMethods
    {
        [LibraryImport(SecretLibrary, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr secret_schema_newv(string name, int flags, IntPtr attributeNamesAndTypes);

        [LibraryImport(SecretLibrary)]
        internal static partial void secret_schema_unref(IntPtr schema);

        [LibraryImport(SecretLibrary, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int secret_password_storev_sync(
            IntPtr schema, IntPtr attributes, string? collection, string label, string password, IntPtr cancellable, out IntPtr error);

        [LibraryImport(SecretLibrary)]
        internal static partial IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        [LibraryImport(SecretLibrary)]
        internal static partial int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        [LibraryImport(SecretLibrary)]
        internal static partial void secret_password_free(IntPtr password);

        [LibraryImport(GLibLibrary)]
        internal static partial IntPtr g_hash_table_new_full(IntPtr hash, IntPtr equal, IntPtr keyDestroy, IntPtr valueDestroy);

        [LibraryImport(GLibLibrary)]
        internal static partial int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

        [LibraryImport(GLibLibrary)]
        internal static partial void g_hash_table_unref(IntPtr table);

        [LibraryImport(GLibLibrary)]
        internal static partial void g_error_free(IntPtr error);
    }
}
