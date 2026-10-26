using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Pennyworth.Cleanup;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The freedesktop.org Trash, behind the <see cref="IRecycleBin"/> seam. The home trash is
/// <c>$XDG_DATA_HOME/Trash</c>; every mount point may also hold <c>$topdir/.Trash/$uid</c> (honoured
/// only when <c>.Trash</c> is a real directory with the sticky bit, as the specification demands) and
/// <c>$topdir/.Trash-$uid</c>. Each trash has <c>files/</c> (the payloads) and <c>info/</c> (one
/// <c>.trashinfo</c> sidecar per payload, the counterpart of the Windows <c>$I</c> file).
/// <para>
/// <c>directorysizes</c> is deliberately left alone: it is only a cache that readers validate
/// against the sidecar's modification time and ignore for directories that no longer exist, while
/// rewriting it would race with the file manager that also writes it.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class XdgTrash(XdgPaths paths, Func<IReadOnlyList<string>> mountPoints, int uid) : IRecycleBin
{
    private const string InfoExtension = ".trashinfo";

    /// <summary>The trash folders to look in: where they are, and the volume root a relative <c>Path</c> hangs from.</summary>
    internal readonly record struct Root(string Folder, string? Topdir);

    /// <summary>The numeric id of the current user, for the <c>.Trash-$uid</c> folder names.</summary>
    public static int CurrentUid() => (int)GetUid();

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();

    public IReadOnlyList<CleanupFile> List()
    {
        var items = new List<CleanupFile>();
        foreach (Root root in Roots())
        {
            try
            {
                ListRoot(root, items);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable trash contributes nothing.
            }
        }

        items.Sort((a, b) => a.Size != b.Size ? b.Size.CompareTo(a.Size) : string.CompareOrdinal(a.Path, b.Path));
        return items;
    }

    /// <summary>
    /// Deletes the payload, then its sidecar. The item must sit directly in a <c>files</c> folder of a
    /// trash that exists now (not a symlink), and its sidecar must be the matching one; anything else
    /// is refused. False when it is already gone. Nothing outside <c>files/</c> and <c>info/</c> is touched.
    /// </summary>
    public bool Delete(CleanupFile item)
    {
        try
        {
            string payload = Path.GetFullPath(item.Path);
            string name = Path.GetFileName(payload);
            string? files = Path.GetDirectoryName(payload);
            if (name.Length == 0 || files is null || Path.GetFileName(files) != "files"
                || Path.GetDirectoryName(files) is not { } trash
                || !Roots().Any(root => root.Folder == trash)
                || IsLink(files))
            {
                return false;
            }

            string info = Path.Combine(trash, "info", name + InfoExtension);
            if (item.CompanionPath is { } given && Path.GetFullPath(given) != info)
            {
                return false;
            }

            if (!Exists(payload))
            {
                return false; // Restored or emptied elsewhere since it was listed.
            }

            DeleteTree(payload);
            File.Delete(info);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>There is no shell icon to redraw on Linux: file managers watch the trash folders themselves.</summary>
    public void RefreshIcon()
    {
    }

    private IReadOnlyList<Root> Roots()
    {
        var roots = new List<Root>();
        void Add(string folder, string? topdir)
        {
            if (!roots.Any(r => r.Folder == folder))
            {
                roots.Add(new Root(folder, topdir));
            }
        }

        Add(Path.Combine(paths.DataHome, "Trash"), null);
        IReadOnlyList<string> mounts;
        try
        {
            mounts = mountPoints();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return roots;
        }

        string id = uid.ToString(CultureInfo.InvariantCulture);
        foreach (string mount in mounts)
        {
            if (!Path.IsPathRooted(mount))
            {
                continue;
            }

            string top = Path.TrimEndingDirectorySeparator(mount);
            if (top.Length == 0)
            {
                top = "/";
            }

            string shared = Path.Combine(top, ".Trash");
            string perUser = Path.Combine(shared, id);
            if (IsSharedTrashUsable(shared) && Directory.Exists(perUser) && !IsLink(perUser))
            {
                Add(perUser, top);
            }

            string own = Path.Combine(top, ".Trash-" + id);
            if (Directory.Exists(own) && !IsLink(own))
            {
                Add(own, top);
            }
        }

        return roots;
    }

    /// <summary>The specification's test for <c>$topdir/.Trash</c>: a real directory (not a link) with the sticky bit.</summary>
    private static bool IsSharedTrashUsable(string folder)
    {
        try
        {
            return Directory.Exists(folder) && !IsLink(folder) && (File.GetUnixFileMode(folder) & UnixFileMode.StickyBit) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ListRoot(Root root, List<CleanupFile> items)
    {
        string infoFolder = Path.Combine(root.Folder, "info");
        string filesFolder = Path.Combine(root.Folder, "files");
        if (!Directory.Exists(infoFolder) || !Directory.Exists(filesFolder))
        {
            return;
        }

        foreach (string sidecar in Directory.EnumerateFiles(infoFolder, "*" + InfoExtension))
        {
            string name = Path.GetFileName(sidecar)[..^InfoExtension.Length];
            string payload = Path.Combine(filesFolder, name);
            try
            {
                if (name.Length == 0 || !Exists(payload) || TrashInfo.Parse(File.ReadAllText(sidecar)) is not { } info)
                {
                    continue; // Orphan or malformed: shown nowhere, deleted never.
                }

                string original = !Path.IsPathRooted(info.Path) && root.Topdir is { } top ? Path.Combine(top, info.Path) : info.Path;
                DateTime deleted = info.DeletedLocal is { } local
                    ? DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime()
                    : DateTime.MinValue;
                items.Add(new CleanupFile(payload, SizeOf(payload), deleted, original, sidecar));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Vanished or unreadable while listing: skip it.
            }
        }
    }

    /// <summary>A file's length, or a directory's total of file lengths. Links are never followed and count as nothing.</summary>
    private static long SizeOf(string path)
    {
        if (IsLink(path))
        {
            return 0;
        }

        if (!Directory.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        long total = 0;
        foreach (FileInfo file in new DirectoryInfo(path).EnumerateFiles("*", options))
        {
            total += file.Length;
        }

        return total;
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>True for anything at the path, including a dangling link.</summary>
    private static bool Exists(string path) => IsLink(path) || File.Exists(path) || Directory.Exists(path);

    /// <summary>Removes a file, link or directory tree. Links are unlinked, never entered; read-only folders are made writable first.</summary>
    private static void DeleteTree(string path)
    {
        if (IsLink(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            const UnixFileMode Needed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if ((File.GetUnixFileMode(path) & Needed) != Needed)
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | Needed);
            }

            foreach (string child in Directory.EnumerateFileSystemEntries(path).ToList())
            {
                DeleteTree(child);
            }

            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }
}

/// <summary>The pure part of reading a trash: the <c>.trashinfo</c> INI format and its percent-encoding.</summary>
internal static class TrashInfo
{
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss";

    /// <summary>What a <c>.trashinfo</c> sidecar says: the decoded original path and when it was trashed (local time), if present.</summary>
    internal readonly record struct Info(string Path, DateTime? DeletedLocal);

    /// <summary>Parses a sidecar; null when it has no <c>[Trash Info]</c> group or no <c>Path</c>. The first value of a key wins.</summary>
    internal static Info? Parse(string text)
    {
        string? path = null;
        string? date = null;
        bool inGroup = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('['))
            {
                if (inGroup)
                {
                    break;
                }

                inGroup = line.Trim() == "[Trash Info]";
                continue;
            }

            int eq = line.IndexOf('=');
            if (!inGroup || eq < 0 || line.StartsWith('#'))
            {
                continue;
            }

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..];
            if (key == "Path")
            {
                path ??= value;
            }
            else if (key == "DeletionDate")
            {
                date ??= value;
            }
        }

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        DateTime? deleted = DateTime.TryParseExact(date?.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : null;
        return new Info(PercentDecode(path), deleted);
    }

    /// <summary>Decodes <c>%XX</c> escapes as UTF-8 bytes; a stray <c>%</c> stays a literal percent sign.</summary>
    internal static string PercentDecode(string value)
    {
        if (!value.Contains('%'))
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        int run = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '%' || !HasTwoHex(value, i))
            {
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(value[run..i]));
            bytes.Add(byte.Parse(value.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            i += 2;
            run = i + 1;
        }

        bytes.AddRange(Encoding.UTF8.GetBytes(value[run..]));
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool HasTwoHex(string value, int percent) =>
        percent + 2 < value.Length && Uri.IsHexDigit(value[percent + 1]) && Uri.IsHexDigit(value[percent + 2]);
}
