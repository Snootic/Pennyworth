using System.Reflection;
using System.Runtime.InteropServices;

namespace Pennyworth.Platform.Linux;

/// <summary>What stops working when a native part is missing.</summary>
public enum PreflightFeature
{
    /// <summary>API keys in the desktop keyring: <c>libsecret-1.so.0</c> and GLib.</summary>
    Keyring,

    /// <summary>Drive scanning, folder sizes, cleanup and the trash: functions of the system C library.</summary>
    Storage,

    /// <summary>Live numbers and closing apps: <c>/proc</c> and the system C library.</summary>
    Monitoring,
}

/// <summary>One part this machine lacks.</summary>
/// <param name="Feature">What cannot work without it.</param>
/// <param name="Missing">The library, function or path, as it would be written in an install command or a bug report.</param>
/// <param name="Message">One plain English sentence that names the part and what to do; no stack trace, no jargon beyond the part's own name.</param>
public sealed record PreflightProblem(PreflightFeature Feature, string Missing, string Message);

/// <summary>The result of <see cref="LinuxPreflight.Run()"/>.</summary>
public sealed record PreflightReport(IReadOnlyList<PreflightProblem> Problems)
{
    /// <summary>The keyring problem, or null when the keyring can be used. It disables the keyring and nothing else.</summary>
    public PreflightProblem? Keyring => Problems.FirstOrDefault(p => p.Feature == PreflightFeature.Keyring);

    /// <summary>The problems that leave the app without its core screens (scanning or live monitoring).</summary>
    public IReadOnlyList<PreflightProblem> Blocking => [.. Problems.Where(p => p.Feature != PreflightFeature.Keyring)];
}

/// <summary>
/// Looks for the native parts Pennyworth calls into before anything calls them, so a missing one becomes a plain
/// sentence and a disabled feature instead of a <see cref="DllNotFoundException"/> in the middle of a scan. Loading
/// uses the same probing rules as the P/Invokes, and nothing here talks to a service or asks for rights.
/// </summary>
public static class LinuxPreflight
{
    internal const string SecretLibrary = "libsecret-1.so.0";
    internal const string GlibLibrary = "libglib-2.0.so.0";
    internal const string CLibrary = "libc";

    // Exactly the functions the Linux project imports from libc, per feature.
    private static readonly string[] StorageFunctions = ["opendir", "readdir", "closedir", "dirfd", "statx", "statvfs", "getuid"];
    private static readonly string[] MonitoringFunctions = ["kill", "readlink"];
    private static readonly string[] MonitoringFiles = ["/proc/meminfo", "/proc/stat"];

    // The libsecret and GLib functions Libsecret.cs imports.
    private static readonly string[] SecretFunctions =
    [
        "secret_password_storev_sync", "secret_password_lookupv_sync", "secret_password_clearv_sync", "secret_password_free",
    ];

    private static readonly string[] GlibFunctions = ["g_hash_table_new_full", "g_hash_table_insert", "g_hash_table_unref", "g_error_free", "g_str_hash", "g_str_equal"];

    public static PreflightReport Run() => Run(new SystemNativeProbe());

    internal static PreflightReport Run(INativeProbe probe)
    {
        var problems = new List<PreflightProblem>();

        // The sentence names the library, not the function that failed: that is what the user installs.
        string? keyring = FirstMissing(probe, SecretLibrary, SecretFunctions) is null && FirstMissing(probe, GlibLibrary, GlibFunctions) is null
            ? null
            : SecretLibrary;
        if (keyring is not null)
        {
            problems.Add(new PreflightProblem(
                PreflightFeature.Keyring,
                keyring,
                $"API keys cannot be saved because the keyring library ({keyring}) is missing or too old; install the libsecret package to turn the keyring on."));
        }

        string? storage = FirstMissing(probe, CLibrary, StorageFunctions);
        if (storage is not null)
        {
            problems.Add(new PreflightProblem(
                PreflightFeature.Storage,
                storage,
                $"Scanning drives needs {storage}, which the system C library on this machine does not provide."));
        }

        string? monitoring = FirstMissing(probe, CLibrary, MonitoringFunctions)
            ?? MonitoringFiles.FirstOrDefault(file => !probe.FileExists(file));
        if (monitoring is not null)
        {
            problems.Add(new PreflightProblem(
                PreflightFeature.Monitoring,
                monitoring,
                $"Live monitoring needs {monitoring}, which this machine does not provide."));
        }

        return new PreflightReport(problems);
    }

    /// <summary>The first thing in <paramref name="library"/> that cannot be used: the library itself, or one of the functions.</summary>
    private static string? FirstMissing(INativeProbe probe, string library, string[] functions)
    {
        if (!probe.CanLoad(library))
        {
            return library;
        }

        return functions.FirstOrDefault(function => !probe.HasExport(library, function));
    }
}

/// <summary>The questions <see cref="LinuxPreflight"/> asks the machine; tests answer them.</summary>
internal interface INativeProbe
{
    bool CanLoad(string library);

    bool HasExport(string library, string function);

    bool FileExists(string path);
}

internal sealed class SystemNativeProbe : INativeProbe
{
    private static readonly Assembly Owner = typeof(SystemNativeProbe).Assembly;

    public bool CanLoad(string library) => NativeLibrary.TryLoad(library, Owner, null, out _);

    public bool HasExport(string library, string function) =>
        NativeLibrary.TryLoad(library, Owner, null, out nint handle) && NativeLibrary.TryGetExport(handle, function, out _);

    public bool FileExists(string path) => File.Exists(path);
}
