using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Core.Storage;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

public class FolderBrowserViewModelTests
{
    [Fact]
    public void Starts_at_this_pc_with_every_drive()
    {
        var host = new Host();
        var browser = new FolderBrowserViewModel(host);

        Assert.True(browser.IsThisPc);
        Assert.Equal(["Games (D:)", "Local Disk (C:)"], browser.Items.Select(i => i.Name));
        Assert.All(browser.Items, item => Assert.True(item.IsDrive));
        Assert.Equal(0.2, browser.Items[0].Fraction, 3); // D: is 100 GB used of 500 GB
    }

    [Fact]
    public void Opening_a_scanned_drive_shows_its_folders_and_an_unscanned_one_scans_it()
    {
        var host = new Host();
        var browser = new FolderBrowserViewModel(host);

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Games (D:)"));
        Assert.Equal(["D:"], host.Scanned);
        Assert.True(browser.IsThisPc);

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Local Disk (C:)"));
        Assert.Equal(@"C:\", browser.Current?.FullPath);
        Assert.Equal("Local Disk (C:)", browser.Title);
    }

    [Fact]
    public void Lists_children_largest_first_scaled_to_the_largest()
    {
        var browser = new FolderBrowserViewModel(new Host());
        browser.Show(Build().Root, resetHistory: true);

        Assert.Equal(["Games", "Windows", "Users", "stray.bin"], browser.Items.Select(i => i.Name));
        Assert.Equal(1.0, browser.Items[0].Fraction, 3);
        Assert.Equal(0.4, browser.Items[1].Fraction, 3);
    }

    [Fact]
    public void Opens_folders_ignores_files_and_builds_breadcrumbs()
    {
        var browser = new FolderBrowserViewModel(new Host());
        browser.Show(Build().Root, resetHistory: true);

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "stray.bin"));
        Assert.Equal(@"C:\", browser.Current?.FullPath);

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Users"));
        Assert.Equal(@"C:\Users", browser.Current?.FullPath);
        Assert.Equal(["This PC", "Local Disk (C:)", "Users"], browser.Breadcrumbs.Select(b => b.Name));
        Assert.True(browser.Breadcrumbs[^1].IsLast);
    }

    [Fact]
    public void Back_follows_history_and_up_goes_to_the_parent_then_this_pc()
    {
        var root = Build().Root;
        var browser = new FolderBrowserViewModel(new Host());
        browser.Show(root, resetHistory: true);
        Assert.False(browser.BackCommand.CanExecute(null));

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Users"));
        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Bob"));
        browser.BackCommand.Execute(null);
        Assert.Equal(@"C:\Users", browser.Current?.FullPath);

        browser.UpCommand.Execute(null);
        browser.UpCommand.Execute(null);
        Assert.True(browser.IsThisPc);
        Assert.False(browser.UpCommand.CanExecute(null));

        browser.NavigateToCommand.Execute(browser.Breadcrumbs[^1]); // the last crumb does nothing
        Assert.True(browser.IsThisPc);
    }

    [Fact]
    public void Titles_the_page_with_the_current_folder()
    {
        var browser = new FolderBrowserViewModel(new Host());
        Assert.Equal("This PC", browser.Title);
        browser.Show(Build().Root, resetHistory: true);

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Games"));

        Assert.Equal("Games", browser.Title);
    }

    [Fact]
    public void Tells_locked_folders_apart_from_empty_ones()
    {
        var b = new StorageTreeBuilder(@"D:\", StorageCategory.Other);
        int locked = b.AddDirectory(StorageTreeBuilder.RootIndex, "Locked", StorageCategory.Other);
        b.MarkLocked(locked);
        b.AddDirectory(StorageTreeBuilder.RootIndex, "Empty", StorageCategory.Other);
        var host = new Host();
        var browser = new FolderBrowserViewModel(host);
        browser.Show(b.Build().Root, resetHistory: true);

        var lockedItem = browser.Items.Single(i => i.Name == "Locked");
        Assert.Equal("—", lockedItem.SizeText);
        browser.OpenCommand.Execute(lockedItem);
        Assert.True(browser.IsLockedFolder);
        Assert.False(browser.IsEmpty);
        Assert.False(browser.HasItems);
        browser.UnlockCommand.Execute(null);
        Assert.Equal(1, host.UnlockRequests);

        browser.UpCommand.Execute(null);
        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Empty"));
        Assert.False(browser.IsLockedFolder);
        Assert.True(browser.IsEmpty);
    }

    [Fact]
    public void Shows_items_in_explorer()
    {
        var host = new Host();
        var browser = new FolderBrowserViewModel(host);
        browser.Show(Build().Root, resetHistory: true);

        browser.ShowInExplorerCommand.Execute(browser.Items[0]);

        Assert.Equal([@"C:\Games"], host.Shown);
    }

    private sealed class Host : IFolderHost
    {
        private readonly StorageTree _tree = Build();

        public IReadOnlyList<DriveViewModel> Drives { get; } = [new(TestApp.DriveD), new(TestApp.DriveC)];

        public List<string> Scanned { get; } = [];

        public List<string> Shown { get; } = [];

        public int UnlockRequests { get; private set; }

        public StorageTree? TreeFor(DriveViewModel drive) => drive.Token == "C:" ? _tree : null;

        public void ScanDrive(DriveViewModel drive) => Scanned.Add(drive.Token);

        public void ShowInExplorer(string path) => Shown.Add(path);

        public void RequestUnlock() => UnlockRequests++;
    }
}
