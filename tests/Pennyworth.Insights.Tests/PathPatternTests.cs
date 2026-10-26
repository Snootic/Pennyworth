using Pennyworth.Insights.Classification;

namespace Pennyworth.Insights.Tests;

public class PathPatternTests
{
    [Theory]
    [InlineData(@"C:\Windows", @"C:\Windows", true)]
    [InlineData(@"C:\Windows", @"c:\WINDOWS", true)]
    [InlineData(@"C:\Windows", @"C:\Windows\System32", false)]
    [InlineData(@"C:\Users\*", @"C:\Users\Bob", true)]
    [InlineData(@"C:\Users\*", @"C:\Users", false)]
    [InlineData(@"?:\pagefile.sys", @"D:\pagefile.sys", true)]
    [InlineData(@"?:\pagefile.sys", @"D:\Games\pagefile.sys", false)]
    [InlineData(@"**\steamapps", @"D:\SteamLibrary\steamapps", true)]
    [InlineData(@"**\steamapps", @"C:\Program Files (x86)\Steam\steamapps", true)]
    [InlineData(@"**\steamapps", @"C:\steamapps\common", false)]
    public void Matches_paths(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathPattern.Parse(pattern).IsMatch(PathPattern.SplitPath(path)));
    }

    [Theory]
    [InlineData("/var/cache", "/var/cache", true)]
    [InlineData("/var/cache", "/VAR/Cache", true)]
    [InlineData("/var/cache", "var/cache", false)]
    [InlineData("/var/cache", "/home/ana/var/cache", false)]
    [InlineData("/var/cache", "/var/cache/zypp", false)]
    [InlineData("/home/*/.cache", "/home/ana/.cache", true)]
    [InlineData("/home/*/.cache", "home/ana/.cache", false)]
    [InlineData("/**/steamapps", "/home/ana/.local/share/Steam/steamapps", true)]
    [InlineData("/**/steamapps", "steamapps", false)]
    [InlineData("**/steamapps", "/home/ana/steamapps", true)]
    [InlineData("**/steamapps", "/steamapps/common", false)]
    public void Rooted_patterns_only_match_rooted_paths(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathPattern.Parse(pattern).IsMatch(PathPattern.SplitPath(path)));
    }

    [Theory]
    [InlineData(@"?:\pagefile.sys", "/pagefile.sys")]
    [InlineData(@"?:\pagefile.sys", "/c:/pagefile.sys")]
    [InlineData(@"C:\Windows", "/C:/Windows")]
    [InlineData(@"C:\Windows", "/Windows")]
    public void Drive_patterns_never_match_a_rooted_path(string pattern, string path)
    {
        Assert.False(PathPattern.Parse(pattern).IsMatch(PathPattern.SplitPath(path)));
    }

    [Fact]
    public void A_rooted_path_keeps_its_root_when_split()
    {
        Assert.Equal(["", "var", "cache"], PathPattern.SplitPath("/var/cache/"));
        Assert.Equal(["var", "cache"], PathPattern.SplitPath("var/cache"));
        Assert.Equal(["C:", "Windows"], PathPattern.SplitPath(@"C:\Windows"));
    }

    [Fact]
    public void Exact_patterns_are_more_specific_than_wildcard_prefixes()
    {
        Assert.True(PathPattern.Parse(@"C:\Users\*\AppData").Specificity > PathPattern.Parse(@"C:\Users\*").Specificity);
        Assert.True(PathPattern.Parse(@"C:\steamapps").Specificity > PathPattern.Parse(@"**\steamapps").Specificity);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"**")]
    [InlineData(@"C:\**\x")]
    [InlineData("/")]
    [InlineData("/**")]
    public void Rejects_invalid_patterns(string pattern)
    {
        Assert.Throws<FormatException>(() => PathPattern.Parse(pattern));
    }
}
