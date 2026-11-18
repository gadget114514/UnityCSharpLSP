using Microsoft.CodeAnalysis;
using UnityCSharpLSP.AssetServer.Parsing;

namespace UnityCSharpLSP.Workspace;

/// <summary>
/// Helpers for mapping Roslyn C# symbols to Unity script <c>.meta</c> GUIDs and
/// checking Unity serialization eligibility.
/// </summary>
public static class UnitySymbolGuidHelper
{
    /// <summary>
    /// Attempts to resolve the 32-character lowercase hex Unity script GUID from the
    /// <c>.meta</c> file of the <c>.cs</c> file declaring <paramref name="symbol"/> (or its containing type).
    /// </summary>
    public static bool TryGetScriptGuid(ISymbol? symbol, out string scriptGuid)
    {
        scriptGuid = string.Empty;

        INamedTypeSymbol? targetType = symbol switch
        {
            INamedTypeSymbol namedType => namedType,
            IFieldSymbol or IMethodSymbol or IPropertySymbol => symbol.ContainingType,
            _ => null,
        };

        if (targetType is null)
        {
            return false;
        }

        if (TryReadGuidFromLocations(targetType.Locations, out scriptGuid))
        {
            return true;
        }

        if (!SymbolEqualityComparer.Default.Equals(targetType, targetType.OriginalDefinition))
        {
            return TryReadGuidFromLocations(targetType.OriginalDefinition.Locations, out scriptGuid);
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="type"/> is or inherits from Unity's
    /// <c>MonoBehaviour</c> or <c>ScriptableObject</c> by walking <see cref="ITypeSymbol.BaseType"/>.
    /// </summary>
    public static bool IsUnityObjectType(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        ITypeSymbol? current = type;
        while (current is not null)
        {
            if (string.Equals(current.Name, "MonoBehaviour", StringComparison.Ordinal) ||
                string.Equals(current.Name, "ScriptableObject", StringComparison.Ordinal))
            {
                if (current.TypeKind == TypeKind.Error ||
                    current.ContainingNamespace is null ||
                    current.ContainingNamespace.IsGlobalNamespace ||
                    string.Equals(current.ContainingNamespace.ToDisplayString(), "UnityEngine", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            current = current.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="field"/> is serialized by Unity
    /// (non-static, non-const, non-readonly, without <c>[NonSerialized]</c>, and either
    /// <c>public</c> or marked with <c>[SerializeField]</c> / <c>[SerializeReference]</c>).
    /// </summary>
    public static bool IsSerializableField(IFieldSymbol? field)
    {
        if (field is null || field.IsStatic || field.IsConst || field.IsReadOnly)
        {
            return false;
        }

        bool hasSerializeAttr = false;
        foreach (var attr in field.GetAttributes())
        {
            var name = attr.AttributeClass?.Name;
            if (name is "NonSerialized" or "NonSerializedAttribute")
            {
                return false;
            }

            if (name is "SerializeField" or "SerializeFieldAttribute" or
                "SerializeReference" or "SerializeReferenceAttribute")
            {
                hasSerializeAttr = true;
            }
        }

        return field.DeclaredAccessibility == Accessibility.Public || hasSerializeAttr;
    }

    private static bool TryReadGuidFromLocations(
        IReadOnlyList<Location> locations,
        out string scriptGuid)
    {
        foreach (var location in locations)
        {
            if (!location.IsInSource)
            {
                continue;
            }

            var filePath = location.SourceTree?.FilePath;
            if (string.IsNullOrEmpty(filePath))
            {
                continue;
            }

            if (MetaFileParser.TryReadGuid(filePath + ".meta", out var guid) && !guid.IsZero)
            {
                scriptGuid = guid.ToHexString();
                return true;
            }
        }

        scriptGuid = string.Empty;
        return false;
    }
}
