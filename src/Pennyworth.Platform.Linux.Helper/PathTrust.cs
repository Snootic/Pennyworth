namespace Pennyworth.Platform.Linux;

/// <summary>What a path is, without following a link at the end.</summary>
internal enum EntryKind
{
    Missing,
    File,
    Directory,
    Symlink,
    Other,
}

/// <summary>One <c>lstat</c> answer: the kind, the owner and the permission bits (<c>st_mode &amp; 07777</c>).</summary>
internal readonly record struct EntryInfo(EntryKind Kind, uint Uid, uint Mode);

internal enum TrustVerdict
{
    Trusted,

    /// <summary>Some component of the path does not exist.</summary>
    Missing,

    /// <summary>The path exists but a component is a link, is not owned by root, or can be changed by someone else.</summary>
    NotTrusted,
}

/// <summary>
/// Decides whether a path can be believed: the chain from <c>/</c> to it is something only root can change. The app uses it
/// before running the helper through <c>pkexec</c> (a helper that an ordinary user could replace would be a root escalation),
/// and the helper uses it before it deletes inside a system folder. The check walks every component with <c>lstat</c> semantics,
/// so a symbolic link anywhere in the chain, including at the end, makes the path untrusted rather than being followed.
/// </summary>
internal static class PathTrust
{
    /// <summary>The group and others write bits.</summary>
    private const uint GroupOrOtherWrite = 0x12;

    private const uint OwnerExecute = 0x40;

    /// <summary>What the kernel reports when <c>lstat</c> fails for a reason other than the path not existing: never a trustworthy owner.</summary>
    private static readonly EntryInfo Unreadable = new(EntryKind.Other, uint.MaxValue, 0x1FF);

    /// <summary><c>lstat</c> of <paramref name="absolutePath"/> through <c>statx(AT_SYMLINK_NOFOLLOW)</c>; <see cref="EntryKind.Missing"/> when it does not exist.</summary>
    internal static EntryInfo Lstat(string absolutePath)
    {
        int errno = SafeFs.StatPathNoFollow(absolutePath, out StatxBuffer buffer);
        if (errno == SafeFs.Enoent || errno == SafeFs.Enotdir)
        {
            return new EntryInfo(EntryKind.Missing, 0, 0);
        }

        if (errno != 0)
        {
            return Unreadable;
        }

        EntryKind kind = SafeFs.Format(in buffer) switch
        {
            SafeFs.FormatRegular => EntryKind.File,
            SafeFs.FormatDirectory => EntryKind.Directory,
            SafeFs.FormatSymlink => EntryKind.Symlink,
            _ => EntryKind.Other,
        };
        return new EntryInfo(kind, buffer.Uid, (uint)(buffer.Mode & 0xFFF));
    }

    /// <summary>
    /// Walks <c>/</c> and every component of <paramref name="absolutePath"/>. Every ancestor, <c>/</c> included, must be a real directory
    /// owned by root that group and others cannot write. The final entry must be a regular file owned by root, not writable by group or
    /// others, and executable by its owner. <see cref="TrustVerdict.Missing"/> when a component does not exist; any other violation
    /// (a link anywhere in the chain too) is <see cref="TrustVerdict.NotTrusted"/>.
    /// </summary>
    internal static TrustVerdict CheckExecutable(string absolutePath, Func<string, EntryInfo> lstat) =>
        Walk(absolutePath, lstat, static info =>
            info.Kind == EntryKind.File && info.Uid == 0 && (info.Mode & GroupOrOtherWrite) == 0 && (info.Mode & OwnerExecute) != 0);

    /// <summary>
    /// Like <see cref="CheckExecutable"/> for a folder the helper cleans: the ancestors are strict, the folder itself must be a real directory
    /// (not a link) owned by root. Its permission bits are not constrained, because <c>/var/crash</c> is world-writable with the sticky bit
    /// on some distributions; deleting inside it is safe because the helper never follows a link and never leaves the folder.
    /// </summary>
    internal static TrustVerdict CheckRootOwnedDirectory(string absolutePath, Func<string, EntryInfo> lstat) =>
        Walk(absolutePath, lstat, static info => info.Kind == EntryKind.Directory && info.Uid == 0);

    private static TrustVerdict Walk(string absolutePath, Func<string, EntryInfo> lstat, Func<EntryInfo, bool> finalIsTrusted)
    {
        if (!IsNormalizedAbsolute(absolutePath))
        {
            return TrustVerdict.NotTrusted;
        }

        string[] segments = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = "/";
        for (int i = 0; i <= segments.Length; i++)
        {
            if (i > 0)
            {
                current = current.Length == 1 ? "/" + segments[i - 1] : current + "/" + segments[i - 1];
            }

            EntryInfo info = lstat(current);
            if (info.Kind == EntryKind.Missing)
            {
                return TrustVerdict.Missing;
            }

            bool isFinal = i == segments.Length;
            bool trusted = isFinal
                ? finalIsTrusted(info)
                : info.Kind == EntryKind.Directory && info.Uid == 0 && (info.Mode & GroupOrOtherWrite) == 0;
            if (!trusted)
            {
                return TrustVerdict.NotTrusted;
            }
        }

        return TrustVerdict.Trusted;
    }

    /// <summary>An absolute path with no empty, <c>.</c> or <c>..</c> segment, no trailing slash (except <c>/</c> itself) and no NUL.</summary>
    internal static bool IsNormalizedAbsolute(string path)
    {
        if (path.Length == 0 || path[0] != '/' || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length == 1)
        {
            return true;
        }

        foreach (string segment in path[1..].Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }
}
