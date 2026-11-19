using UnityCSharpLSP.AssetServer.Contracts;

namespace UnityCSharpLSP.AssetServer.Parsing;

/// <summary>
/// Resolves Unity component class IDs to C# type names for completion.
/// Only well-known IDs (and MonoBehaviours with a resolvable script) produce a name so
/// unknown components never become incorrect completion candidates.
/// </summary>
public static class UnityComponentTypeMap
{
    private const int ClassIdMonoBehaviour = 114;

    private static readonly IReadOnlyDictionary<int, string> BuiltinTypeNames =
        new Dictionary<int, string>
        {
            [4] = "Transform",
            [224] = "RectTransform",
        };

    /// <summary>
    /// Attempts to resolve a completion-safe type name for <paramref name="component"/>.
    /// </summary>
    public static bool TryGetTypeName(AssetComponentSummaryDto? component, out string typeName)
    {
        typeName = string.Empty;
        if (component is null)
        {
            return false;
        }

        if (BuiltinTypeNames.TryGetValue(component.ClassId, out string? builtinName))
        {
            typeName = builtinName;
            return true;
        }

        if (component.ClassId == ClassIdMonoBehaviour)
        {
            return TryResolveMonoBehaviourTypeName(component, out typeName);
        }

        return false;
    }

    /// <summary>
    /// Resolves a MonoBehaviour type name from the YAML <c>m_EditorClassIdentifier</c>
    /// (preferred) or from the resolved <c>m_Script</c> path / type name.
    /// </summary>
    private static bool TryResolveMonoBehaviourTypeName(AssetComponentSummaryDto component, out string typeName)
    {
        if (TryExtractIdentifier(component.EditorClassIdentifier, out typeName) ||
            TryExtractIdentifier(component.ScriptPath, out typeName) ||
            TryExtractIdentifier(component.TypeName, out typeName))
        {
            return true;
        }

        typeName = string.Empty;
        return false;
    }

    private static bool TryExtractIdentifier(string? value, out string typeName)
    {
        typeName = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim();

        int separator = candidate.LastIndexOf("::", StringComparison.Ordinal);
        if (separator >= 0)
        {
            candidate = candidate[(separator + 2)..];
        }

        int comma = candidate.IndexOf(',');
        if (comma >= 0)
        {
            candidate = candidate[..comma];
        }

        int generic = candidate.IndexOf('`');
        if (generic >= 0)
        {
            candidate = candidate[..generic];
        }

        int dot = candidate.LastIndexOf('.');
        if (dot >= 0)
        {
            candidate = candidate[(dot + 1)..];
        }

        candidate = candidate.Trim();
        if (!IsValidIdentifier(candidate) || IsPlaceholder(candidate))
        {
            return false;
        }

        typeName = candidate;
        return true;
    }

    private static bool IsValidIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (!(char.IsLetterOrDigit(value[i]) || value[i] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlaceholder(string value)
    {
        if (string.Equals(value, "Missing Script", StringComparison.Ordinal) ||
            string.Equals(value, "MonoBehaviour", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.StartsWith("Component_", StringComparison.Ordinal))
        {
            return value.Length > "Component_".Length &&
                   value["Component_".Length..].All(char.IsDigit);
        }

        return false;
    }
}
