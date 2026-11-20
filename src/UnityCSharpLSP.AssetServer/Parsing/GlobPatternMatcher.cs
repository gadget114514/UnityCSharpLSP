using System.Text;
using System.Text.RegularExpressions;

namespace UnityCSharpLSP.AssetServer.Parsing;

/// <summary>
/// Matches file paths against shell-style glob patterns. Patterns without a slash are matched
/// against the file name, so <c>*.cs</c> behaves like ripgrep's <c>-g '*.cs'</c>.
/// A leading <c>!</c> negates a pattern.
/// </summary>
public static class GlobPatternMatcher
{
    public static bool MatchesAny(string? path, IReadOnlyList<string>? patterns)
    {
        if (string.IsNullOrEmpty(path) || patterns is null || patterns.Count == 0)
        {
            return true;
        }

        bool included = false;
        foreach (string pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            bool negated = pattern[0] == '!';
            string glob = negated ? pattern[1..] : pattern;
            if (!Matches(path, glob))
            {
                continue;
            }

            included = !negated;
        }

        return included;
    }

    public static bool Matches(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        string normalizedPath = path.Replace('\\', '/');

        if (pattern[0] == '~')
        {
            string needle = pattern[1..].Replace('\\', '/').TrimStart('/');
            return needle.Length > 0 && normalizedPath.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        string normalizedPattern = pattern.Replace('\\', '/').TrimStart('/');
        string candidate = normalizedPattern.Contains('/')
            ? normalizedPath
            : normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];

        var regex = new StringBuilder("^");
        for (int i = 0; i < normalizedPattern.Length; i++)
        {
            char c = normalizedPattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < normalizedPattern.Length && normalizedPattern[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < normalizedPattern.Length && normalizedPattern[i + 1] == '/')
                        {
                            i++;
                            regex.Append("(?:.*/)?");
                        }
                        else
                        {
                            regex.Append(".*");
                        }
                    }
                    else
                    {
                        regex.Append("[^/]*");
                    }
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        regex.Append('$');

        return Regex.IsMatch(candidate, regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
