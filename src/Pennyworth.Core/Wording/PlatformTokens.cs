using System.Text.RegularExpressions;

namespace Pennyworth.Core.Wording;

/// <summary>Replaces the <c>{@Name}</c> tokens of a resource string with the operating system's words.</summary>
public static partial class PlatformTokens
{
    /// <summary>
    /// The text with every token replaced. A string without tokens comes back as it is, and one with
    /// <c>{0}</c> placeholders keeps them for <see cref="string.Format(string, object?)"/>.
    /// </summary>
    /// <exception cref="FormatException">A token names no <see cref="PlatformText"/>.</exception>
    public static string Resolve(string template, IPlatformText text, bool portuguese) =>
        template.Contains("{@", StringComparison.Ordinal)
            ? Token().Replace(template, match => Enum.TryParse(match.Groups[1].Value, out PlatformText value)
                ? text.Text(value, portuguese)
                : throw new FormatException($"'{match.Value}' is not a platform text."))
            : template;

    /// <summary>The names of the tokens in a resource string.</summary>
    public static IEnumerable<string> Names(string template) =>
        Token().Matches(template).Select(match => match.Groups[1].Value);

    [GeneratedRegex(@"\{@([A-Za-z]\w*)\}")]
    private static partial Regex Token();
}
