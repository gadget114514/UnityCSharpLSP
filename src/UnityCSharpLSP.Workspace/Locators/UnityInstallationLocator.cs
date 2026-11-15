using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnityCSharpLSP.Workspace.Locators;

public enum UnityLocationSource
{
    None,
    ExplicitPath,
    EditorInstance,
    EditorsJson,
    StandardPath,
    ProjectLibrary,
}

/// <summary>
/// Result of locating the Unity Editor / managed assemblies. Either property may be null;
/// <see cref="Warning"/> explains why when nothing was found.
/// </summary>
public sealed record UnityInstallationResult(
    string? EditorPath,
    string? ManagedDirectory,
    UnityLocationSource Source,
    string? Warning);

/// <summary>
/// Locates the Unity Editor installation that matches a project and its managed DLL directory.
/// Never throws for missing or malformed inputs; failures are reported through <see cref="UnityInstallationResult.Warning"/>.
/// </summary>
public static class UnityInstallationLocator
{
    public const string EditorPathEnvironmentVariable = "UNITY_EDITOR_PATH";

    private static readonly Regex EditorVersionRegex =
        new(@"m_EditorVersion:\s*([0-9a-zA-Z\.\-]+)", RegexOptions.Compiled);

    /// <summary>
    /// Reads the Unity version from <c>ProjectSettings/ProjectVersion.txt</c>, e.g. <c>2022.3.20f1</c>.
    /// </summary>
    public static bool TryGetProjectVersion(string projectRoot, out string? version)
    {
        version = null;
        var path = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var match = EditorVersionRegex.Match(File.ReadAllText(path));
            if (!match.Success)
            {
                return false;
            }

            version = match.Groups[1].Value;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Locates the Editor in priority order: explicit path (argument or <c>UNITY_EDITOR_PATH</c>),
    /// Unity Hub <c>editors.json</c>, standard install paths, and finally the project's
    /// <c>Library/ScriptAssemblies</c> as a fallback.
    /// </summary>
    public static UnityInstallationResult Locate(
        string projectRoot,
        string? unityVersion = null,
        string? explicitEditorPath = null,
        string? editorsJsonPathOverride = null)
    {
        var explicitPath = explicitEditorPath;
        if (string.IsNullOrWhiteSpace(explicitPath))
        {
            explicitPath = Environment.GetEnvironmentVariable(EditorPathEnvironmentVariable);
        }

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var root = TryResolveEditorRoot(explicitPath);
            if (root is not null)
            {
                return Found(root, UnityLocationSource.ExplicitPath);
            }
        }

        // Check active/recent editor instance recorded in Library/EditorInstance.json
        if (editorsJsonPathOverride is null && TryGetEditorInstance(projectRoot, out var contentsPath, out var appPath, out _))
        {
            var instanceCandidate = contentsPath ?? appPath;
            if (!string.IsNullOrWhiteSpace(instanceCandidate))
            {
                var root = TryResolveEditorRoot(instanceCandidate);
                if (root is not null)
                {
                    return Found(root, UnityLocationSource.EditorInstance);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(unityVersion))
        {
            foreach (var candidate in EnumerateEditorsJsonLocations(unityVersion, editorsJsonPathOverride))
            {
                var root = TryResolveEditorRoot(candidate);
                if (root is not null)
                {
                    return Found(root, UnityLocationSource.EditorsJson);
                }
            }

            foreach (var candidate in EnumerateStandardInstallCandidates(unityVersion))
            {
                var root = TryResolveEditorRoot(candidate);
                if (root is not null)
                {
                    return Found(root, UnityLocationSource.StandardPath);
                }
            }
        }

        // Fallback: If exact version not found and no override is set, look for any installed Unity Editor in Hub or standard paths
        if (editorsJsonPathOverride is null)
        {
            foreach (var candidate in EnumerateAnyInstalledEditorCandidates())
            {
                var root = TryResolveEditorRoot(candidate);
                if (root is not null)
                {
                    var warning = !string.IsNullOrWhiteSpace(unityVersion)
                        ? $"Unity Editor {unityVersion} not found; falling back to installed Editor at {root}."
                        : null;
                    return new UnityInstallationResult(
                        EditorPath: root,
                        ManagedDirectory: Path.Combine(root, "Data", "Managed"),
                        Source: UnityLocationSource.StandardPath,
                        Warning: warning);
                }
            }
        }

        var scriptAssemblies = Path.Combine(projectRoot, "Library", "ScriptAssemblies");
        if (Directory.Exists(scriptAssemblies))
        {
            return new UnityInstallationResult(
                EditorPath: null,
                ManagedDirectory: scriptAssemblies,
                Source: UnityLocationSource.ProjectLibrary,
                Warning: "Unity Editor not found; using Library/ScriptAssemblies as a fallback. Engine references may be missing.");
        }

        return new UnityInstallationResult(
            EditorPath: null,
            ManagedDirectory: null,
            Source: UnityLocationSource.None,
            Warning: $"Unity Editor {unityVersion ?? "(unknown version)"} not found. Set {EditorPathEnvironmentVariable} to the Editor install directory.");
    }

    /// <summary>
    /// Given an Editor directory (or Unity.exe / Unity.app path), returns the directory that contains
    /// <c>Data/Managed</c>, or null if none exists.
    /// </summary>
    public static string? TryResolveEditorRoot(string path)
    {
        try
        {
            var candidates = new List<string> { path };

            if (File.Exists(path))
            {
                var parent = Path.GetDirectoryName(path);
                if (parent is not null)
                {
                    candidates.Add(parent);
                }
            }
            else
            {
                candidates.Add(Path.Combine(path, "Editor"));
                candidates.Add(Path.Combine(path, "Contents"));
                candidates.Add(Path.Combine(path, "Unity.app", "Contents"));
            }

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(Path.Combine(candidate, "Data", "Managed")))
                {
                    return Path.GetFullPath(candidate);
                }

                if (Directory.Exists(Path.Combine(candidate, "Managed")))
                {
                    var parent = Path.GetDirectoryName(candidate);
                    if (parent is not null && Directory.Exists(Path.Combine(parent, "Data", "Managed")))
                    {
                        return Path.GetFullPath(parent);
                    }
                    return Path.GetFullPath(candidate);
                }
            }
        }
        catch (ArgumentException)
        {
            // Invalid path characters.
        }

        return null;
    }

    /// <summary>
    /// Reads the active or recent Unity Editor instance from <c>Library/EditorInstance.json</c>.
    /// </summary>
    public static bool TryGetEditorInstance(string projectRoot, out string? appContentsPath, out string? appPath, out string? version)
    {
        appContentsPath = null;
        appPath = null;
        version = null;

        var path = Path.Combine(projectRoot, "Library", "EditorInstance.json");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = doc.RootElement;
            if (root.TryGetProperty("app_contents_path", out var contentsElem) && contentsElem.ValueKind == JsonValueKind.String)
            {
                appContentsPath = contentsElem.GetString();
            }
            if (root.TryGetProperty("app_path", out var appElem) && appElem.ValueKind == JsonValueKind.String)
            {
                appPath = appElem.GetString();
            }
            if (root.TryGetProperty("version", out var verElem) && verElem.ValueKind == JsonValueKind.String)
            {
                version = verElem.GetString();
            }

            return !string.IsNullOrWhiteSpace(appContentsPath) || !string.IsNullOrWhiteSpace(appPath);
        }
        catch
        {
            return false;
        }
    }

    private static UnityInstallationResult Found(string editorRoot, UnityLocationSource source) =>
        new(EditorPath: editorRoot,
            ManagedDirectory: Path.Combine(editorRoot, "Data", "Managed"),
            Source: source,
            Warning: null);

    private static IEnumerable<string> EnumerateEditorsJsonLocations(string? version, string? overridePath)
    {
        var path = overridePath ?? GetDefaultEditorsJsonPath();
        if (path is null || !File.Exists(path))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            using var stream = File.OpenRead(path);
            document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var entry in EnumerateEditorEntries(document.RootElement))
            {
                if (version is not null)
                {
                    if (!entry.TryGetProperty("version", out var versionElement) ||
                        versionElement.ValueKind != JsonValueKind.String ||
                        !string.Equals(versionElement.GetString(), version, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (!entry.TryGetProperty("location", out var location))
                {
                    continue;
                }

                if (location.ValueKind == JsonValueKind.String)
                {
                    yield return location.GetString()!;
                }
                else if (location.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in location.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            yield return item.GetString()!;
                        }
                    }
                }
            }
        }
    }

    public static IEnumerable<string> EnumerateAnyInstalledEditorCandidates(string? editorsJsonPathOverride = null)
    {
        // 1. From editors.json (any version)
        foreach (var loc in EnumerateEditorsJsonLocations(null, editorsJsonPathOverride))
        {
            yield return loc;
        }

        // 2. From standard Hub directory
        string? hubEditorDir = null;
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            hubEditorDir = Path.Combine(pf, "Unity", "Hub", "Editor");
        }
        else if (OperatingSystem.IsMacOS())
        {
            hubEditorDir = Path.Combine("/Applications", "Unity", "Hub", "Editor");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                hubEditorDir = Path.Combine(home, "Unity", "Hub", "Editor");
            }
        }

        if (hubEditorDir is not null && Directory.Exists(hubEditorDir))
        {
            IEnumerable<string> subdirs = [];
            try
            {
                subdirs = Directory.EnumerateDirectories(hubEditorDir)
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
            }

            foreach (var dir in subdirs)
            {
                if (OperatingSystem.IsMacOS())
                {
                    yield return Path.Combine(dir, "Unity.app", "Contents");
                }
                else
                {
                    yield return Path.Combine(dir, "Editor");
                }
            }
        }
    }

    // Supports both the legacy root array / {"editors": [...]} layout and the newer {"data": [...]} layout.
    private static IEnumerable<JsonElement> EnumerateEditorEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "data", "editors" })
            {
                if (root.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array)
                {
                    return array.EnumerateArray();
                }
            }
        }

        return [];
    }

    private static string? GetDefaultEditorsJsonPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "UnityHub", "editors.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "UnityHub", "editors.json")
            : Path.Combine(home, ".config", "UnityHub", "editors.json");
    }

    private static IEnumerable<string> EnumerateStandardInstallCandidates(string version)
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(programFiles, "Unity", "Hub", "Editor", version, "Editor");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine("/Applications", "Unity", "Hub", "Editor", version, "Unity.app", "Contents");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, "Unity", "Hub", "Editor", version, "Editor");
            }
        }
    }
}
