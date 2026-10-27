using System.Globalization;

namespace Pennyworth.Platform.Linux;

/// <summary>Pure reader of the one key of a <c>.desktop</c> file the storage page needs: the app's name.</summary>
internal static class DesktopEntry
{
    /// <summary>
    /// The <c>Name</c> of the <c>[Desktop Entry]</c> group in <paramref name="culture"/>: <c>Name[pt_BR]</c>, then <c>Name[pt]</c>,
    /// then the plain <c>Name</c>. Other groups (<c>[Desktop Action ...]</c> has names too), comments and blank values are skipped.
    /// Null when the file has no usable name.
    /// </summary>
    public static string? Name(string text, CultureInfo culture)
    {
        string[] preferred = LocaleKeys(culture);
        string?[] found = new string?[preferred.Length + 1];
        bool inEntry = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEntry = line == "[Desktop Entry]";
                continue;
            }

            if (!inEntry || !line.StartsWith("Name", StringComparison.Ordinal))
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string key = line[..equals].TrimEnd();
            string value = line[(equals + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (key == "Name")
            {
                found[^1] = value;
            }
            else if (key.Length > 6 && key[4] == '[' && key[^1] == ']')
            {
                int rank = Array.IndexOf(preferred, key[5..^1]);
                if (rank >= 0)
                {
                    found[rank] = value;
                }
            }
        }

        return found.FirstOrDefault(name => name is not null);
    }

    /// <summary>Locale suffixes to try, most specific first: <c>pt-BR</c> gives <c>pt_BR</c> then <c>pt</c>.</summary>
    private static string[] LocaleKeys(CultureInfo culture)
    {
        string name = culture.Name.Replace('-', '_');
        if (name.Length == 0)
        {
            return [];
        }

        string language = culture.TwoLetterISOLanguageName;
        return name == language ? [name] : [name, language];
    }
}
