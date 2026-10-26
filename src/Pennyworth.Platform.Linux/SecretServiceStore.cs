using Pennyworth.Assistant.Providers;

namespace Pennyworth.Platform.Linux;

/// <summary>The keyring operations <see cref="SecretServiceStore"/> needs; tests replace it.</summary>
internal interface ISecretBackend
{
    /// <summary>Stores the secret under the profile id, replacing any earlier one.</summary>
    void Store(string profileId, string label, string secret);

    /// <summary>The secret, or null when none is stored.</summary>
    string? Lookup(string profileId);

    /// <summary>Removes the secret; nothing stored is not an error.</summary>
    void Clear(string profileId);
}

/// <summary>The keyring answered with an error, or no keyring answered. The message is safe to show.</summary>
internal sealed class KeyringException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// API keys in the desktop keyring (the freedesktop Secret Service: GNOME Keyring, KWallet, KeePassXC...)
/// through libsecret, in the default collection under the attribute <c>profile=&lt;profile id&gt;</c>.
/// There is no other place for a key: when libsecret is missing or no service answers, <see cref="Save"/> throws
/// and nothing is written to a file or the environment. <see cref="Read"/> then finds no key and
/// <see cref="Delete"/> has nothing to remove, so the Ask screen shows "no key" and can still drop a profile.
/// </summary>
public sealed class SecretServiceStore : ICredentialStore
{
    internal const string NoKeyringMessage =
        "No keyring is available to keep the key. Install and unlock a Secret Service keyring "
        + "(GNOME Keyring or KWallet), sign in again, and retry. The key was not saved.";

    private const string LabelPrefix = "Pennyworth assistant key";

    private readonly ISecretBackend _backend;

    public SecretServiceStore()
        : this(new Libsecret())
    {
    }

    internal SecretServiceStore(ISecretBackend backend) => _backend = backend;

    /// <summary>True when libsecret loads and a keyring answers; never throws. Asks the service, so it can start it.</summary>
    public static bool IsAvailable() => IsAvailable(new Libsecret());

    internal static bool IsAvailable(ISecretBackend backend)
    {
        try
        {
            _ = backend.Lookup("pennyworth-availability-probe");
            return true;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return false;
        }
    }

    /// <exception cref="InvalidOperationException">No keyring is available or it refused the key; the key was not stored.</exception>
    /// <exception cref="ArgumentException">The secret contains a NUL character, which the keyring would silently cut off.</exception>
    public void Save(string profileId, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Contains('\0') || profileId.Contains('\0'))
        {
            throw new ArgumentException("The key and its name cannot contain NUL characters.");
        }

        try
        {
            _backend.Store(profileId, $"{LabelPrefix} ({profileId})", secret);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            throw ex as KeyringException ?? new KeyringException(NoKeyringMessage, ex);
        }
    }

    /// <summary>The key, or null when none is saved or no keyring can be asked.</summary>
    public string? Read(string profileId)
    {
        if (string.IsNullOrEmpty(profileId) || profileId.Contains('\0'))
        {
            return null;
        }

        try
        {
            return _backend.Lookup(profileId);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return null;
        }
    }

    /// <summary>Removes the key. Without a keyring there is none to remove, so that is not an error.</summary>
    public void Delete(string profileId)
    {
        if (string.IsNullOrEmpty(profileId) || profileId.Contains('\0'))
        {
            return;
        }

        try
        {
            _backend.Clear(profileId);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
        }
    }

    private static bool IsUnavailable(Exception ex) => ex is KeyringException or DllNotFoundException or EntryPointNotFoundException;
}
