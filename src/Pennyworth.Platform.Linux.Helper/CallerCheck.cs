using System.Globalization;
using System.Runtime.InteropServices;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Where <see cref="CallerCheck"/> learns about the process and the machine. The real set reads the current process, the user
/// database and <c>/etc/login.defs</c>; a test supplies its own, so nothing a test does can change what runs in production.
/// </summary>
/// <param name="EffectiveUserId"><c>geteuid()</c>.</param>
/// <param name="PkexecUid">The value of <c>PKEXEC_UID</c> (set by pkexec to the user who authenticated), null when it is not set.</param>
/// <param name="UserExists">Whether the user database knows the user id (<c>getpwuid_r</c>).</param>
/// <param name="LoginDefs">The text of <c>/etc/login.defs</c>, null when it cannot be read.</param>
internal sealed record CallerSources(Func<uint> EffectiveUserId, Func<string?> PkexecUid, Func<uint, bool> UserExists, Func<string?> LoginDefs)
{
    /// <summary>Longest <c>/etc/login.defs</c> read: the real one is a few kilobytes.</summary>
    private const int MaxLoginDefsBytes = 256 * 1024;

    internal static CallerSources Real { get; } = new(
        static () => CallerCheckNative.GetEffectiveUserId(),
        static () => Environment.GetEnvironmentVariable("PKEXEC_UID"),
        static uid => CallerCheckNative.UserExists(uid),
        static () => ReadLoginDefs());

    private static string? ReadLoginDefs()
    {
        try
        {
            using var stream = new FileStream("/etc/login.defs", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] buffer = new byte[MaxLoginDefsBytes];
            int length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Who runs the helper: whether it has root rights and, if so, the user who asked through pkexec.</summary>
/// <param name="IsRoot">The effective user id is 0.</param>
/// <param name="RequestingUid">The verified <c>PKEXEC_UID</c> when <paramref name="IsRoot"/>; null otherwise.</param>
internal readonly record struct Caller(bool IsRoot, uint? RequestingUid);

/// <summary>
/// The helper's one check about its caller. As root it trusts no environment, with one exception: <c>PKEXEC_UID</c>, which pkexec
/// sets after authentication, must name an existing ordinary user (a uid in the range <c>/etc/login.defs</c> gives to people, never 0).
/// Run by anything else with root rights (a <c>sudo</c> shell, a service) there is no such user and the helper refuses.
/// Without root rights nothing is checked: the helper then has the caller's own rights and can do no more than the caller can.
/// </summary>
internal static class CallerCheck
{
    internal const uint DefaultUidMin = 1000;
    internal const uint DefaultUidMax = 60000;

    /// <exception cref="HelperException"><see cref="HelperExit.Refused"/> when the helper runs as root and <c>PKEXEC_UID</c> is absent, malformed, 0, unknown or outside the ordinary users' range.</exception>
    internal static Caller Verify(CallerSources sources)
    {
        if (sources.EffectiveUserId() != 0)
        {
            return new Caller(false, null);
        }

        string? text = sources.PkexecUid();
        if (text is null)
        {
            throw Refuse("it was not started through pkexec");
        }

        if (text.Length is 0 or > 10 || !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint uid))
        {
            throw Refuse("the requesting user is not a number");
        }

        (uint min, uint max) = ParseUidRange(sources.LoginDefs());
        if (uid == 0 || uid < min || uid >= max)
        {
            throw Refuse("the requesting user is not an ordinary user");
        }

        if (!sources.UserExists(uid))
        {
            throw Refuse("the requesting user does not exist");
        }

        return new Caller(true, uid);
    }

    /// <summary>
    /// <c>UID_MIN</c> and <c>UID_MAX</c> of <c>login.defs</c> (<c>KEY value</c>, <c>#</c> starts a comment). A value that is missing,
    /// not a plain number, or a range that is empty falls back to the defaults 1000 and 60000, as <c>useradd</c> does.
    /// </summary>
    internal static (uint Min, uint Max) ParseUidRange(string? loginDefs)
    {
        uint? min = null;
        uint? max = null;
        foreach (string raw in (loginDefs ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
            {
                continue;
            }

            switch (fields[0])
            {
                case "UID_MIN":
                    min = value;
                    break;
                case "UID_MAX":
                    max = value;
                    break;
            }
        }

        uint low = min ?? DefaultUidMin;
        uint high = max ?? DefaultUidMax;
        return low < high ? (low, high) : (DefaultUidMin, DefaultUidMax);
    }

    private static HelperException Refuse(string reason) => new(HelperExit.Refused, "refused: " + reason);
}

internal static unsafe partial class CallerCheckNative
{
    private const int Erange = 34;
    private const int InitialBuffer = 1024;
    private const int MaxBuffer = 1024 * 1024;

    /// <summary><c>struct passwd</c> is 48 bytes on the 64-bit Linux targets; the room is generous because only the result pointer is read.</summary>
    private const int PasswdBytes = 128;

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserIdNative();

    [LibraryImport("libc", EntryPoint = "getpwuid_r")]
    private static partial int GetPwUidR(uint uid, byte* passwd, byte* buffer, nuint bufferLength, byte** result);

    internal static uint GetEffectiveUserId() => GetEffectiveUserIdNative();

    /// <summary>Whether the user database (files, SSSD, LDAP... as the system is configured) has an entry for <paramref name="uid"/>.</summary>
    internal static bool UserExists(uint uid)
    {
        byte* passwd = stackalloc byte[PasswdBytes];
        for (int size = InitialBuffer; size <= MaxBuffer; size *= 4)
        {
            byte[] buffer = new byte[size];
            byte* result;
            int error;
            fixed (byte* bytes = buffer)
            {
                error = GetPwUidR(uid, passwd, bytes, (nuint)buffer.Length, &result);
            }

            if (error == Erange)
            {
                continue;
            }

            return error == 0 && result != null;
        }

        return false;
    }
}
