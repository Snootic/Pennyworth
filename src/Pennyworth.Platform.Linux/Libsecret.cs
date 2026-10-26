using System.Runtime.InteropServices;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The Secret Service through libsecret's non-variadic <c>*v_sync</c> password calls, which take the
/// <c>SecretSchema</c> and the attribute <c>GHashTable</c> as plain pointers. A missing library surfaces as
/// <see cref="DllNotFoundException"/>/<see cref="EntryPointNotFoundException"/>; a service that does not answer
/// (absent, locked and refused, no default collection) as <see cref="KeyringException"/>.
/// </summary>
internal sealed unsafe partial class Libsecret : ISecretBackend
{
    private const string SecretLibrary = "libsecret-1.so.0";
    private const string GlibLibrary = "libglib-2.0.so.0";
    private const string DefaultCollection = "default";
    private const string SchemaName = "app.pennyworth.Assistant";
    private const string ProfileAttribute = "profile";

    // The C layout of SecretSchema (secret-schema.h, checked with sizeof/offsetof on libsecret 0.21):
    // name @0, flags @8, attributes[32] of { name, type } @16 (16 bytes each), then reserved fields up to 592.
    private const int SchemaSize = 592;
    private const int FlagsOffset = 8;
    private const int FirstAttributeOffset = 16;
    private const int AttributeTypeOffset = 8;
    private const int SchemaFlagsNone = 0;
    private const int SchemaAttributeString = 0;

    /// <summary>Offset of <c>message</c> in <c>GError { GQuark domain; gint code; gchar *message; }</c>.</summary>
    private const int ErrorMessageOffset = 8;

    private static readonly Lock Gate = new();

    // Built once and kept for the life of the process: the schema is read by every call.
    private static Native? s_native;

    public void Store(string profileId, string label, string secret)
    {
        Native native = Load();
        nint secretText = 0;
        nint labelText = 0;
        nint collection = 0;
        try
        {
            secretText = Marshal.StringToCoTaskMemUTF8(secret);
            labelText = Marshal.StringToCoTaskMemUTF8(label);
            collection = Marshal.StringToCoTaskMemUTF8(DefaultCollection);
            using var attributes = new Attributes(native, profileId);
            int stored = secret_password_storev_sync(native.Schema, attributes.Table, collection, labelText, secretText, 0, out nint error);
            if (stored == 0 || error != 0)
            {
                throw Failure(error, "store the key");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUTF8(secretText);
            Marshal.FreeCoTaskMem(labelText);
            Marshal.FreeCoTaskMem(collection);
        }
    }

    public string? Lookup(string profileId)
    {
        Native native = Load();
        using var attributes = new Attributes(native, profileId);
        nint found = secret_password_lookupv_sync(native.Schema, attributes.Table, 0, out nint error);
        if (error != 0)
        {
            throw Failure(error, "read the key");
        }

        if (found == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(found) ?? "";
        }
        finally
        {
            secret_password_free(found);
        }
    }

    public void Clear(string profileId)
    {
        Native native = Load();
        using var attributes = new Attributes(native, profileId);

        // FALSE with no error means nothing matched, which is what "deleted" means here too.
        _ = secret_password_clearv_sync(native.Schema, attributes.Table, 0, out nint error);
        if (error != 0)
        {
            throw Failure(error, "delete the key");
        }
    }

    private static KeyringException Failure(nint error, string action)
    {
        string detail = "";
        if (error != 0)
        {
            detail = Marshal.PtrToStringUTF8(*(nint*)(error + ErrorMessageOffset)) ?? "";
            g_error_free(error);
        }

        return new KeyringException(
            detail.Length == 0
                ? $"The keyring would not {action}."
                : $"The keyring would not {action}: {detail}");
    }

    private static Native Load()
    {
        lock (Gate)
        {
            return s_native ??= Native.Create();
        }
    }

    private sealed class Native
    {
        private Native(nint schema, nint stringHash, nint stringEqual, nint profileName)
        {
            Schema = schema;
            StringHash = stringHash;
            StringEqual = stringEqual;
            ProfileName = profileName;
        }

        public nint Schema { get; }

        public nint StringHash { get; }

        public nint StringEqual { get; }

        public nint ProfileName { get; }

        /// <exception cref="DllNotFoundException">libsecret or GLib is not installed.</exception>
        /// <exception cref="EntryPointNotFoundException">Either library lacks a symbol this code needs.</exception>
        public static Native Create()
        {
            if (!NativeLibrary.TryLoad(SecretLibrary, out _))
            {
                throw new DllNotFoundException($"{SecretLibrary} is not installed.");
            }

            if (!NativeLibrary.TryLoad(GlibLibrary, out nint glib))
            {
                throw new DllNotFoundException($"{GlibLibrary} is not installed.");
            }

            nint hash = NativeLibrary.GetExport(glib, "g_str_hash");
            nint equal = NativeLibrary.GetExport(glib, "g_str_equal");

            nint schema = (nint)NativeMemory.AllocZeroed(SchemaSize);
            nint profile = Marshal.StringToCoTaskMemUTF8(ProfileAttribute);
            *(nint*)schema = Marshal.StringToCoTaskMemUTF8(SchemaName);
            *(int*)(schema + FlagsOffset) = SchemaFlagsNone;
            *(nint*)(schema + FirstAttributeOffset) = profile;
            *(int*)(schema + FirstAttributeOffset + AttributeTypeOffset) = SchemaAttributeString;
            return new Native(schema, hash, equal, profile);
        }
    }

    /// <summary>The one-entry attribute table of a call; owns the value string.</summary>
    private sealed class Attributes : IDisposable
    {
        private readonly nint _value;

        public Attributes(Native native, string profileId)
        {
            _value = Marshal.StringToCoTaskMemUTF8(profileId);
            Table = g_hash_table_new_full(native.StringHash, native.StringEqual, 0, 0);
            _ = g_hash_table_insert(Table, native.ProfileName, _value);
        }

        public nint Table { get; }

        public void Dispose()
        {
            g_hash_table_unref(Table);
            Marshal.FreeCoTaskMem(_value);
        }
    }

    [LibraryImport(SecretLibrary)]
    private static partial int secret_password_storev_sync(nint schema, nint attributes, nint collection, nint label, nint password, nint cancellable, out nint error);

    [LibraryImport(SecretLibrary)]
    private static partial nint secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, out nint error);

    [LibraryImport(SecretLibrary)]
    private static partial int secret_password_clearv_sync(nint schema, nint attributes, nint cancellable, out nint error);

    /// <summary>Overwrites the password with zeros, then frees it.</summary>
    [LibraryImport(SecretLibrary)]
    private static partial void secret_password_free(nint password);

    [LibraryImport(GlibLibrary)]
    private static partial nint g_hash_table_new_full(nint hash, nint equal, nint keyDestroy, nint valueDestroy);

    [LibraryImport(GlibLibrary)]
    private static partial int g_hash_table_insert(nint table, nint key, nint value);

    [LibraryImport(GlibLibrary)]
    private static partial void g_hash_table_unref(nint table);

    [LibraryImport(GlibLibrary)]
    private static partial void g_error_free(nint error);
}
