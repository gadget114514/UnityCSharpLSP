namespace UnityCSharpLSP.Mcp;

/// <summary>
/// Resolves the Unity project workspace root directory for MCP mode.
/// </summary>
public static class WorkspaceResolver
{
    /// <summary>
    /// Returns <paramref name="explicitWorkspace"/> (normalized) when provided; otherwise walks up
    /// from <paramref name="baseDirectory"/> (or the current working directory) to find the nearest
    /// ancestor containing <c>Assets/</c> or <c>ProjectSettings/</c>, falling back to the starting directory.
    /// </summary>
    public static string Resolve(string? explicitWorkspace, string? baseDirectory = null)
        => Resolve(explicitWorkspace, baseDirectory, boundaryDirectory: null);

    internal static string Resolve(string? explicitWorkspace, string? baseDirectory, string? boundaryDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitWorkspace))
        {
            return Path.GetFullPath(explicitWorkspace);
        }

        var startPath = Path.GetFullPath(baseDirectory ?? Directory.GetCurrentDirectory());
        var boundary = string.IsNullOrWhiteSpace(boundaryDirectory) ? null : Path.GetFullPath(boundaryDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var current = new DirectoryInfo(startPath);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Assets")) ||
                Directory.Exists(Path.Combine(current.FullName, "ProjectSettings")))
            {
                return current.FullName;
            }

            if (boundary is not null && string.Equals(current.FullName, boundary, comparison))
            {
                break;
            }

            current = current.Parent;
        }

        return startPath;
    }
}
