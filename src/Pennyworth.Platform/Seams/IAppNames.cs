using System.Globalization;

namespace Pennyworth.Platform;

/// <summary>
/// Names the app a folder belongs to, from what the system itself records about its apps (on Linux the
/// <c>.desktop</c> entry of a Flatpak app), so that the storage page can say "Google Chrome" for
/// <c>com.google.Chrome</c>. The knowledge base names the fixed places; this names the ones that depend on what is installed.
/// </summary>
public interface IAppNames
{
    /// <summary>The app's readable name in <paramref name="culture"/>, or null when this folder is not an app's or its name is unknown.</summary>
    string? NameOf(string fullPath, CultureInfo culture);
}

/// <summary>For a system that records no app names to look up: every folder keeps the name it has.</summary>
public sealed class NoAppNames : IAppNames
{
    public static readonly NoAppNames Instance = new();

    private NoAppNames()
    {
    }

    public string? NameOf(string fullPath, CultureInfo culture) => null;
}
