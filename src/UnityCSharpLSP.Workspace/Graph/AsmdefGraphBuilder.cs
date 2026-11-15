using System.Text.Json;
using UnityCSharpLSP.Workspace.Models;

namespace UnityCSharpLSP.Workspace.Graph;

/// <summary>
/// Discovers <c>.asmdef</c> files under a directory (typically <c>Assets/</c>), assigns every <c>.cs</c> file
/// to its owning assembly and resolves the <c>references</c> graph.
/// </summary>
public static class AsmdefGraphBuilder
{
    public const string AssemblyCSharpName = "Assembly-CSharp";
    public const string AssemblyCSharpEditorName = "Assembly-CSharp-Editor";

    private const string GuidPrefix = "GUID:";

    private static readonly string[] ExcludedDirectoryNames = ["Library", "Temp", "Logs", "obj", "bin"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Builds the assembly graph for everything under <paramref name="searchRoot"/>.
    /// GUID references are resolved against <c>*.asmdef.meta</c> files under the same root.
    /// </summary>
    public static AsmdefGraph Build(string searchRoot)
    {
        var root = Path.GetFullPath(searchRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var warnings = new List<string>();

        var asmdefs = LoadAsmdefFiles(root, warnings);
        var asmdefByDirectory = new Dictionary<string, AsmdefFile>(StringComparer.OrdinalIgnoreCase);
        var asmdefByName = new Dictionary<string, AsmdefFile>(StringComparer.Ordinal);

        foreach (var asmdef in asmdefs)
        {
            if (!asmdefByDirectory.TryAdd(asmdef.DirectoryPath, asmdef))
            {
                warnings.Add($"Multiple .asmdef files in one folder; ignoring '{asmdef.FilePath}'.");
                continue;
            }

            if (!asmdefByName.TryAdd(asmdef.Name, asmdef))
            {
                warnings.Add($"Duplicate assembly name '{asmdef.Name}' ('{asmdef.FilePath}'); the first definition is used.");
            }
        }

        var guidToName = BuildGuidMap(root, asmdefs);

        // Assign source files to assemblies.
        var sourceFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var fileOwnership = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var csFile in EnumerateFiles(root, "*.cs"))
        {
            var owner = FindOwningAsmdef(csFile, root, asmdefByDirectory);
            if (owner is not null && !ReferenceEquals(asmdefByName[owner.Name], owner))
            {
                // Shadowed by a duplicate assembly name (already reported in warnings); leave the file unowned.
                continue;
            }

            var assemblyName = owner?.Name
                ?? (IsInEditorFolder(csFile, root) ? AssemblyCSharpEditorName : AssemblyCSharpName);

            if (!sourceFiles.TryGetValue(assemblyName, out var list))
            {
                sourceFiles[assemblyName] = list = [];
            }

            list.Add(csFile);
            fileOwnership[csFile] = assemblyName;
        }

        // Implicit assemblies: create Assembly-CSharp-Editor only when needed; it always references Assembly-CSharp.
        if (sourceFiles.ContainsKey(AssemblyCSharpEditorName) && !sourceFiles.ContainsKey(AssemblyCSharpName))
        {
            sourceFiles[AssemblyCSharpName] = [];
        }

        var autoReferenced = asmdefByName.Values.Where(a => a.AutoReferenced).Select(a => a.Name).ToList();

        var nodes = new Dictionary<string, AssemblyNode>(StringComparer.Ordinal);

        foreach (var (name, asmdef) in asmdefByName)
        {
            nodes[name] = CreateNode(name, asmdef, root, sourceFiles, asmdef.References, guidToName, asmdefByName, warnings, Array.Empty<string>());
        }

        foreach (var implicitName in new[] { AssemblyCSharpName, AssemblyCSharpEditorName })
        {
            if (!sourceFiles.ContainsKey(implicitName) || nodes.ContainsKey(implicitName))
            {
                continue;
            }

            // Assembly-CSharp only auto-references asmdefs that are not editor-only; the Editor assembly sees all of them
            // and always references Assembly-CSharp.
            var autoRefs = implicitName == AssemblyCSharpName
                ? autoReferenced.Where(n => !IsEditorOnly(asmdefByName[n])).ToArray()
                : [AssemblyCSharpName, .. autoReferenced];

            nodes[implicitName] = CreateNode(implicitName, null, root, sourceFiles, [], guidToName, asmdefByName, warnings, autoRefs);
        }

        return new AsmdefGraph
        {
            Assemblies = nodes,
            FileOwnership = fileOwnership,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Recursively parses all <c>*.asmdef</c> files under <paramref name="searchRoot"/>.
    /// Malformed files are skipped and reported in <paramref name="warnings"/> when provided.
    /// </summary>
    public static IReadOnlyList<AsmdefFile> LoadAsmdefFiles(string searchRoot, List<string>? warnings = null)
    {
        var result = new List<AsmdefFile>();
        foreach (var path in EnumerateFiles(Path.GetFullPath(searchRoot), "*.asmdef"))
        {
            var asmdef = TryParseAsmdef(path, out var error);
            if (asmdef is not null)
            {
                result.Add(asmdef);
            }
            else
            {
                warnings?.Add($"Failed to parse '{path}': {error}");
            }
        }

        return result;
    }

    /// <summary>Parses a single .asmdef file. Returns null and an error message on failure.</summary>
    public static AsmdefFile? TryParseAsmdef(string path, out string? error)
    {
        error = null;
        try
        {
            using var stream = File.OpenRead(path);
            var asmdef = JsonSerializer.Deserialize<AsmdefFile>(stream, JsonOptions);
            if (asmdef is null)
            {
                error = "empty document";
                return null;
            }

            asmdef.FilePath = Path.GetFullPath(path);
            if (string.IsNullOrWhiteSpace(asmdef.Name))
            {
                asmdef.Name = Path.GetFileNameWithoutExtension(path);
            }

            asmdef.References ??= [];
            asmdef.VersionDefines ??= [];
            return asmdef;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static AssemblyNode CreateNode(
        string name,
        AsmdefFile? asmdef,
        string root,
        Dictionary<string, List<string>> sourceFiles,
        IEnumerable<string> explicitReferences,
        Dictionary<string, string> guidToName,
        Dictionary<string, AsmdefFile> asmdefByName,
        List<string> warnings,
        IEnumerable<string> autoReferences)
    {
        var references = new List<string>();
        var unresolved = new List<string>();

        foreach (var reference in explicitReferences)
        {
            string? target;
            if (reference.StartsWith(GuidPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var guid = reference[GuidPrefix.Length..].Trim();
                target = guidToName.GetValueOrDefault(guid);
                if (target is null)
                {
                    unresolved.Add(reference);
                    warnings.Add($"Assembly '{name}' references unknown {reference}.");
                    continue;
                }
            }
            else
            {
                target = reference;
            }

            if (asmdefByName.ContainsKey(target))
            {
                AddUnique(references, target, name);
            }
            else
            {
                unresolved.Add(reference);
            }
        }

        foreach (var auto in autoReferences)
        {
            AddUnique(references, auto, name);
        }

        return new AssemblyNode
        {
            Name = name,
            Asmdef = asmdef,
            RootDirectory = asmdef?.DirectoryPath ?? root,
            SourceFiles = sourceFiles.TryGetValue(name, out var files) ? files : [],
            References = references,
            UnresolvedReferences = unresolved,
        };
    }

    private static void AddUnique(List<string> list, string item, string self)
    {
        if (!string.Equals(item, self, StringComparison.Ordinal) &&
            !list.Contains(item, StringComparer.Ordinal))
        {
            list.Add(item);
        }
    }

    private static Dictionary<string, string> BuildGuidMap(string root, IReadOnlyList<AsmdefFile> asmdefs)
    {
        var asmdefByPath = asmdefs.ToDictionary(a => a.FilePath, a => a.Name, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var metaPath in EnumerateFiles(root, "*.asmdef.meta"))
        {
            var asmdefPath = metaPath[..^".meta".Length];
            if (!asmdefByPath.TryGetValue(asmdefPath, out var name))
            {
                continue;
            }

            string? guid = null;
            try
            {
                foreach (var line in File.ReadLines(metaPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("guid:", StringComparison.Ordinal))
                    {
                        guid = trimmed["guid:".Length..].Trim();
                        break;
                    }
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(guid))
            {
                map[guid] = name;
            }
        }

        return map;
    }

    // Nearest ancestor folder (up to and including root) that contains an .asmdef.
    private static AsmdefFile? FindOwningAsmdef(string csFile, string root, Dictionary<string, AsmdefFile> byDirectory)
    {
        var dir = Path.GetDirectoryName(csFile);
        while (dir is not null && dir.Length >= root.Length)
        {
            if (byDirectory.TryGetValue(dir, out var asmdef))
            {
                return asmdef;
            }

            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    // True when a folder named "Editor" lies between root and the file.
    private static bool IsInEditorFolder(string csFile, string root)
    {
        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(csFile) ?? root);
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "Editor", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEditorOnly(AsmdefFile asmdef) =>
        asmdef.IncludePlatforms.Any(p => string.Equals(p, "Editor", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            string[] files;
            string[] subDirs;
            try
            {
                files = Directory.GetFiles(dir, pattern);
                subDirs = Directory.GetDirectories(dir);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Push(sub);
            }
        }
    }
}
