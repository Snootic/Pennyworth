namespace Pennyworth.Core.Storage;

/// <summary>An immutable scan result. Directory sizes include everything below them.</summary>
public sealed class StorageTree
{
    private readonly NodeData[] _nodes;
    private readonly string[] _names;
    private readonly long[] _categoryBytes;
    private Dictionary<StorageCategory, IReadOnlyList<CategoryArea>>? _areas;

    internal StorageTree(string rootPath, NodeData[] nodes, string[] names, long fileCount, long totalBytes, long[] categoryBytes)
    {
        RootPath = rootPath;
        _nodes = nodes;
        _names = names;
        FileCount = fileCount;
        TotalBytes = totalBytes;
        _categoryBytes = categoryBytes;
    }

    public string RootPath { get; }

    public long FileCount { get; }

    public long TotalBytes { get; }

    public StorageNode Root => new(this, StorageTreeBuilder.RootIndex);

    public long GetCategoryBytes(StorageCategory category) => _categoryBytes[(int)category];

    /// <summary>
    /// The top-most folders (or root files) where a category starts, each with only the
    /// bytes below it that belong to that category. Largest first.
    /// </summary>
    public IReadOnlyList<CategoryArea> GetCategoryAreas(StorageCategory category)
    {
        _areas ??= ComputeAreas();
        return _areas.TryGetValue(category, out var list) ? list : [];
    }

    /// <summary>
    /// Every folder the scan could not list and that has no size yet: the ones a privileged measurement can still fill in.
    /// Locked folders never have children.
    /// </summary>
    public IReadOnlyList<StorageNode> GetLockedFolders()
    {
        var locked = new List<StorageNode>();
        for (int i = 0; i < _nodes.Length; i++)
        {
            if ((_nodes[i].Flags & (NodeFlags.Directory | NodeFlags.Locked | NodeFlags.Measured)) == (NodeFlags.Directory | NodeFlags.Locked))
            {
                locked.Add(new StorageNode(this, i));
            }
        }

        return locked;
    }

    /// <summary>
    /// A new tree in which the locked folders found in <paramref name="sizesByPath"/> (key: <see cref="StorageNode.FullPath"/>,
    /// compared by the dictionary's own comparer) carry the measured size, rolled up to every ancestor and added to the totals
    /// and to the folder's category. Only folders that are locked and not yet measured are updated, so measuring twice never
    /// counts a folder twice; other keys are ignored. This tree is not changed. Negative sizes count as zero.
    /// </summary>
    public StorageTree WithMeasuredFolders(IReadOnlyDictionary<string, FolderMeasurement> sizesByPath)
    {
        ArgumentNullException.ThrowIfNull(sizesByPath);

        NodeData[] nodes = (NodeData[])_nodes.Clone();
        long[] categoryBytes = (long[])_categoryBytes.Clone();
        long fileCount = FileCount;
        long totalBytes = TotalBytes;

        if (sizesByPath.Count > 0)
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                if ((nodes[i].Flags & (NodeFlags.Directory | NodeFlags.Locked | NodeFlags.Measured)) != (NodeFlags.Directory | NodeFlags.Locked)
                    || !sizesByPath.TryGetValue(new StorageNode(this, i).FullPath, out FolderMeasurement measurement))
                {
                    continue;
                }

                long sizeOnDisk = Math.Max(0, measurement.SizeOnDisk);
                long logicalSize = Math.Max(0, measurement.LogicalSize);
                long files = Math.Max(0, measurement.Files);

                nodes[i].Flags |= NodeFlags.Measured;
                for (int index = i; index >= 0; index = index == StorageTreeBuilder.RootIndex ? -1 : nodes[index].Parent)
                {
                    nodes[index].SizeOnDisk += sizeOnDisk;
                    nodes[index].LogicalSize += logicalSize;
                }

                totalBytes += sizeOnDisk;
                fileCount += files;
                categoryBytes[(int)nodes[i].Category] += sizeOnDisk;
            }
        }

        return new StorageTree(RootPath, nodes, _names, fileCount, totalBytes, categoryBytes);
    }

    /// <summary>
    /// The bytes below <paramref name="node"/> that belong to <paramref name="category"/> in the same area as the node:
    /// the walk stops at a folder of another category, the way <see cref="GetCategoryAreas"/> ends an area there.
    /// Zero when the node itself is of another category.
    /// </summary>
    public long GetCategoryBytes(StorageNode node, StorageCategory category)
    {
        if (node.Category != category)
        {
            return 0;
        }

        long bytes = 0;
        var pending = new Stack<int>();
        pending.Push(node.Index);
        while (pending.TryPop(out int index))
        {
            ref readonly NodeData data = ref _nodes[index];
            if (IsLeaf(in data))
            {
                bytes += data.SizeOnDisk;
                continue;
            }

            for (int child = data.FirstChild; child >= 0; child = _nodes[child].NextSibling)
            {
                if (_nodes[child].Category == category)
                {
                    pending.Push(child);
                }
            }
        }

        return bytes;
    }

    internal ref readonly NodeData Node(int index) => ref _nodes[index];

    /// <summary>Files, and locked folders whose size was measured afterwards, are where bytes are counted.</summary>
    private static bool IsLeaf(in NodeData node) => (node.Flags & (NodeFlags.Directory | NodeFlags.Measured)) != NodeFlags.Directory;

    internal string Name(int index) => _names[index];

    private Dictionary<StorageCategory, IReadOnlyList<CategoryArea>> ComputeAreas()
    {
        // A node starts a new area when its category differs from its parent's.
        var areaOf = new int[_nodes.Length];
        var bytesByArea = new Dictionary<int, long>();
        for (int i = 0; i < _nodes.Length; i++)
        {
            ref readonly NodeData node = ref _nodes[i];
            areaOf[i] = i == 0 || node.Category != _nodes[node.Parent].Category ? i : areaOf[node.Parent];
            if (IsLeaf(in node))
            {
                bytesByArea[areaOf[i]] = bytesByArea.GetValueOrDefault(areaOf[i]) + node.SizeOnDisk;
            }
        }

        return bytesByArea
            .Where(pair => pair.Value > 0)
            .GroupBy(pair => _nodes[pair.Key].Category)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CategoryArea>)group
                    .Select(pair => new CategoryArea(new StorageNode(this, pair.Key), pair.Value))
                    .OrderByDescending(area => area.Bytes)
                    .ToArray());
    }
}

public readonly record struct CategoryArea(StorageNode Node, long Bytes);

/// <summary>The size of a folder the scan could not list, measured afterwards by someone who could.</summary>
/// <param name="SizeOnDisk">Bytes the folder's files occupy on disk.</param>
/// <param name="LogicalSize">Sum of the files' lengths.</param>
/// <param name="Files">Number of files found.</param>
public readonly record struct FolderMeasurement(long SizeOnDisk, long LogicalSize, long Files);
