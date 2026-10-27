using System.Collections;
using System.Globalization;
using System.Resources;
using Pennyworth.App.Resources;

namespace Pennyworth.App.Tests;

public class StringsTests
{
    [Fact]
    public void Every_string_has_a_portuguese_translation()
    {
        var manager = Strings.ResourceManager;
        var english = Keys(manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!);
        var portuguese = Keys(manager.GetResourceSet(CultureInfo.GetCultureInfo("pt-BR"), createIfNotExists: true, tryParents: false)!);

        Assert.Empty(english.Except(portuguese));
        Assert.Empty(portuguese.Except(english));
    }

    [Fact]
    public void Both_languages_use_the_same_placeholders()
    {
        var english = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        var portuguese = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("pt-BR"), createIfNotExists: true, tryParents: false)!;

        foreach (DictionaryEntry entry in english)
        {
            string key = (string)entry.Key;
            Assert.True(
                Placeholders((string)entry.Value!).SetEquals(Placeholders(portuguese.GetString(key)!)),
                $"'{key}' has different placeholders in Portuguese.");
        }
    }

    [Fact]
    public void Formatted_strings_accept_their_arguments()
    {
        // Every string takes at most a text and a number: "{0}" and "{1:N0}".
        foreach (string culture in new[] { "en-US", "pt-BR" })
        {
            var info = CultureInfo.GetCultureInfo(culture);
            foreach (DictionaryEntry entry in Strings.ResourceManager.GetResourceSet(info, createIfNotExists: true, tryParents: true)!)
            {
                string text = Pennyworth.Core.Wording.PlatformTokens.Resolve((string)entry.Value!, Pennyworth.Core.Wording.WindowsText.Instance, culture == "pt-BR");
                _ = string.Format(info, text, "1 GB", 1234L);
            }
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+(:[^}]*)?\}").Select(match => match.Value).ToHashSet();

    private static HashSet<string> Keys(ResourceSet set) =>
        set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet();
}
