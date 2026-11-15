using System.Text.Json;

namespace UnityCSharpLSP.Workspace.Locators;

public enum UnityPackageSource
{
    BuiltIn,
    Registry,
    Git,
    Local,
    Embedded,
    Unknown,
}

/// <summary>
/// A package from the project's manifest and/or lock file, with its resolved on-disk location (null if not found).
/// </summary>
public sealed record ResolvedPackage(
    string Id,
    string Version,
    UnityPackageSource Source,
    string? Location);

/// <summary>
/// An entry from <c>Packages/packages-lock.json</c>.
/// </summary>
public sealed record LockedPackage(
    string? Version,
    string? Source,
    string? Hash,
    string? Url,
    string? Path);

/// <summary>
/// Parses <c>Packages/manifest.json</c> and <c>Packages/packages-lock.json</c> and locates package folders.
/// </summary>
public static class UnityPackageResolver
{
    private const string BuiltInPrefix = "com.unity.modules.";
    private const string FilePrefix = "file:";

    /// <summary>
    /// Returns the <c>dependencies</c> of <c>Packages/manifest.json</c> as packageId -> version spec.
    /// Returns an empty dictionary if the file is missing or malformed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadManifestDependencies(string projectRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = TryParse(Path.Combine(projectRoot, "Packages", "manifest.json"));
        if (document is null ||
            !document.RootElement.TryGetProperty("dependencies", out var deps) ||
            deps.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in deps.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the entries of <c>Packages/packages-lock.json</c>. Returns an empty dictionary if the file is missing or malformed.
    /// </summary>
    public static IReadOnlyDictionary<string, LockedPackage> ReadPackagesLock(string projectRoot)
    {
        var result = new Dictionary<string, LockedPackage>(StringComparer.Ordinal);
        using var document = TryParse(Path.Combine(projectRoot, "Packages", "packages-lock.json"));
        if (document is null ||
            !document.RootElement.TryGetProperty("dependencies", out var deps) ||
            deps.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in deps.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            result[property.Name] = new LockedPackage(
                Version: GetString(property.Value, "version"),
                Source: GetString(property.Value, "source"),
                Hash: GetString(property.Value, "hash"),
                Url: GetString(property.Value, "url"),
                Path: GetString(property.Value, "path"));
        }

        return result;
    }

    /// <summary>
    /// Resolves manifest and lock-file packages (including transitive lock entries) to their folders.
    /// </summary>
    /// <param name="projectRoot">Unity project root.</param>
    /// <param name="editorDataRoot">Optional Editor root (containing <c>Data/</c>) used to locate built-in modules.</param>
    public static IReadOnlyList<ResolvedPackage> Resolve(string projectRoot, string? editorDataRoot = null)
    {
        var manifest = ReadManifestDependencies(projectRoot);
        var locks = ReadPackagesLock(projectRoot);
        var packagesDir = Path.Combine(projectRoot, "Packages");
        var cacheDir = Path.Combine(projectRoot, "Library", "PackageCache");

        var ids = manifest.Keys.Concat(locks.Keys).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
        var result = new List<ResolvedPackage>();

        foreach (var id in ids)
        {
            locks.TryGetValue(id, out var locked);
            manifest.TryGetValue(id, out var spec);
            var version = locked?.Version ?? spec ?? string.Empty;

            var (source, location) = ResolveLocation(id, spec, locked, packagesDir, cacheDir, editorDataRoot);
            result.Add(new ResolvedPackage(id, version, source, location));
        }

        return result;
    }

    private static (UnityPackageSource Source, string? Location) ResolveLocation(
        string id,
        string? spec,
        LockedPackage? locked,
        string packagesDir,
        string cacheDir,
        string? editorDataRoot)
    {
        if (id.StartsWith(BuiltInPrefix, StringComparison.Ordinal) || string.Equals(locked?.Source, "builtin", StringComparison.OrdinalIgnoreCase))
        {
            var builtIn = editorDataRoot is null
                ? null
                : Path.Combine(editorDataRoot, "Data", "Resources", "PackageManager", "BuiltInPackages", id);
            return (UnityPackageSource.BuiltIn, ExistingDirectory(builtIn));
        }

        var localPath = GetLocalPath(spec, locked);
        if (localPath is not null)
        {
            var full = Path.GetFullPath(Path.Combine(packagesDir, localPath));
            return (UnityPackageSource.Local, ExistingDirectory(full));
        }

        if (string.Equals(locked?.Source, "embedded", StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(Path.Combine(packagesDir, id)))
        {
            return (UnityPackageSource.Embedded, ExistingDirectory(Path.Combine(packagesDir, id)));
        }

        if (string.Equals(locked?.Source, "git", StringComparison.OrdinalIgnoreCase) || IsGitSpec(spec))
        {
            return (UnityPackageSource.Git, FindInPackageCache(cacheDir, id, locked?.Hash));
        }

        if (string.Equals(locked?.Source, "registry", StringComparison.OrdinalIgnoreCase) || spec is not null)
        {
            return (UnityPackageSource.Registry, FindInPackageCache(cacheDir, id, locked?.Version ?? spec));
        }

        return (UnityPackageSource.Unknown, null);
    }

    private static string? GetLocalPath(string? spec, LockedPackage? locked)
    {
        if (spec is not null && spec.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return spec[FilePrefix.Length..];
        }

        if (locked?.Version is { } lockVersion && lockVersion.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return lockVersion[FilePrefix.Length..];
        }

        if (locked is not null && string.Equals(locked.Source, "local", StringComparison.OrdinalIgnoreCase))
        {
            return locked.Path ?? locked.Version;
        }

        return null;
    }

    private static bool IsGitSpec(string? spec) =>
        spec is not null &&
        (spec.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
         spec.Contains("://", StringComparison.Ordinal) ||
         spec.EndsWith(".git", StringComparison.OrdinalIgnoreCase));

    // Library/PackageCache entries are named "<id>@<version>" (registry) or "<id>@<hash>" (git).
    private static string? FindInPackageCache(string cacheDir, string id, string? versionOrHash)
    {
        if (!Directory.Exists(cacheDir))
        {
            return null;
        }

        string[] candidates;
        try
        {
            candidates = Directory.GetDirectories(cacheDir, id + "@*");
        }
        catch (IOException)
        {
            return null;
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(versionOrHash))
        {
            // Git cache folders may carry a (short) commit hash suffix, so match by prefix.
            var exact = candidates.FirstOrDefault(c =>
                Path.GetFileName(c).StartsWith(id + "@" + versionOrHash, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return candidates.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).First();
    }

    private static string? ExistingDirectory(string? path) =>
        path is not null && Directory.Exists(path) ? path : null;

    private static JsonDocument? TryParse(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
