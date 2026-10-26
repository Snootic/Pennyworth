namespace Pennyworth.Platform.Linux;

/// <summary>
/// Resolves the <c>%NAME%</c> variables the Linux knowledge base writes its paths with: <c>HOME</c>,
/// <c>TMPDIR</c> and the four <c>XDG_*_HOME</c> folders. Names are exact, as on Linux. Everything else
/// is unresolved, which drops the rule that uses it.
/// </summary>
/// <remarks>
/// Windows names (<c>TEMP</c>, <c>LOCALAPPDATA</c>, <c>SystemRoot</c>...) deliberately do not resolve.
/// Pointing <c>%TEMP%</c> at <c>/tmp</c> would turn a Windows rule, with its Windows promise
/// ("Windows recreates these"), into deleting files from a Linux machine.
/// </remarks>
public sealed class EnvironmentScope(XdgPaths paths, Func<string, string?>? environment = null)
{
    private const string FallbackTemp = "/tmp";

    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <summary>The folder for <paramref name="name"/>, or null when it has no meaning on Linux.</summary>
    public string? Resolve(string name) => name switch
    {
        "HOME" => paths.Home,
        "TMPDIR" => Temp(),
        "XDG_CACHE_HOME" => paths.CacheHome,
        "XDG_CONFIG_HOME" => paths.ConfigHome,
        "XDG_DATA_HOME" => paths.DataHome,
        "XDG_STATE_HOME" => paths.StateHome,
        _ => null,
    };

    private string Temp() =>
        _environment("TMPDIR") is { Length: > 0 } dir && Path.IsPathRooted(dir)
            ? Path.TrimEndingDirectorySeparator(dir)
            : FallbackTemp;
}
