using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.Core.Storage;

namespace Pennyworth.App.ViewModels;

internal static class CategoryText
{
    /// <summary>Resource key suffix for the bar colour, e.g. <c>Brush.Category.Games</c>.</summary>
    public const string UnaccountedKey = "Unaccounted";

    public static string Name(StorageCategory category) => category switch
    {
        StorageCategory.Games => Strings.Category_Games,
        StorageCategory.Apps => Strings.Category_Apps,
        StorageCategory.YourFiles => Strings.Category_YourFiles,
        StorageCategory.Windows => Localizer.Instance.Resolve(Strings.Category_Windows),
        StorageCategory.HiddenSystem => Localizer.Instance.Resolve(Strings.Category_HiddenSystem),
        _ => Strings.Category_Other,
    };

    /// <summary>One plain-language line under the category name.</summary>
    public static string Hint(StorageCategory category) => category switch
    {
        StorageCategory.Games => Strings.CategoryHint_Games,
        StorageCategory.Apps => Strings.CategoryHint_Apps,
        StorageCategory.YourFiles => Strings.CategoryHint_YourFiles,
        StorageCategory.Windows => Localizer.Instance.Resolve(Strings.CategoryHint_Windows),
        StorageCategory.HiddenSystem => Localizer.Instance.Resolve(Strings.CategoryHint_HiddenSystem),
        _ => Strings.CategoryHint_Other,
    };

    public static string ColorKey(StorageCategory category) => category.ToString();
}
