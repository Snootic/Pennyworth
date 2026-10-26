using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Pennyworth.Cleanup;
using Pennyworth.KnowledgeBase;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>The Linux cleanup rules against the real environment, safety guard and file system of the machine running the tests.</summary>
public partial class LinuxKnowledgeBaseTests
{
    private static readonly IReadOnlyList<CleanupRule> Rules = KnowledgeBaseLoader.LoadCleanupRules(KnowledgeBaseLoader.Linux).Rules;

    private static LinuxSafetyGuard Guard(XdgPaths paths, EnvironmentScope scope) => new(paths, () => MountTable.MountPoints(), scope.Resolve);

    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex VariablePattern();

    [Fact]
    public void Every_variable_in_the_linux_data_resolves_with_the_environment_scope()
    {
        var scope = new EnvironmentScope(new XdgPaths(name => name == "HOME" ? "/home/ana" : null));
        var paths = KnowledgeBaseLoader.LoadEmbedded(KnowledgeBaseLoader.Linux).Locations.Select(l => l.Path)
            .Concat(Rules.SelectMany(r => r.Paths))
            .ToList();

        var names = paths.SelectMany(p => VariablePattern().Matches(p).Select(m => m.Groups[1].Value)).Distinct().ToList();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.False(string.IsNullOrEmpty(scope.Resolve(name)), name));
    }

    [Fact]
    public void The_guard_accepts_every_cleanup_path_for_a_typical_home()
    {
        var paths = new XdgPaths(name => name == "HOME" ? "/home/ana" : null);
        var scope = new EnvironmentScope(paths);
        var guard = Guard(paths, scope);

        foreach (CleanupRule rule in Rules.Where(r => r.Kind == CleanupKind.FolderContents))
        {
            foreach (string pattern in rule.Paths)
            {
                string path = pattern.Replace("*", "profile");
                foreach (Match variable in VariablePattern().Matches(path))
                {
                    path = path.Replace(variable.Value, scope.Resolve(variable.Groups[1].Value));
                }

                Assert.True(guard.MayCleanInside(path), $"{rule.Id}: the guard refuses {path}");
            }
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Every_cleanup_rule_resolves_here_and_the_guard_refuses_none_of_its_folders()
    {
        var paths = new XdgPaths();
        var scope = new EnvironmentScope(paths);
        var real = new CleanupService(Rules, environment: LinuxCleanupEnvironment.Create(paths, scope, () => MountTable.MountPoints(), XdgTrash.CurrentUid()));
        var unguarded = new CleanupService(Rules, environment: new CleanupEnvironment(scope.Resolve, _ => true, StringComparer.Ordinal));

        foreach (CleanupRule rule in Rules.Where(r => r.Kind == CleanupKind.FolderContents))
        {
            CleanupEstimate guarded = await real.MeasureAsync(rule, CancellationToken.None);
            CleanupEstimate all = await unguarded.MeasureAsync(rule, CancellationToken.None);

            Assert.Equal(all.Folders!.Select(f => f.Path), guarded.Folders!.Select(f => f.Path));
            Assert.Equal(!rule.RequiresAdmin, guarded.IsAvailable);
            Assert.All(guarded.Folders!, f => Assert.True(f.Bytes >= 0 && f.Files >= 0, f.Path));
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task No_cleanup_rule_points_at_user_data()
    {
        var paths = new XdgPaths();
        var scope = new EnvironmentScope(paths);
        var service = new CleanupService(Rules, environment: LinuxCleanupEnvironment.Create(paths, scope, () => MountTable.MountPoints(), XdgTrash.CurrentUid()));
        string[] userFolders = ["Documents", "Downloads", "Pictures", "Videos", "Music", "Desktop", "Public", "Templates"];

        foreach (CleanupRule rule in Rules.Where(r => r.Kind == CleanupKind.FolderContents))
        {
            CleanupEstimate estimate = await service.MeasureAsync(rule, CancellationToken.None);
            foreach (CleanupFolder folder in estimate.Folders!)
            {
                string relative = folder.Path.StartsWith(paths.Home + "/", StringComparison.Ordinal) ? folder.Path[(paths.Home.Length + 1)..] : folder.Path;
                Assert.DoesNotContain(relative.Split('/')[0], userFolders);
                Assert.NotEqual(paths.Home, folder.Path);
                Assert.NotEqual(paths.CacheHome, folder.Path);
                Assert.NotEqual(paths.DataHome, folder.Path);
                Assert.NotEqual(paths.ConfigHome, folder.Path);
                Assert.False(folder.Path.Contains("/compatdata") || folder.Path.Contains("/steamapps/common"), folder.Path);
            }
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task The_trash_rule_measures_through_the_real_xdg_trash()
    {
        var paths = new XdgPaths();
        var scope = new EnvironmentScope(paths);
        var service = new CleanupService(Rules, environment: LinuxCleanupEnvironment.Create(paths, scope, () => MountTable.MountPoints(), XdgTrash.CurrentUid()));
        CleanupRule trash = Rules.Single(r => r.Kind == CleanupKind.RecycleBin);

        CleanupEstimate estimate = await service.MeasureAsync(trash, CancellationToken.None);

        Assert.True(estimate.IsAvailable);
        Assert.True(estimate.Bytes >= 0);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void The_platform_names_its_data_set()
    {
        Assert.Equal(KnowledgeBaseLoader.Linux, new LinuxPlatform().Name);
    }

    [Fact]
    public void Windows_variable_names_do_not_resolve_so_the_windows_rules_resolve_to_nothing_here()
    {
        var scope = new EnvironmentScope(new XdgPaths(name => name == "HOME" ? "/home/ana" : null));

        Assert.All(
            KnowledgeBaseLoader.LoadCleanupRules(KnowledgeBaseLoader.Windows).Rules.SelectMany(r => r.Paths)
                .SelectMany(p => VariablePattern().Matches(p).Select(m => m.Groups[1].Value)),
            name => Assert.Null(scope.Resolve(name)));
    }
}
