using System.Globalization;
using System.Text;

namespace Pennyworth.Platform.Linux;

/// <summary>A value of a GVariant text dump: a tuple or array of values, a dictionary, or a single string or number.</summary>
internal abstract record GVariantNode;

/// <summary>A tuple <c>(a, b)</c> or an array <c>[a, b]</c>; the two read the same here, by position.</summary>
internal sealed record GVariantList(IReadOnlyList<GVariantNode> Items) : GVariantNode;

/// <summary>A dictionary <c>{'key': value}</c>.</summary>
internal sealed record GVariantDictionary(IReadOnlyList<(GVariantNode Key, GVariantNode Value)> Entries) : GVariantNode
{
    public GVariantNode? this[string key] =>
        Entries.FirstOrDefault(entry => entry.Key is GVariantAtom { Text: var text } && text == key).Value;
}

/// <summary>A string (quotes removed) or a number, boolean or other bare word, as written.</summary>
internal sealed record GVariantAtom(string Text) : GVariantNode
{
    public bool IsTrue => Text == "true";

    public double? AsDouble => double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    public int? AsInt => int.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}

/// <summary>
/// A reader for the text format <c>gdbus call</c> and <c>gdbus introspect</c> print (the GVariant text format):
/// <c>(uint32 6, [(('eDP-1', 'AUO'), [], {'is-current': &lt;true&gt;})], @a{sv} {})</c>. It keeps what a caller reads by
/// position and key and drops what only types the text: <c>uint32</c>-style prefixes, <c>@a{sv}</c> annotations and the
/// <c>&lt;&gt;</c> of a variant (its content stands in its place). Text it cannot read gives null, never an exception.
/// </summary>
internal static class GVariantText
{
    /// <summary>Words that precede a bare number only to name its type.</summary>
    private static readonly HashSet<string> TypeWords = new(StringComparer.Ordinal)
    {
        "byte", "int16", "uint16", "int32", "uint32", "int64", "uint64", "double", "handle", "objectpath", "signature",
    };

    public static GVariantNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var reader = new Reader(text);
        try
        {
            GVariantNode node = reader.ReadValue();
            reader.SkipSpace();
            return reader.AtEnd ? node : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class Reader(string text)
    {
        private int _position;

        public bool AtEnd => _position >= text.Length;

        public void SkipSpace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        public GVariantNode ReadValue()
        {
            SkipSpace();
            SkipAnnotations();
            if (AtEnd)
            {
                throw new FormatException("A value was expected.");
            }

            return text[_position] switch
            {
                '(' => ReadList('(', ')'),
                '[' => ReadList('[', ']'),
                '{' => ReadDictionary(),
                '<' => ReadVariant(),
                '\'' or '"' => new GVariantAtom(ReadString()),
                _ => new GVariantAtom(ReadWord()),
            };
        }

        /// <summary>Drops <c>@a{sv}</c> and <c>uint32 </c> style type prefixes in front of the value.</summary>
        private void SkipAnnotations()
        {
            while (true)
            {
                if (_position < text.Length && text[_position] == '@')
                {
                    while (_position < text.Length && !char.IsWhiteSpace(text[_position]))
                    {
                        _position++;
                    }

                    SkipSpace();
                    continue;
                }

                int end = _position;
                while (end < text.Length && char.IsLetterOrDigit(text[end]))
                {
                    end++;
                }

                if (end > _position && end < text.Length && text[end] == ' ' && TypeWords.Contains(text[_position..end]))
                {
                    _position = end;
                    SkipSpace();
                    continue;
                }

                return;
            }
        }

        private GVariantList ReadList(char open, char close)
        {
            _position++; // The opening bracket.
            var items = new List<GVariantNode>();
            while (true)
            {
                SkipSpace();
                if (AtEnd)
                {
                    throw new FormatException("Unclosed list.");
                }

                if (text[_position] == close)
                {
                    _position++;
                    return new GVariantList(items);
                }

                items.Add(ReadValue());
                SkipSpace();
                if (!AtEnd && text[_position] == ',')
                {
                    _position++;
                }
            }
        }

        private GVariantDictionary ReadDictionary()
        {
            _position++;
            var entries = new List<(GVariantNode, GVariantNode)>();
            while (true)
            {
                SkipSpace();
                if (AtEnd)
                {
                    throw new FormatException("Unclosed dictionary.");
                }

                if (text[_position] == '}')
                {
                    _position++;
                    return new GVariantDictionary(entries);
                }

                GVariantNode key = ReadValue();
                SkipSpace();
                if (AtEnd || text[_position] != ':')
                {
                    throw new FormatException("A dictionary entry needs a colon.");
                }

                _position++;
                entries.Add((key, ReadValue()));
                SkipSpace();
                if (!AtEnd && text[_position] == ',')
                {
                    _position++;
                }
            }
        }

        private GVariantNode ReadVariant()
        {
            _position++;
            GVariantNode inner = ReadValue();
            SkipSpace();
            if (AtEnd || text[_position] != '>')
            {
                throw new FormatException("Unclosed variant.");
            }

            _position++;
            return inner;
        }

        private string ReadString()
        {
            char quote = text[_position++];
            var value = new StringBuilder();
            while (_position < text.Length)
            {
                char c = text[_position++];
                if (c == quote)
                {
                    return value.ToString();
                }

                if (c == '\\' && _position < text.Length)
                {
                    c = text[_position++];
                }

                value.Append(c);
            }

            throw new FormatException("Unclosed string.");
        }

        private string ReadWord()
        {
            int start = _position;
            while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not (',' or ')' or ']' or '}' or '>' or ':'))
            {
                _position++;
            }

            if (_position == start)
            {
                throw new FormatException($"Unexpected '{text[_position]}'.");
            }

            return text[start.._position];
        }
    }
}
