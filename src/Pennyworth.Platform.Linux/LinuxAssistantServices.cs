using Pennyworth.Assistant.Providers;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The Ask screen's services with their files under the XDG data home and free space measured per file system.
/// The on-PC model is not offered yet: it needs the Linux inference runtime, and a download that cannot
/// run would be worse than no option.
/// </summary>
public static class LinuxAssistantServices
{
    public static AssistantServices Create(XdgPaths paths, ICredentialStore keys, Func<long> availableMemory, string platformName) =>
        AssistantServicesFactory.Create(
            keys,
            paths.ConversationFolder,
            paths.UsagePath,
            paths.ModelRoot,
            availableMemory,
            folder => FreeSpace(folder, Directory.Exists, DriveFreeSpace),
            canRunLocally: false,
            platformName: platformName);

    /// <summary>
    /// Free bytes for the user on the file system that holds <paramref name="folder"/>, which need not exist yet:
    /// the nearest existing parent decides (on Linux <c>Path.GetPathRoot</c> is always <c>/</c>, which is the wrong
    /// mount for /home or an external disk). 0 when it cannot be measured.
    /// </summary>
    internal static long FreeSpace(string folder, Func<string, bool> exists, Func<string, long> measure)
    {
        try
        {
            for (string? path = Path.GetFullPath(folder); path is not null; path = Path.GetDirectoryName(path))
            {
                if (exists(path))
                {
                    return measure(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return 0;
    }

    // On Unix DriveInfo takes any path and asks statvfs about it, so a subfolder reports its own mount.
    private static long DriveFreeSpace(string existingFolder) => new DriveInfo(existingFolder).AvailableFreeSpace;
}
