using System.Text.Json.Serialization;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// What the app and the root helper agree on: where the helper lives, the subcommands (each also a polkit
/// action), the exit codes and the JSON that crosses stdin and stdout. Both sides compile this one file, so they
/// cannot drift. Nothing here is a secret and nothing in a request is trusted by the helper: it validates every field.
/// </summary>
public static class HelperProtocol
{
    /// <summary>The only place the app will run the helper from (and the path the polkit policy names).</summary>
    public const string InstalledPath = "/usr/libexec/pennyworth/pennyworth-helper";

    /// <summary>The JSON <c>version</c> both sides write and expect; a helper that answers another version is outdated.</summary>
    public const int Version = 1;

    /// <summary>The longest stdin the helper reads; anything beyond is refused with <see cref="HelperExit.TooLarge"/>.</summary>
    public const int MaxRequestBytes = 32 * 1024 * 1024;

    /// <summary>The longest stdout the app reads from the helper.</summary>
    public const int MaxResponseBytes = 8 * 1024 * 1024;

    /// <summary>Most folders one <c>measure-folders</c> request may name.</summary>
    public const int MaxFolders = 512;

    /// <summary>Most files one <c>clean-system</c> request may list; the app sends no more and counts the rest as skipped.</summary>
    public const int MaxCleanFiles = 200_000;

    /// <summary>Longest a single helper run may work before it stops itself.</summary>
    public static readonly TimeSpan MaxRuntime = TimeSpan.FromMinutes(10);

    public const string ReadFirmwareTables = "read-firmware-tables";
    public const string MeasureSystemSpace = "measure-system-space";
    public const string MeasureFolders = "measure-folders";
    public const string CleanSystem = "clean-system";

    /// <summary>The polkit action ids of <c>app.pennyworth.policy</c>, one per subcommand.</summary>
    public static string ActionId(string subcommand) => "app.pennyworth." + subcommand;

    /// <summary>The subcommands, in the order the policy lists them.</summary>
    public static IReadOnlyList<string> Subcommands { get; } = [ReadFirmwareTables, MeasureSystemSpace, MeasureFolders, CleanSystem];

    /// <summary>The file the firmware tables are read from; the helper reads nothing else for <see cref="ReadFirmwareTables"/>.</summary>
    public const string FirmwareTablePath = "/sys/firmware/dmi/tables/DMI";

    /// <summary>Largest firmware table the helper returns (the real one is tens of kilobytes).</summary>
    public const int MaxFirmwareTableBytes = 4 * 1024 * 1024;
}

/// <summary>
/// Exit codes of the helper. 126 and 127 are pkexec's own ("dismissed" / "not authorised") and are never used here.
/// 0 is success; the others say why nothing was done. The message is on stderr, one line.
/// </summary>
public static class HelperExit
{
    public const int Ok = 0;

    /// <summary>An unexpected failure inside the helper.</summary>
    public const int Failed = 1;

    /// <summary>Unknown subcommand, wrong argument count or unreadable JSON: the caller did not follow the protocol.</summary>
    public const int Usage = 2;

    /// <summary>The request was understood and refused: invalid path, caller not a normal user, an allowlisted folder that is not safe.</summary>
    public const int Refused = 3;

    /// <summary>The request or an answer is larger than the fixed limits.</summary>
    public const int TooLarge = 4;

    /// <summary>The run was stopped at <see cref="HelperProtocol.MaxRuntime"/>.</summary>
    public const int TimedOut = 5;

    /// <summary>A system tool or file the action needs is not there.</summary>
    public const int Missing = 6;
}

/// <summary>Answer of <c>read-firmware-tables</c>.</summary>
/// <param name="Table"><c>/sys/firmware/dmi/tables/DMI</c>, base64.</param>
public sealed record FirmwareTablesResponse(int Version, string Table);

/// <summary>Answer of <c>measure-system-space &lt;mountpoint&gt;</c>. A part the helper could not measure is null.</summary>
/// <param name="SnapshotBytes">Bytes only snapshots hold: what deleting every snapshot would free.</param>
/// <param name="MetadataBytes">Reserved blocks plus, on btrfs, the metadata and system chunks in use; the same quantity the unprivileged measurement reports.</param>
public sealed record SystemSpaceResponse(int Version, string FileSystem, long? SnapshotBytes, long? MetadataBytes);

/// <summary>Request of <c>measure-folders</c>, on stdin.</summary>
public sealed record FoldersRequest(int Version, IReadOnlyList<string> Paths);

/// <summary>One folder of a <see cref="FoldersResponse"/>, counted the way the scan counts (allocated bytes, hard links once, no other file system).</summary>
public sealed record FolderSize(string Path, long SizeOnDisk, long LogicalSize, long Files, bool Complete);

/// <summary>Answer of <c>measure-folders</c>: one entry per accepted path, in request order. A refused path is left out.</summary>
public sealed record FoldersResponse(int Version, IReadOnlyList<FolderSize> Folders);

/// <summary>One file of the preview the person saw, as <c>clean-system</c> receives it.</summary>
/// <param name="Root">One of the rule's allowlisted folders (<see cref="SystemCleanupRules"/>), exactly as listed there.</param>
/// <param name="Path">Path below <paramref name="Root"/>, <c>/</c>-separated, never absolute, never containing <c>..</c>.</param>
/// <param name="Size">Size listed in the preview; reported as freed when the file is deleted.</param>
/// <param name="ModifiedTicks">Last write time when listed: UTC <see cref="DateTime.Ticks"/> (100 ns since 0001-01-01), what .NET reports. A file whose time differs now is left alone.</param>
public sealed record CleanFile(string Root, string Path, long Size, long ModifiedTicks);

/// <summary>Request of <c>clean-system &lt;ruleId&gt;</c>, on stdin: the preview, nothing else. The helper deletes only what it lists in its own allowlist AND the preview names.</summary>
public sealed record CleanRequest(int Version, IReadOnlyList<CleanFile> Files);

/// <summary>Answer of <c>clean-system</c>.</summary>
public sealed record CleanResponse(int Version, long FreedBytes, long Deleted, long Skipped);

/// <summary>
/// The rules the helper may clean and the folders it cleans for each, compiled in: the caller names a rule, never a path to
/// delete. The folders are those of the knowledge base's rules that have <c>requiresAdmin</c> (a test keeps both lists equal).
/// </summary>
public static class SystemCleanupRules
{
    private static readonly Dictionary<string, string[]> Folders = new(StringComparer.Ordinal)
    {
        ["system-package-cache"] =
        [
            "/var/cache/zypp/packages",
            "/var/cache/apt/archives",
            "/var/cache/pacman/pkg",
            "/var/cache/dnf",
            "/var/cache/libdnf5",
        ],
        ["system-crash-reports"] = ["/var/crash"],
    };

    /// <summary>The rule ids, for the policy test and the helper's usage text.</summary>
    public static IReadOnlyCollection<string> Ids => Folders.Keys;

    /// <summary>The allowlisted folders of <paramref name="ruleId"/>; null for any rule the helper does not know.</summary>
    public static IReadOnlyList<string>? FoldersOf(string ruleId) => Folders.GetValueOrDefault(ruleId);

    /// <summary>
    /// Names inside a rule's folder the helper never deletes even when the preview lists them: <c>lock</c> and <c>partial</c>
    /// in apt's archive (apt's own working files). Compared against the first path segment below the folder.
    /// </summary>
    public static bool IsKept(string root, string relativePath)
    {
        if (root != "/var/cache/apt/archives")
        {
            return false;
        }

        int slash = relativePath.IndexOf('/', StringComparison.Ordinal);
        string first = slash < 0 ? relativePath : relativePath[..slash];
        return first is "lock" or "partial";
    }
}

[JsonSerializable(typeof(FirmwareTablesResponse))]
[JsonSerializable(typeof(SystemSpaceResponse))]
[JsonSerializable(typeof(FoldersRequest))]
[JsonSerializable(typeof(FoldersResponse))]
[JsonSerializable(typeof(CleanRequest))]
[JsonSerializable(typeof(CleanResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
public sealed partial class HelperJson : JsonSerializerContext;
