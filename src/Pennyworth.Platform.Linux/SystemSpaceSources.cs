namespace Pennyworth.Platform.Linux;

/// <summary>
/// Everything system-space measuring reads from the machine, as delegates so the orchestration runs
/// over fabricated mountinfo, sysfs and command output in tests. Each one answers null or empty when
/// the source is missing or unreadable: that is a normal answer, never an exception.
/// </summary>
/// <param name="ReadText">Whole text of a file (mountinfo, sysfs attributes, snapper configs).</param>
/// <param name="ListNames">Entry names of a directory.</param>
/// <param name="DeviceName">Kernel name of a mount source (<c>/dev/mapper/home</c> is <c>dm-0</c>).</param>
/// <param name="StatVfs"><c>statvfs</c> of a path.</param>
/// <param name="RunBtrfsUsage">Output of <c>btrfs filesystem usage -b</c> for a mount point.</param>
internal sealed record SystemSpaceSources(
    Func<string, string?> ReadText,
    Func<string, IReadOnlyList<string>> ListNames,
    Func<string, string> DeviceName,
    Func<string, VfsCounters?> StatVfs,
    Func<string, string?> RunBtrfsUsage)
{
    /// <summary>
    /// <c>btrfs</c> lives in <c>sbin</c>, which an ordinary user's <c>PATH</c> (and a desktop launcher's)
    /// often lacks, so the absolute locations are tried after the bare name.
    /// </summary>
    private static readonly string[] BtrfsPrograms = ["btrfs", "/usr/sbin/btrfs", "/sbin/btrfs"];

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    internal static SystemSpaceSources Real { get; } = new(ReadFile, ListDirectory, ResolveDeviceName, SystemSpaceStatVfs.Read, RunBtrfsUsageCommand);

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ListDirectory(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ResolveDeviceName(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return source;
        }

        try
        {
            return File.ResolveLinkTarget(source, returnFinalTarget: true)?.Name ?? Path.GetFileName(source);
        }
        catch (IOException)
        {
            return Path.GetFileName(source);
        }
    }

    private static string? RunBtrfsUsageCommand(string mountPoint)
    {
        foreach (string program in BtrfsPrograms)
        {
            if (ProcessProbe.Run(program, ["filesystem", "usage", "-b", mountPoint], CommandTimeout) is { } output)
            {
                return output;
            }
        }

        return null;
    }
}
