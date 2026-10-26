using Pennyworth.Assistant.Providers;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public sealed class LinuxAssistantServicesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pennyworth-assistant-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [LinuxFact]
    public void Keeps_conversations_usage_and_models_under_the_data_home()
    {
        var paths = new XdgPaths(name => name switch
        {
            "HOME" => _root,
            "XDG_DATA_HOME" => Path.Combine(_root, "data"),
            _ => null,
        });

        AssistantServices services = LinuxAssistantServices.Create(paths, new NullKeys(), () => 123);

        services.Conversations.Start("hello", "cloud-1", DateTimeOffset.UnixEpoch);
        services.Usage.Add("cloud-1", 1, 1, DateTimeOffset.UnixEpoch);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "data", "Pennyworth", "conversations"), "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(_root, "data", "Pennyworth", "usage.json")));
        Assert.StartsWith(Path.Combine(_root, "data", "Pennyworth", "models") + Path.DirectorySeparatorChar, services.Models.FolderOf(services.Catalog.Models[0]));
        Assert.Equal(123, services.AvailableMemory());
        Assert.Equal("linux", services.PlatformName);
    }

    [LinuxFact]
    public void Free_space_of_a_folder_is_that_of_its_file_system()
    {
        var paths = new XdgPaths(name => name == "HOME" ? _root : null);
        AssistantServices services = LinuxAssistantServices.Create(paths, new NullKeys(), () => 0);
        string sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;

        Assert.True(services.FreeSpace(sub) > 0);
        Assert.InRange(Math.Abs(services.FreeSpace(sub) - new DriveInfo(_root).AvailableFreeSpace), 0, 64L << 20);
    }

    [Fact]
    public void A_folder_that_does_not_exist_yet_is_measured_at_its_nearest_existing_parent()
    {
        var measured = new List<string>();
        string existing = Path.GetFullPath(Path.Combine("/data", "models"));

        long free = LinuxAssistantServices.FreeSpace(
            Path.Combine(existing, "phi", "abc1234"),
            path => path == existing || path == Path.GetDirectoryName(existing),
            path =>
            {
                measured.Add(path);
                return 42;
            });

        Assert.Equal(42, free);
        Assert.Equal([existing], measured);
    }

    [Fact]
    public void Measures_the_deepest_existing_folder_not_the_root()
    {
        var measured = new List<string>();

        LinuxAssistantServices.FreeSpace("/mnt/disk/a/b", path => path is "/mnt/disk/a" or "/mnt/disk" or "/mnt" or "/", path =>
        {
            measured.Add(path);
            return 1;
        });

        Assert.Equal(["/mnt/disk/a"], measured);
    }

    [Fact]
    public void Free_space_is_zero_when_nothing_can_be_measured()
    {
        Assert.Equal(0, LinuxAssistantServices.FreeSpace("/gone/away", _ => false, _ => 7));
        Assert.Equal(0, LinuxAssistantServices.FreeSpace("/x", _ => true, _ => throw new UnauthorizedAccessException()));
        Assert.Equal(0, LinuxAssistantServices.FreeSpace("a\0b", _ => true, _ => 7));
    }

    private sealed class NullKeys : ICredentialStore
    {
        public void Save(string profileId, string secret)
        {
        }

        public string? Read(string profileId) => null;

        public void Delete(string profileId)
        {
        }
    }
}
