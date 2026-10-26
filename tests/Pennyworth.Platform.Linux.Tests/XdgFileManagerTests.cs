using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class XdgFileManagerTests
{
    [Theory]
    [InlineData("/home/u/a.txt", "file:///home/u/a.txt")]
    [InlineData("/home/u/my file.txt", "file:///home/u/my%20file.txt")]
    [InlineData("/home/u/a#b", "file:///home/u/a%23b")]
    [InlineData("/home/u/100%", "file:///home/u/100%25")]
    [InlineData("/home/u/a?b=c&d", "file:///home/u/a%3Fb%3Dc%26d")]
    [InlineData("/home/u/it's", "file:///home/u/it%27s")]
    [InlineData("/home/u/\"q\"", "file:///home/u/%22q%22")]
    [InlineData("/home/u/a,b", "file:///home/u/a%2Cb")]
    [InlineData("/home/u/café", "file:///home/u/caf%C3%A9")]
    [InlineData("/home/u/日本", "file:///home/u/%E6%97%A5%E6%9C%AC")]
    [InlineData("/home/u/😀", "file:///home/u/%F0%9F%98%80")]
    [InlineData("/home/u/a-b_c.d~e", "file:///home/u/a-b_c.d~e")]
    [InlineData("/", "file:///")]
    public void Uri_is_percent_encoded(string path, string expected)
    {
        Assert.Equal(expected, XdgFileManager.BuildFileUri(path));
    }

    [Fact]
    public void Existing_file_is_revealed_and_its_folder_opened()
    {
        var (reveal, open) = XdgFileManager.ChooseTargets(
            "/a/b/c.txt", p => p == "/a/b/c.txt", p => p is "/a" or "/a/b" or "/");
        Assert.Equal("/a/b/c.txt", reveal);
        Assert.Equal("/a/b", open);
    }

    [Fact]
    public void Existing_folder_is_opened_itself()
    {
        var (reveal, open) = XdgFileManager.ChooseTargets("/a/b", _ => false, p => p is "/a" or "/a/b" or "/");
        Assert.Equal("/a/b", reveal);
        Assert.Equal("/a/b", open);
    }

    [Fact]
    public void Mount_root_is_opened_itself()
    {
        var (reveal, open) = XdgFileManager.ChooseTargets("/", _ => false, p => p == "/");
        Assert.Equal("/", reveal);
        Assert.Equal("/", open);
    }

    [Fact]
    public void Missing_path_falls_back_to_the_nearest_existing_parent()
    {
        var (reveal, open) = XdgFileManager.ChooseTargets("/a/b/c/d.txt", _ => false, p => p is "/a" or "/");
        Assert.Equal("/a", reveal);
        Assert.Equal("/a", open);
    }

    [Fact]
    public void A_file_in_the_way_of_a_missing_path_is_not_mistaken_for_a_folder()
    {
        var (reveal, open) = XdgFileManager.ChooseTargets("/a/f.txt/x", p => p == "/a/f.txt", p => p is "/a" or "/");
        Assert.Equal("/a/f.txt", reveal);
        Assert.Equal("/a", open);
    }

    [Fact]
    public void Gdbus_arguments_carry_the_uri_as_one_argument()
    {
        string[] args = XdgFileManager.GdbusArguments("file:///a%20b");
        Assert.Contains("['file:///a%20b']", args);
        Assert.Contains("org.freedesktop.FileManager1.ShowItems", args);
    }

    [Fact]
    public void Dbus_send_arguments_carry_the_uri_as_one_argument()
    {
        string[] args = XdgFileManager.DbusSendArguments("file:///a%20b");
        Assert.Contains("array:string:file:///a%20b", args);
    }

    [Fact]
    public void Falls_back_to_dbus_send_then_xdg_open()
    {
        string dir = Directory.CreateTempSubdirectory("pennyworth-xdg").FullName;
        try
        {
            string file = Path.Combine(dir, "my file.txt");
            File.WriteAllText(file, "x");
            var ran = new List<string>();
            var opened = new List<string>();
            var launcher = new XdgFileManager((exe, _) => { ran.Add(exe); return null; }, opened.Add);

            launcher.ShowInExplorer(file);

            Assert.Equal(["gdbus", "dbus-send"], ran);
            Assert.Equal([dir], opened);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_file_manager_that_answers_means_xdg_open_is_not_used()
    {
        var opened = new List<string>();
        var launcher = new XdgFileManager((_, _) => "()", opened.Add);

        launcher.ShowInExplorer("/");

        Assert.Empty(opened);
    }

    [Fact]
    public void A_missing_xdg_open_does_not_throw()
    {
        var launcher = new XdgFileManager((_, _) => null, _ => throw new System.ComponentModel.Win32Exception(2));
        launcher.ShowInExplorer("/");
    }

    [Fact]
    public void An_empty_path_does_nothing()
    {
        var launcher = new XdgFileManager((_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        launcher.ShowInExplorer("");
    }
}
