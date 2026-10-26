using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Pennyworth.Core.Storage;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Scans a folder tree on Linux without privileges, by listing directories in parallel. It mirrors
/// the Windows <c>DirectoryScanner</c>: the same worker queue, tree builder, classifier inheritance,
/// progress snapshots and cancellation.
/// <para>
/// Sizes come from one <c>statx</c> per entry: allocated bytes are <c>st_blocks * 512</c> (a sparse file
/// costs what it stores) and logical bytes are <c>st_size</c>. Rules:
/// </para>
/// <list type="bullet">
/// <item>Symbolic links are never followed; a link counts as a file of its own (tiny) size.</item>
/// <item>The scan never leaves the file system of the scan root (<c>du -x</c>): a directory whose
/// <c>st_dev</c> differs from the root's is left out. That keeps a scan of <c>/</c> finite
/// (<c>/proc</c>, <c>/sys</c>, <c>/dev</c>, <c>/run</c>, tmpfs, network mounts) and also excludes separately
/// mounted btrfs subvolumes, which have their own device number. Scan those folders on their own.</item>
/// <item>A file with several hard links is counted once, at the first path found.</item>
/// <item>A directory that cannot be listed is marked locked; one that vanished mid-scan is skipped.</item>
/// <item>Directory entries are read with <c>opendir</c>/<c>readdir</c> as raw bytes and stat'ed relative to
/// the directory's descriptor. File names that are not valid UTF-8 therefore still resolve and are
/// counted (the displayed name uses U+FFFD for the bad bytes), and only the path of a directory, not
/// of every file, is ever built, which avoids a UTF-16 round trip per entry.</item>
/// </list>
/// Directory sizes themselves (the 4 KiB of metadata <c>du</c> adds per folder) are not counted, as on Windows.
/// </summary>
public sealed unsafe partial class PosixDirectoryScanner : IStorageScanner
{
    private readonly int _workerCount;
    private readonly TimeSpan _progressInterval;

    public PosixDirectoryScanner(int? workerCount = null, TimeSpan? progressInterval = null)
    {
        _workerCount = workerCount ?? Math.Clamp(Environment.ProcessorCount, 2, 16);
        _progressInterval = progressInterval ?? TimeSpan.FromMilliseconds(150);
    }

    public Task<StorageTree> ScanAsync(
        string rootPath,
        IPathClassifier classifier,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => new ScanRun(rootPath, classifier, _workerCount).Run(progress, _progressInterval, cancellationToken), cancellationToken);
    }

    [LibraryImport("libc", EntryPoint = "opendir", SetLastError = true)]
    private static partial nint OpenDir(byte* path);

    [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static partial byte* ReadDir(nint directory);

    [LibraryImport("libc", EntryPoint = "closedir")]
    private static partial int CloseDir(nint directory);

    [LibraryImport("libc", EntryPoint = "dirfd")]
    private static partial int DirFd(nint directory);

    private sealed class ScanRun
    {
        // struct dirent64: d_ino u64, d_off i64, d_reclen u16, d_type u8, d_name[]. Same on glibc and musl, x64 and arm64.
        private const int DirentTypeOffset = 18;
        private const int DirentNameOffset = 19;
        private const byte DirentDirectory = 4;
        private const int Enoent = 2;
        private const int CancellationCheckMask = 1023;

        private readonly IPathClassifier _classifier;
        private readonly int _workerCount;
        private readonly StorageTreeBuilder _builder;
        private readonly object _builderLock = new();
        private readonly ConcurrentStack<WorkItem> _pending = new();
        private readonly ConcurrentDictionary<(ulong Device, ulong Inode), byte> _hardLinks = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly bool _hasRootDevice;
        private readonly ulong _rootDevice;
        private int _outstanding;

        public ScanRun(string rootPath, IPathClassifier classifier, int workerCount)
        {
            _classifier = classifier;
            _workerCount = workerCount;
            StorageCategory rootCategory = classifier.Classify(rootPath, StorageCategory.Other);
            _builder = new StorageTreeBuilder(rootPath, rootCategory);

            byte[] rootBytes = ToPathBytes(rootPath);
            if (Statx.StatPath(rootPath, followLinks: true, out FileStat root) == StatResult.Ok && root.HasIdentity)
            {
                _hasRootDevice = true;
                _rootDevice = root.Device;
            }

            Enqueue(new WorkItem(StorageTreeBuilder.RootIndex, rootPath, rootBytes, rootCategory, IsRoot: true));
        }

        public StorageTree Run(IProgress<ScanProgress>? progress, TimeSpan progressInterval, CancellationToken cancellationToken)
        {
            using var timer = progress is null ? null : new Timer(_ => Report(progress), null, progressInterval, progressInterval);
            Task[] workers = Enumerable.Range(0, _workerCount)
                .Select(_ => Task.Factory.StartNew(() => Work(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                .ToArray();

            try
            {
                Task.WaitAll(workers, cancellationToken);
            }
            catch (AggregateException) when (cancellationToken.IsCancellationRequested)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            timer?.Dispose();
            if (progress is not null)
            {
                Report(progress);
            }

            lock (_builderLock)
            {
                return _builder.Build();
            }
        }

        private void Work(CancellationToken cancellationToken)
        {
            while (true)
            {
                _available.Wait(cancellationToken);
                if (!_pending.TryPop(out WorkItem item))
                {
                    return; // Woken up because the scan is finished.
                }

                try
                {
                    Process(item, cancellationToken);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _outstanding) == 0)
                    {
                        _available.Release(_workerCount);
                    }
                }
            }
        }

        private void Process(WorkItem item, CancellationToken cancellationToken)
        {
            var directories = new List<PendingDirectory>();
            var files = new List<PendingFile>();
            var lockedDirectories = new List<string>();

            nint directory;
            fixed (byte* path = item.PathBytes)
            {
                directory = OpenDir(path);
            }

            if (directory == 0)
            {
                // A directory deleted since it was listed is not locked, it is gone.
                if (Marshal.GetLastPInvokeError() != Enoent)
                {
                    lock (_builderLock)
                    {
                        _builder.MarkLocked(item.Index);
                    }
                }

                return;
            }

            bool listingFailed;
            try
            {
                listingFailed = ReadEntries(item, directory, directories, files, lockedDirectories, cancellationToken);
            }
            finally
            {
                CloseDir(directory);
            }

            var children = new WorkItem[directories.Count];
            lock (_builderLock)
            {
                if (listingFailed)
                {
                    _builder.MarkLocked(item.Index);
                }

                foreach (PendingFile file in files)
                {
                    _builder.AddFile(item.Index, file.Name, file.Allocated, file.Logical, file.Category);
                }

                foreach (string name in lockedDirectories)
                {
                    _builder.MarkLocked(_builder.AddDirectory(item.Index, name, item.Category));
                }

                for (int i = 0; i < directories.Count; i++)
                {
                    PendingDirectory child = directories[i];
                    int index = _builder.AddDirectory(item.Index, child.Name, child.Category);
                    children[i] = new WorkItem(index, child.Path, child.PathBytes, child.Category, IsRoot: false);
                }
            }

            foreach (WorkItem child in children)
            {
                Enqueue(child);
            }
        }

        /// <summary>Lists one open directory into the pending lists; true when readdir failed part way.</summary>
        private bool ReadEntries(
            WorkItem item,
            nint directory,
            List<PendingDirectory> directories,
            List<PendingFile> files,
            List<string> lockedDirectories,
            CancellationToken cancellationToken)
        {
            int dirFd = DirFd(directory);
            int seen = 0;
            while (true)
            {
                byte* entry = ReadDir(directory);
                if (entry == null)
                {
                    return Marshal.GetLastPInvokeError() != 0;
                }

                if ((++seen & CancellationCheckMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                byte* name = entry + DirentNameOffset;
                ReadOnlySpan<byte> nameBytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
                if (IsDotEntry(nameBytes))
                {
                    continue;
                }

                byte type = entry[DirentTypeOffset];
                StatResult result = Statx.Stat(dirFd, name, followLinks: false, out FileStat stat);
                if (result == StatResult.Unavailable)
                {
                    string fallbackPath = Path.Join(item.Path, Encoding.UTF8.GetString(nameBytes));
                    if (Statx.FallbackStat(fallbackPath) is { } fallback)
                    {
                        stat = fallback;
                        result = StatResult.Ok;
                    }
                    else
                    {
                        result = StatResult.Failed;
                    }
                }

                switch (result)
                {
                    case StatResult.Ok:
                        break;
                    case StatResult.Denied when type == DirentDirectory:
                        lockedDirectories.Add(Encoding.UTF8.GetString(nameBytes));
                        continue;
                    default:
                        continue; // Vanished, unreadable or failed: the entry is skipped.
                }

                if (stat.Kind == FileKind.Directory)
                {
                    if (_hasRootDevice && stat.HasIdentity && stat.Device != _rootDevice)
                    {
                        continue; // Another file system: not part of this scan.
                    }

                    string directoryName = Encoding.UTF8.GetString(nameBytes);
                    string path = Path.Join(item.Path, directoryName);
                    directories.Add(new PendingDirectory(
                        directoryName,
                        path,
                        Combine(item.PathBytes, nameBytes),
                        _classifier.Classify(path, item.Category)));
                    continue;
                }

                if (stat is { HasIdentity: true, LinkCount: > 1 } && !_hardLinks.TryAdd((stat.Device, stat.Inode), 0))
                {
                    continue; // Another name of a file already counted.
                }

                string fileName = Encoding.UTF8.GetString(nameBytes);
                StorageCategory category = item.IsRoot
                    ? _classifier.Classify(Path.Join(item.Path, fileName), item.Category)
                    : item.Category;
                files.Add(new PendingFile(fileName, stat.AllocatedBytes, stat.LogicalBytes, category));
            }
        }

        private static bool IsDotEntry(ReadOnlySpan<byte> name) =>
            name is [(byte)'.'] or [(byte)'.', (byte)'.'];

        private static byte[] ToPathBytes(string path)
        {
            string trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
            if (trimmed.Length == 0)
            {
                trimmed = "/";
            }

            int length = Encoding.UTF8.GetByteCount(trimmed);
            byte[] bytes = new byte[length + 1];
            Encoding.UTF8.GetBytes(trimmed, bytes);
            return bytes;
        }

        /// <summary>The NUL-terminated byte path of a child; <paramref name="parent"/> is NUL-terminated too.</summary>
        private static byte[] Combine(byte[] parent, ReadOnlySpan<byte> name)
        {
            int parentLength = parent.Length - 1;
            bool needsSeparator = parentLength == 0 || parent[parentLength - 1] != (byte)'/';
            byte[] result = new byte[parentLength + (needsSeparator ? 1 : 0) + name.Length + 1];
            parent.AsSpan(0, parentLength).CopyTo(result);
            if (needsSeparator)
            {
                result[parentLength] = (byte)'/';
            }

            name.CopyTo(result.AsSpan(parentLength + (needsSeparator ? 1 : 0)));
            return result;
        }

        private void Enqueue(WorkItem item)
        {
            Interlocked.Increment(ref _outstanding);
            _pending.Push(item);
            _available.Release();
        }

        private void Report(IProgress<ScanProgress> progress)
        {
            ScanProgress snapshot;
            lock (_builderLock)
            {
                snapshot = new ScanProgress(_builder.FileCount, _builder.TotalBytes, _builder.SnapshotCategoryBytes());
            }

            progress.Report(snapshot);
        }
    }

    private readonly record struct WorkItem(int Index, string Path, byte[] PathBytes, StorageCategory Category, bool IsRoot);

    private readonly record struct PendingDirectory(string Name, string Path, byte[] PathBytes, StorageCategory Category);

    private readonly record struct PendingFile(string Name, long Allocated, long Logical, StorageCategory Category);
}
