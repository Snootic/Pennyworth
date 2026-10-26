namespace Pennyworth.Platform.Linux.Tests;

/// <summary>A throwaway <c>/proc</c>-and-<c>/sys</c> tree for a specs reader to look at instead of the real machine.</summary>
internal sealed class SpecsTree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("pennyworth-specs-").FullName;

    public SpecsTree File(string path, string content)
    {
        string full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return this;
    }

    public SpecsTree Dir(string path)
    {
        Directory.CreateDirectory(Full(path));
        return this;
    }

    /// <summary>A symlink at <paramref name="path"/>; a relative <paramref name="target"/> is relative to the link's folder, as in sysfs.</summary>
    public SpecsTree Link(string path, string target)
    {
        string full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.CreateSymbolicLink(full, target);
        return this;
    }

    public string Full(string path) => Path.Combine(Root, path.TrimStart('/'));

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
