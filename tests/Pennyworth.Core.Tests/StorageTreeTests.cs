using Pennyworth.Core.Storage;

namespace Pennyworth.Core.Tests;

public class StorageTreeTests
{
    // C:\
    //   Windows\            (Windows)
    //     a.dll 100
    //   Users\              (Other)
    //     Bob\              (YourFiles)
    //       photo.jpg 300
    //       AppData\        (Apps)
    //         cache.bin 50
    //   hiberfil.sys 1000   (HiddenSystem, root file)
    private static StorageTree BuildSample()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        int windows = b.AddDirectory(StorageTreeBuilder.RootIndex, "Windows", StorageCategory.Windows);
        b.AddFile(windows, "a.dll", 100, 90, StorageCategory.Windows);
        int users = b.AddDirectory(StorageTreeBuilder.RootIndex, "Users", StorageCategory.Other);
        int bob = b.AddDirectory(users, "Bob", StorageCategory.YourFiles);
        b.AddFile(bob, "photo.jpg", 300, 290, StorageCategory.YourFiles);
        int appData = b.AddDirectory(bob, "AppData", StorageCategory.Apps);
        b.AddFile(appData, "cache.bin", 50, 40, StorageCategory.Apps);
        b.AddFile(StorageTreeBuilder.RootIndex, "hiberfil.sys", 1000, 1000, StorageCategory.HiddenSystem);
        return b.Build();
    }

    [Fact]
    public void Rolls_sizes_up_to_every_directory()
    {
        var tree = BuildSample();

        Assert.Equal(1450, tree.Root.SizeOnDisk);
        Assert.Equal(1420, tree.Root.LogicalSize);
        Assert.Equal(1450, tree.TotalBytes);
        Assert.Equal(4, tree.FileCount);
        var users = tree.Root.GetChildren().Single(n => n.Name == "Users");
        Assert.Equal(350, users.SizeOnDisk);
    }

    [Fact]
    public void Children_are_sorted_largest_first()
    {
        var names = BuildSample().Root.GetChildren().Select(n => n.Name);

        Assert.Equal(["hiberfil.sys", "Users", "Windows"], names);
    }

    [Fact]
    public void Builds_full_paths_from_the_root()
    {
        var bob = BuildSample().Root.GetChildren().Single(n => n.Name == "Users").GetChildren().Single();

        Assert.Equal(@"C:\Users\Bob", bob.FullPath);
        Assert.Equal(@"C:\", bob.Parent!.Value.Parent!.Value.FullPath);
    }

    [Fact]
    public void Tracks_bytes_per_category()
    {
        var tree = BuildSample();

        Assert.Equal(100, tree.GetCategoryBytes(StorageCategory.Windows));
        Assert.Equal(300, tree.GetCategoryBytes(StorageCategory.YourFiles));
        Assert.Equal(50, tree.GetCategoryBytes(StorageCategory.Apps));
        Assert.Equal(1000, tree.GetCategoryBytes(StorageCategory.HiddenSystem));
        Assert.Equal(0, tree.GetCategoryBytes(StorageCategory.Games));
    }

    [Fact]
    public void Category_areas_exclude_nested_areas_of_other_categories()
    {
        var tree = BuildSample();

        var yourFiles = Assert.Single(tree.GetCategoryAreas(StorageCategory.YourFiles));
        Assert.Equal(@"C:\Users\Bob", yourFiles.Node.FullPath);
        Assert.Equal(300, yourFiles.Bytes);

        var apps = Assert.Single(tree.GetCategoryAreas(StorageCategory.Apps));
        Assert.Equal(@"C:\Users\Bob\AppData", apps.Node.FullPath);

        var hidden = Assert.Single(tree.GetCategoryAreas(StorageCategory.HiddenSystem));
        Assert.Equal(@"C:\hiberfil.sys", hidden.Node.FullPath);
        Assert.False(hidden.Node.IsDirectory);

        Assert.Empty(tree.GetCategoryAreas(StorageCategory.Games));
    }

    [Fact]
    public void Marks_locked_directories()
    {
        var b = new StorageTreeBuilder(@"D:\", StorageCategory.Other);
        int secret = b.AddDirectory(StorageTreeBuilder.RootIndex, "Secret", StorageCategory.Other);
        b.MarkLocked(secret);

        var node = Assert.Single(b.Build().Root.GetChildren());

        Assert.True(node.IsLocked);
        Assert.False(node.HasChildren);
    }

    [Fact]
    public void Rejects_children_added_to_a_file()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        b.AddFile(StorageTreeBuilder.RootIndex, "a.txt", 1, 1, StorageCategory.Other);

        Assert.Throws<ArgumentOutOfRangeException>(() => b.AddFile(1, "b.txt", 1, 1, StorageCategory.Other));
    }

    [Fact]
    public void Snapshot_reflects_files_added_so_far()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        b.AddFile(StorageTreeBuilder.RootIndex, "a.bin", 10, 10, StorageCategory.Games);

        var snapshot = b.SnapshotCategoryBytes();

        Assert.Equal(10, snapshot[(int)StorageCategory.Games]);
        Assert.Equal(StorageCategories.Count, snapshot.Length);
    }

    [Fact]
    public void Counts_the_bytes_of_one_category_below_a_folder_and_stops_at_another_category()
    {
        // /data (Apps) holds a loose 10, a/ (Apps, 30), g/ (Games, 7) and g/h/ (Apps again, 5).
        var b = new StorageTreeBuilder("/", StorageCategory.Other);
        int data = b.AddDirectory(StorageTreeBuilder.RootIndex, "data", StorageCategory.Apps);
        b.AddFile(data, "loose", 10, 10, StorageCategory.Apps);
        int a = b.AddDirectory(data, "a", StorageCategory.Apps);
        b.AddFile(a, "a.bin", 30, 30, StorageCategory.Apps);
        int g = b.AddDirectory(data, "g", StorageCategory.Games);
        b.AddFile(g, "g.pak", 7, 7, StorageCategory.Games);
        int h = b.AddDirectory(g, "h", StorageCategory.Apps);
        b.AddFile(h, "h.bin", 5, 5, StorageCategory.Apps);
        StorageTree tree = b.Build();
        StorageNode dataNode = tree.Root.GetChildren().Single();

        Assert.Equal(40, tree.GetCategoryBytes(dataNode, StorageCategory.Apps));
        Assert.Equal(30, tree.GetCategoryBytes(dataNode.GetChildren().Single(n => n.Name == "a"), StorageCategory.Apps));
        Assert.Equal(0, tree.GetCategoryBytes(dataNode.GetChildren().Single(n => n.Name == "g"), StorageCategory.Apps));
        Assert.Equal(7, tree.GetCategoryBytes(dataNode.GetChildren().Single(n => n.Name == "g"), StorageCategory.Games));
        // Exactly what the area list reports for the folder.
        Assert.Equal(40, tree.GetCategoryAreas(StorageCategory.Apps).Single(area => area.Node.Name == "data").Bytes);
    }

    // / (Other)
    //   open/ (Apps)           f.bin 10
    //   secret/ (Apps)         locked
    //   deep/ (Other)
    //     vault/ (Games)       locked, the second one
    private static StorageTree BuildWithLockedFolders()
    {
        var b = new StorageTreeBuilder("/", StorageCategory.Other);
        int open = b.AddDirectory(StorageTreeBuilder.RootIndex, "open", StorageCategory.Apps);
        b.AddFile(open, "f.bin", 10, 8, StorageCategory.Apps);
        int secret = b.AddDirectory(StorageTreeBuilder.RootIndex, "secret", StorageCategory.Apps);
        b.MarkLocked(secret);
        int deep = b.AddDirectory(StorageTreeBuilder.RootIndex, "deep", StorageCategory.Other);
        int vault = b.AddDirectory(deep, "vault", StorageCategory.Games);
        b.MarkLocked(vault);
        return b.Build();
    }

    private static StorageNode Find(StorageTree tree, string name) =>
        Descendants(tree.Root).Single(node => node.Name == name);

    [Fact]
    public void Lists_the_locked_folders_that_have_no_size_yet()
    {
        StorageTree tree = BuildWithLockedFolders();

        Assert.Equal(["secret", "vault"], tree.GetLockedFolders().Select(node => node.Name).Order());
        Assert.All(tree.GetLockedFolders(), node => Assert.False(node.IsMeasured));
        Assert.Empty(BuildSample().GetLockedFolders());

        StorageTree measured = tree.WithMeasuredFolders(new Dictionary<string, FolderMeasurement>
        {
            [Find(tree, "secret").FullPath] = new(100, 90, 4),
        });

        Assert.Equal(["vault"], measured.GetLockedFolders().Select(node => node.Name));
    }

    [Fact]
    public void Measured_folders_add_up_in_ancestors_totals_and_categories()
    {
        StorageTree tree = BuildWithLockedFolders();

        StorageTree measured = tree.WithMeasuredFolders(new Dictionary<string, FolderMeasurement>
        {
            [Find(tree, "secret").FullPath] = new(100, 90, 4),
            [Find(tree, "vault").FullPath] = new(1000, 900, 7),
        });

        Assert.Equal(1110, measured.TotalBytes);
        Assert.Equal(1 + 4 + 7, measured.FileCount);
        Assert.Equal(1110, measured.Root.SizeOnDisk);
        Assert.Equal(998, measured.Root.LogicalSize);
        Assert.Equal(1000, measured.Root.GetChildren().Single(node => node.Name == "deep").SizeOnDisk);
        Assert.Equal(110, measured.GetCategoryBytes(StorageCategory.Apps));
        Assert.Equal(1000, measured.GetCategoryBytes(StorageCategory.Games));
        Assert.Equal(0, measured.GetCategoryBytes(StorageCategory.Other));

        StorageNode secret = Find(measured, "secret");
        Assert.True(secret.IsMeasured);
        Assert.True(secret.IsLocked);
        Assert.False(secret.HasChildren);
        Assert.Equal(100, secret.SizeOnDisk);
        Assert.Equal(90, secret.LogicalSize);
    }

    [Fact]
    public void Measured_folders_count_as_leaves_of_their_category()
    {
        StorageTree tree = BuildWithLockedFolders();
        StorageTree measured = tree.WithMeasuredFolders(new Dictionary<string, FolderMeasurement>
        {
            [Find(tree, "secret").FullPath] = new(100, 90, 4),
            [Find(tree, "vault").FullPath] = new(1000, 900, 7),
        });

        // The area list sums to the category's bytes, with the locked folder inside its area.
        var apps = measured.GetCategoryAreas(StorageCategory.Apps);
        Assert.Equal(110, apps.Sum(area => area.Bytes));
        Assert.Equal(100, apps.Single(area => area.Node.Name == "secret").Bytes);
        Assert.Equal(1000, measured.GetCategoryAreas(StorageCategory.Games).Single().Bytes);
        Assert.Equal(100, measured.GetCategoryBytes(Find(measured, "secret"), StorageCategory.Apps));
        Assert.Equal(1000, measured.GetCategoryBytes(Find(measured, "vault"), StorageCategory.Games));
        Assert.Equal(0, measured.GetCategoryBytes(Find(measured, "vault"), StorageCategory.Apps));

        // Before measuring, a locked folder holds nothing: the old behaviour.
        Assert.Equal(10, tree.GetCategoryAreas(StorageCategory.Apps).Sum(area => area.Bytes));
        Assert.Empty(tree.GetCategoryAreas(StorageCategory.Games));
    }

    [Fact]
    public void Measured_folders_keep_the_children_listing_sorted_by_size()
    {
        StorageTree tree = BuildWithLockedFolders();
        StorageTree measured = tree.WithMeasuredFolders(new Dictionary<string, FolderMeasurement>
        {
            [Find(tree, "secret").FullPath] = new(100, 90, 4),
        });

        Assert.Equal(["secret", "open", "deep"], measured.Root.GetChildren().Select(node => node.Name));
    }

    [Fact]
    public void A_folder_measured_twice_is_counted_once()
    {
        StorageTree tree = BuildWithLockedFolders();
        var sizes = new Dictionary<string, FolderMeasurement> { [Find(tree, "secret").FullPath] = new(100, 90, 4) };

        StorageTree twice = tree.WithMeasuredFolders(sizes).WithMeasuredFolders(sizes);

        Assert.Equal(110, twice.TotalBytes);
        Assert.Equal(5, twice.FileCount);
        Assert.Equal(110, twice.Root.SizeOnDisk);
        Assert.Equal(110, twice.GetCategoryBytes(StorageCategory.Apps));
    }

    [Fact]
    public void Unknown_keys_and_folders_that_are_not_locked_are_ignored()
    {
        StorageTree tree = BuildWithLockedFolders();
        StorageNode open = tree.Root.GetChildren().Single(node => node.Name == "open");

        StorageTree measured = tree.WithMeasuredFolders(new Dictionary<string, FolderMeasurement>
        {
            ["/nowhere"] = new(500, 500, 5),
            [open.FullPath] = new(500, 500, 5),
            [tree.Root.FullPath] = new(500, 500, 5),
        });

        Assert.Equal(tree.TotalBytes, measured.TotalBytes);
        Assert.Equal(tree.FileCount, measured.FileCount);
        Assert.Equal(tree.Root.SizeOnDisk, measured.Root.SizeOnDisk);
        Assert.Equal(2, measured.GetLockedFolders().Count);
        Assert.False(measured.Root.GetChildren().Single(node => node.Name == "open").IsMeasured);
    }

    [Fact]
    public void Measuring_leaves_the_original_tree_untouched()
    {
        StorageTree tree = BuildWithLockedFolders();

        StorageTree measured = tree.WithMeasuredFolders(new Dictionary<string, FolderMeasurement>
        {
            [Find(tree, "secret").FullPath] = new(100, 90, 4),
        });

        Assert.NotSame(tree, measured);
        Assert.Equal(10, tree.TotalBytes);
        Assert.Equal(1, tree.FileCount);
        Assert.Equal(10, tree.Root.SizeOnDisk);
        Assert.Equal(10, tree.GetCategoryBytes(StorageCategory.Apps));
        Assert.False(Find(tree, "secret").IsMeasured);
        Assert.Equal(0, Find(tree, "secret").SizeOnDisk);
    }

    private static IEnumerable<StorageNode> Descendants(StorageNode node)
    {
        foreach (StorageNode child in node.GetChildren())
        {
            yield return child;
            foreach (StorageNode below in Descendants(child))
            {
                yield return below;
            }
        }
    }
}
