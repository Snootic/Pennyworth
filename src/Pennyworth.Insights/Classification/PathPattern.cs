namespace Pennyworth.Insights.Classification;

/// <summary>
/// A path pattern split into folder names. Supports <c>?:</c> (any drive) as the first
/// segment, <c>*</c> (any one folder name) and a leading <c>**</c> (any prefix).
/// Matching is case-insensitive. Environment variables must already be expanded.
/// </summary>
/// <remarks>
/// A pattern that starts with a separator (<c>/var/cache</c>) is rooted: it only matches rooted paths,
/// never the same names written relative to some folder, and a drive-letter pattern never matches a
/// rooted path. A drive letter is not a separator, so <c>C:\Windows</c> carries its root in its first segment.
/// </remarks>
public sealed class PathPattern
{
    private readonly string[] _segments;
    private readonly bool _anyPrefix;
    private readonly bool _rooted;

    private PathPattern(string[] segments, bool anyPrefix, bool rooted)
    {
        _segments = segments;
        _anyPrefix = anyPrefix;
        _rooted = rooted;
    }

    /// <summary>Higher wins when several patterns match the same path.</summary>
    public int Specificity => (_segments.Length * 2) + (_anyPrefix ? 0 : 1);

    public static PathPattern Parse(string pattern)
    {
        string[] segments = SplitPath(pattern);
        bool rooted = segments.Length > 0 && segments[0].Length == 0;
        if (rooted)
        {
            segments = segments[1..];
        }

        bool anyPrefix = segments.Length > 0 && segments[0] == "**";
        if (anyPrefix)
        {
            segments = segments[1..];
        }

        if (segments.Length == 0 || segments.Contains("**"))
        {
            throw new FormatException($"Invalid path pattern '{pattern}'.");
        }

        return new PathPattern(segments, anyPrefix, rooted);
    }

    /// <summary>
    /// Splits a path into folder names. A path that starts with a separator gets an empty first
    /// segment standing for its root, which keeps <c>/var/cache</c> apart from <c>var/cache</c>.
    /// </summary>
    public static string[] SplitPath(string path)
    {
        string[] names = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return path.Length > 0 && path[0] is '\\' or '/' ? ["", .. names] : names;
    }

    public bool IsMatch(string[] pathSegments)
    {
        bool pathRooted = pathSegments.Length > 0 && pathSegments[0].Length == 0;
        if (_rooted != pathRooted && (_rooted || !_anyPrefix))
        {
            // A rooted pattern needs a rooted path. A rooted path only matches a pattern that
            // does not start at any root of its own, i.e. one that begins with "**".
            return false;
        }

        int first = pathRooted ? 1 : 0;
        int count = pathSegments.Length - first;
        if (_anyPrefix ? count < _segments.Length : count != _segments.Length)
        {
            return false;
        }

        int offset = first + count - _segments.Length;
        for (int i = 0; i < _segments.Length; i++)
        {
            if (!SegmentMatches(_segments[i], pathSegments[offset + i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SegmentMatches(string pattern, string segment) => pattern switch
    {
        "*" => true,
        "?:" => segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':',
        _ => string.Equals(pattern, segment, StringComparison.OrdinalIgnoreCase),
    };
}
