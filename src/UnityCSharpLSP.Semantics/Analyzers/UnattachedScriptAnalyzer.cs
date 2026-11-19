using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.Workspace;

#pragma warning disable RS1032 // message intentionally keeps the trailing period
#pragma warning disable RS2008 // this is a workspace diagnostic, not a shipped Roslyn analyzer

namespace UnityCSharpLSP.Semantics.Analyzers;

/// <summary>
/// Reports concrete <c>MonoBehaviour</c> / <c>ScriptableObject</c> scripts that are never
/// attached to a prefab or scene as <c>UNITY0004</c> hints on the declaring C# document.
/// </summary>
public static class UnattachedScriptAnalyzer
{
    public const string DiagnosticId = "UNITY0004";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Unattached Unity script",
        messageFormat: "{0} is not attached to any prefab or scene.",
        category: "Unity",
        defaultSeverity: DiagnosticSeverity.Hidden,
        isEnabledByDefault: true);

    /// <summary>
    /// Enumerates attachable Unity script types in <paramref name="ws"/> and returns a hint
    /// for each type whose script GUID never appears in any Unity asset attachment.
    /// When <paramref name="filePath"/> is supplied, only diagnostics for that document are returned.
    /// </summary>
    public static async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
        UnityWorkspaceManager ws,
        string? filePath = null,
        CancellationToken ct = default)
    {
        var results = new List<Diagnostic>();
        var seenGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in ws.CurrentSolution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            bool isEditorProject = project.ParseOptions is CSharpParseOptions csharpOptions &&
                                   csharpOptions.PreprocessorSymbolNames.Contains("UNITY_EDITOR");

            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace))
            {
                ct.ThrowIfCancellationRequested();

                if (!IsAttachableScript(type) || isEditorProject || IsEditorOnlySource(ws, type))
                {
                    continue;
                }

                if (!UnitySymbolGuidHelper.TryGetScriptGuid(type, out var scriptGuid) ||
                    string.IsNullOrEmpty(scriptGuid) ||
                    !seenGuids.Add(scriptGuid))
                {
                    continue;
                }

                var location = type.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree is not null);
                var sourcePath = location?.SourceTree?.FilePath;
                if (location is null || string.IsNullOrEmpty(sourcePath))
                {
                    continue;
                }

                if (filePath is not null && !PathsEqual(sourcePath, filePath))
                {
                    continue;
                }

                var references = await ws.AssetService.FindReferencesAsync(
                    new AssetReferenceQueryDto
                    {
                        ScriptGuid = scriptGuid,
                        MemberKind = AssetMemberKind.Class,
                        IncludeTransitiveScenes = true,
                        MaxResults = 1,
                    },
                    ct).ConfigureAwait(false);

                if (references.Locations.Length == 0)
                {
                    results.Add(Diagnostic.Create(Rule, location, type.Name));
                }
            }
        }

        return results;
    }

    private static bool IsAttachableScript(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic ||
            !UnitySymbolGuidHelper.IsUnityObjectType(type))
        {
            return false;
        }

        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.Arity > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEditorOnlySource(UnityWorkspaceManager ws, INamedTypeSymbol type)
    {
        foreach (var location in type.Locations)
        {
            if (!location.IsInSource)
            {
                continue;
            }

            var sourcePath = location.SourceTree?.FilePath;
            if (string.IsNullOrEmpty(sourcePath))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(ws.ProjectInfo.ProjectRootPath, sourcePath);
            var directory = Path.GetDirectoryName(relativePath);
            if (string.IsNullOrEmpty(directory) || directory.StartsWith("..", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var segment in directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.Equals(segment, "Editor", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                foreach (var type in EnumerateTypes(ns))
                {
                    yield return type;
                }
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in EnumerateTypes(type))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        return string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
