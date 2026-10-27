using System.Runtime.InteropServices;

namespace Pennyworth.Core.Storage;

/// <summary>
/// Collects scan results into a compact, index-based tree. Not thread-safe:
/// scanners must serialize calls. Parents must be added before their children.
/// </summary>
public sealed class StorageTreeBuilder
{
    public const int RootIndex = 0;

    private readonly string _rootPath;
    private readonly List<NodeData> _nodes = [];
    private readonly List<string> _names = [];
    private readonly long[] _categoryBytes = new long[StorageCategories.Count];
    private bool _built;

    public StorageTreeBuilder(string rootPath, StorageCategory rootCategory)
    {
        _rootPath = rootPath;
        Add(-1, rootPath, rootCategory, NodeFlags.Directory, 0, 0);
    }

    public long FileCount { get; private set; }

    public long TotalBytes { get; private set; }

    public int AddDirectory(int parent, string name, StorageCategory category)
    {
        EnsureDirectory(parent);
        return Add(parent, name, category, NodeFlags.Directory, 0, 0);
    }

    public void AddFile(int parent, string name, long sizeOnDisk, long logicalSize, StorageCategory category)
    {
        EnsureDirectory(parent);
        Add(parent, name, category, NodeFlags.None, sizeOnDisk, logicalSize);
        FileCount++;
        TotalBytes += sizeOnDisk;
        _categoryBytes[(int)category] += sizeOnDisk;
    }

    /// <summary>Marks a directory whose contents could not be read.</summary>
    public void MarkLocked(int directory)
    {
        EnsureDirectory(directory);
        CollectionsMarshal.AsSpan(_nodes)[directory].Flags |= NodeFlags.Locked;
    }

    public long[] SnapshotCategoryBytes() => (long[])_categoryBytes.Clone();

    public StorageTree Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("Build can only be called once.");
        }

        _built = true;
        NodeData[] nodes = [.. _nodes];

        // Children always have a higher index than their parent, so one reverse pass
        // rolls every size up to the root.
        for (int i = nodes.Length - 1; i > 0; i--)
        {
            ref NodeData parent = ref nodes[nodes[i].Parent];
            parent.SizeOnDisk += nodes[i].SizeOnDisk;
            parent.LogicalSize += nodes[i].LogicalSize;
        }

        return new StorageTree(_rootPath, nodes, [.. _names], FileCount, TotalBytes, SnapshotCategoryBytes());
    }

    private int Add(int parent, string name, StorageCategory category, NodeFlags flags, long sizeOnDisk, long logicalSize)
    {
        if (_built)
        {
            throw new InvalidOperationException("The tree has already been built.");
        }

        int index = _nodes.Count;
        var node = new NodeData
        {
            Parent = parent,
            FirstChild = -1,
            NextSibling = -1,
            SizeOnDisk = sizeOnDisk,
            LogicalSize = logicalSize,
            Category = category,
            Flags = flags,
        };

        if (parent >= 0)
        {
            ref NodeData parentNode = ref CollectionsMarshal.AsSpan(_nodes)[parent];
            node.NextSibling = parentNode.FirstChild;
            parentNode.FirstChild = index;
        }

        _nodes.Add(node);
        _names.Add(name);
        return index;
    }

    private void EnsureDirectory(int index)
    {
        if ((uint)index >= (uint)_nodes.Count || (_nodes[index].Flags & NodeFlags.Directory) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Not a directory in this tree.");
        }
    }
}

[Flags]
internal enum NodeFlags : byte
{
    None = 0,
    Directory = 1,
    Locked = 2,

    /// <summary>A locked directory whose size was filled in afterwards (see <see cref="StorageTree.WithMeasuredFolders"/>).</summary>
    Measured = 4,
}

internal struct NodeData
{
    public int Parent;
    public int FirstChild;
    public int NextSibling;
    public long SizeOnDisk;
    public long LogicalSize;
    public StorageCategory Category;
    public NodeFlags Flags;
}
