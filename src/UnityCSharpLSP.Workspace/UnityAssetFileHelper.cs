using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnityCSharpLSP.Workspace;

/// <summary>
/// Helpers for identifying Unity serialized YAML asset files (<c>.prefab</c>, <c>.unity</c>,
/// <c>.asset</c>, <c>.mat</c>) and resolving their referenced C# Roslyn symbols.
/// </summary>
public static class UnityAssetFileHelper
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="filePath"/> has a Unity YAML-serialized asset
    /// extension (<c>.prefab</c>, <c>.unity</c>, <c>.asset</c>, or <c>.mat</c>).
    /// </summary>
    public static bool IsYamlAssetFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var ext = Path.GetExtension(filePath);
        return string.Equals(ext, ".prefab", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".unity", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".asset", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".mat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the Roslyn <see cref="INamedTypeSymbol"/> (and optionally <see cref="IFieldSymbol"/>)
    /// corresponding to a Unity script GUID / path and serialized field name within
    /// <paramref name="ws"/>.
    /// </summary>
    public static async Task<(INamedTypeSymbol? TypeSymbol, IFieldSymbol? FieldSymbol)> ResolveScriptSymbolAsync(
        UnityWorkspaceManager ws,
        string? scriptGuid,
        string? targetFullPath,
        string? fieldName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ws);

        var scriptPath = await ResolveScriptFilePathAsync(ws, scriptGuid, targetFullPath, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return (null, null);
        }

        var doc = FindDocument(ws, scriptPath);
        if (doc is null)
        {
            return (null, null);
        }

        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var semanticModel = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return (null, null);
        }

        var expectedTypeName = Path.GetFileNameWithoutExtension(scriptPath);
        var typeSymbol = FindPrimaryScriptType(root, semanticModel, expectedTypeName, ct);
        if (typeSymbol is null || string.IsNullOrWhiteSpace(fieldName))
        {
            return (typeSymbol, null);
        }

        var fieldSymbol = FindSerializedField(typeSymbol, fieldName);
        return (typeSymbol, fieldSymbol);
    }

    private static async Task<string?> ResolveScriptFilePathAsync(
        UnityWorkspaceManager ws,
        string? scriptGuid,
        string? targetFullPath,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(targetFullPath) &&
            targetFullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return targetFullPath;
        }

        if (!string.IsNullOrWhiteSpace(scriptGuid))
        {
            var entry = await ws.AssetService.GetAssetByGuidAsync(scriptGuid, ct).ConfigureAwait(false);
            if (entry is not null &&
                !string.IsNullOrWhiteSpace(entry.FullPath) &&
                entry.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return entry.FullPath;
            }
        }

        return null;
    }

    private static Document? FindDocument(UnityWorkspaceManager ws, string scriptPath)
    {
        var doc = ws.GetDocument(scriptPath);
        if (doc is not null)
        {
            return doc;
        }

        var full = Path.GetFullPath(scriptPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var project in ws.CurrentSolution.Projects)
        {
            foreach (var candidate in project.Documents)
            {
                if (!string.IsNullOrEmpty(candidate.FilePath) &&
                    string.Equals(Path.GetFullPath(candidate.FilePath), full, comparison))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static INamedTypeSymbol? FindPrimaryScriptType(
        SyntaxNode root,
        SemanticModel semanticModel,
        string expectedTypeName,
        CancellationToken ct)
    {
        INamedTypeSymbol? bestType = null;
        var bestScore = -1;

        foreach (var decl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(decl, ct) is not INamedTypeSymbol namedType)
            {
                continue;
            }

            var score = 0;
            if (UnitySymbolGuidHelper.IsUnityObjectType(namedType))
            {
                score += 2;
            }

            if (string.Equals(namedType.Name, expectedTypeName, StringComparison.Ordinal))
            {
                score += 1;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestType = namedType;
                if (bestScore == 3)
                {
                    break;
                }
            }
        }

        return bestType;
    }

    private static IFieldSymbol? FindSerializedField(INamedTypeSymbol typeSymbol, string fieldName)
    {
        for (INamedTypeSymbol? current = typeSymbol; current is not null; current = current.BaseType)
        {
            var direct = current.GetMembers(fieldName).OfType<IFieldSymbol>().FirstOrDefault();
            if (direct is not null)
            {
                return direct;
            }
        }

        for (INamedTypeSymbol? current = typeSymbol; current is not null; current = current.BaseType)
        {
            foreach (var field in current.GetMembers().OfType<IFieldSymbol>())
            {
                foreach (var attr in field.GetAttributes())
                {
                    var name = attr.AttributeClass?.Name;
                    if (name is "FormerlySerializedAs" or "FormerlySerializedAsAttribute" &&
                        attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string oldName &&
                        string.Equals(oldName, fieldName, StringComparison.Ordinal))
                    {
                        return field;
                    }
                }
            }
        }

        return null;
    }
}
