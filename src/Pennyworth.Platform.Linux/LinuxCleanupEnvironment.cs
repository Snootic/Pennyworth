using System.Runtime.Versioning;
using Pennyworth.Cleanup;

namespace Pennyworth.Platform.Linux;

/// <summary>Builds the cleanup environment for Linux: XDG variables, the safety guard, the XDG trash and case-sensitive paths.</summary>
[SupportedOSPlatform("linux")]
internal static class LinuxCleanupEnvironment
{
    public static CleanupEnvironment Create(XdgPaths paths, EnvironmentScope scope, Func<IReadOnlyList<string>> mountPoints, int uid) =>
        new(
            scope.Resolve,
            new LinuxSafetyGuard(paths, mountPoints, scope.Resolve).MayCleanInside,
            StringComparer.Ordinal,
            new XdgTrash(paths, mountPoints, uid));
}
