using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityCSharpLSP.Workspace.Models;

namespace UnityCSharpLSP.Workspace.Defines;

/// <summary>
/// Computes Roslyn preprocessor conditional compilation symbols for Unity assemblies.
/// </summary>
public static class DefineSymbolManager
{
    private static readonly Regex VersionRegex = new(
        @"^(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?",
        RegexOptions.Compiled);

    // Well-known historic Unity minor releases (from 2017.1 to 2023.3, and 6000.0)
    private static readonly (int Major, int Minor)[] KnownReleases =
    [
        (2017, 1), (2017, 2), (2017, 3), (2017, 4),
        (2018, 1), (2018, 2), (2018, 3), (2018, 4),
        (2019, 1), (2019, 2), (2019, 3), (2019, 4),
        (2020, 1), (2020, 2), (2020, 3),
        (2021, 1), (2021, 2), (2021, 3),
        (2022, 1), (2022, 2), (2022, 3),
        (2023, 1), (2023, 2), (2023, 3),
        (6000, 0), (6000, 1), (6000, 2),
    ];

    /// <summary>
    /// Computes compilation symbols for a specific assembly within a Unity project.
    /// </summary>
    public static HashSet<string> GetSymbols(
        string? unityVersion,
        bool isEditor,
        IReadOnlyDictionary<string, string>? packages = null,
        AsmdefFile? asmdef = null,
        IEnumerable<string>? extraSymbols = null)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        // 1. Unity version symbols
        if (!string.IsNullOrWhiteSpace(unityVersion))
        {
            AddVersionSymbols(unityVersion, symbols);
        }

        // 2. Platform symbols
        AddPlatformSymbols(symbols, isEditor);

        // 3. Editor symbols
        if (isEditor || (asmdef is not null && IsEditorAssembly(asmdef)))
        {
            symbols.Add("UNITY_EDITOR");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                symbols.Add("UNITY_EDITOR_WIN");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                symbols.Add("UNITY_EDITOR_OSX");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                symbols.Add("UNITY_EDITOR_LINUX");
            }
        }

        // 4. Version defines from asmdef
        if (asmdef?.VersionDefines is { Length: > 0 } versionDefines && packages is not null)
        {
            foreach (var vd in versionDefines)
            {
                if (string.IsNullOrWhiteSpace(vd.Define) || string.IsNullOrWhiteSpace(vd.Name))
                {
                    continue;
                }

                if (EvaluateVersionDefine(vd, unityVersion, packages))
                {
                    symbols.Add(vd.Define.Trim());
                }
            }
        }

        // 5. Extra / user defined symbols
        if (extraSymbols is not null)
        {
            foreach (var extra in extraSymbols)
            {
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    symbols.Add(extra.Trim());
                }
            }
        }

        // Standard Unity defines
        symbols.Add("TRACE");
        symbols.Add("DEBUG");

        return symbols;
    }

    private static void AddVersionSymbols(string unityVersion, HashSet<string> symbols)
    {
        var match = VersionRegex.Match(unityVersion);
        if (!match.Success)
        {
            return;
        }

        if (!int.TryParse(match.Groups["major"].Value, out var major))
        {
            return;
        }

        int minor = 0;
        if (match.Groups["minor"].Success)
        {
            int.TryParse(match.Groups["minor"].Value, out minor);
        }

        symbols.Add($"UNITY_{major}");
        symbols.Add($"UNITY_{major}_OR_NEWER");

        if (match.Groups["minor"].Success)
        {
            symbols.Add($"UNITY_{major}_{minor}");
            symbols.Add($"UNITY_{major}_{minor}_OR_NEWER");
        }

        if (match.Groups["patch"].Success && int.TryParse(match.Groups["patch"].Value, out var patch))
        {
            symbols.Add($"UNITY_{major}_{minor}_{patch}");
        }

        // Generate prior version _OR_NEWER symbols
        foreach (var (kMajor, kMinor) in KnownReleases)
        {
            if (major > kMajor || (major == kMajor && minor >= kMinor))
            {
                symbols.Add($"UNITY_{kMajor}_OR_NEWER");
                symbols.Add($"UNITY_{kMajor}_{kMinor}_OR_NEWER");
            }
        }
    }

    private static void AddPlatformSymbols(HashSet<string> symbols, bool isEditor)
    {
        symbols.Add("UNITY_STANDALONE");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            symbols.Add("UNITY_STANDALONE_WIN");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            symbols.Add("UNITY_STANDALONE_OSX");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            symbols.Add("UNITY_STANDALONE_LINUX");
        }
    }

    public static bool IsEditorAssembly(AsmdefFile asmdef)
    {
        if (asmdef.IncludePlatforms.Length > 0)
        {
            return asmdef.IncludePlatforms.Contains("Editor", StringComparer.OrdinalIgnoreCase);
        }

        if (asmdef.ExcludePlatforms.Length > 0)
        {
            return !asmdef.ExcludePlatforms.Contains("Editor", StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool EvaluateVersionDefine(
        VersionDefine vd,
        string? unityVersion,
        IReadOnlyDictionary<string, string> packages)
    {
        string? versionToCheck = null;

        if (string.Equals(vd.Name, "Unity", StringComparison.OrdinalIgnoreCase))
        {
            versionToCheck = unityVersion;
        }
        else if (packages.TryGetValue(vd.Name, out var pkgVer))
        {
            versionToCheck = pkgVer;
        }

        if (string.IsNullOrWhiteSpace(versionToCheck))
        {
            return false;
        }

        // If expression is empty or "*", simply having the package/version present is enough.
        if (string.IsNullOrWhiteSpace(vd.Expression) || vd.Expression.Trim() == "*")
        {
            return true;
        }

        // Simple semver/prefix check
        return MatchesExpression(versionToCheck, vd.Expression.Trim());
    }

    private static bool MatchesExpression(string version, string expression)
    {
        // Handle interval format: [min,max) or [min,max] or (min,max)
        if ((expression.StartsWith('[') || expression.StartsWith('(')) &&
            (expression.EndsWith(']') || expression.EndsWith(')')))
        {
            var parts = expression[1..^1].Split(',', 2);
            if (parts.Length == 2)
            {
                var minStr = parts[0].Trim();
                var maxStr = parts[1].Trim();

                var minInclude = expression.StartsWith('[');
                var maxInclude = expression.EndsWith(']');

                if (!string.IsNullOrEmpty(minStr))
                {
                    var cmp = CompareVersions(version, minStr);
                    if (minInclude ? cmp < 0 : cmp <= 0) return false;
                }

                if (!string.IsNullOrEmpty(maxStr))
                {
                    var cmp = CompareVersions(version, maxStr);
                    if (maxInclude ? cmp > 0 : cmp >= 0) return false;
                }

                return true;
            }
        }

        // Fallback: minimum version matching (expression e.g. "1.2.0")
        return CompareVersions(version, expression) >= 0;
    }

    private static int CompareVersions(string a, string b)
    {
        var va = ParseVersionNumbers(a);
        var vb = ParseVersionNumbers(b);

        var len = Math.Max(va.Length, vb.Length);
        for (int i = 0; i < len; i++)
        {
            var na = i < va.Length ? va[i] : 0;
            var nb = i < vb.Length ? vb[i] : 0;
            if (na != nb)
            {
                return na.CompareTo(nb);
            }
        }

        return 0;
    }

    private static int[] ParseVersionNumbers(string v)
    {
        var cleaned = Regex.Replace(v, @"[^\d\.]", "");
        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var res = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            int.TryParse(parts[i], out res[i]);
        }
        return res;
    }
}
