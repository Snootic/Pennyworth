using System.Globalization;
using Pennyworth.Insights.Classification;
using Pennyworth.Platform;

namespace Pennyworth.App.Services;

/// <summary>Plain-language titles and descriptions for known folders.</summary>
public interface ILocationInfo
{
    (string Title, string Description)? Describe(string fullPath);

    /// <summary>
    /// True for a folder that holds one place per app or tool, which the storage page lists folder by folder
    /// rather than as one lump (<c>.var/app</c>, <c>/opt</c>).
    /// </summary>
    bool SplitsIntoPlaces(string fullPath) => false;
}

public sealed class KnowledgeBaseLocationInfo(RuleClassifier classifier, IAppNames? appNames = null) : ILocationInfo
{
    private readonly IAppNames _appNames = appNames ?? NoAppNames.Instance;

    public (string Title, string Description)? Describe(string fullPath)
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        if (classifier.FindLocation(fullPath) is { } location)
        {
            return (location.Title.Get(culture), location.Description.Get(culture));
        }

        return DescribeChild(fullPath, culture);
    }

    public bool SplitsIntoPlaces(string fullPath) => classifier.FindLocation(fullPath)?.Children is not null;

    /// <summary>A folder with no rule of its own inside a folder that holds one place per app: named after its app, else after itself.</summary>
    private (string Title, string Description)? DescribeChild(string fullPath, CultureInfo culture)
    {
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null || classifier.FindLocation(parent)?.Children is not { } children)
        {
            return null;
        }

        string name = _appNames.NameOf(fullPath, culture) ?? Path.GetFileName(fullPath);
        string title = children.Title is { } template ? string.Format(culture, template.Get(culture), name) : name;
        string description = children.Description is { } text ? string.Format(culture, text.Get(culture), name) : string.Empty;
        return (title, description);
    }
}
