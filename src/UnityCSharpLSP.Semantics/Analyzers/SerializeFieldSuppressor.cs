using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnityCSharpLSP.Semantics.Analyzers;

/// <summary>
/// Suppresses standard C# compiler warnings that conflict with Unity serialization patterns.
/// In particular, CS0649 ("Field is never assigned to...") for fields marked with [SerializeField].
/// </summary>
public static class SerializeFieldSuppressor
{
    public const string CS0649 = "CS0649";

    /// <summary>
    /// Checks whether a diagnostic should be suppressed based on Unity semantics.
    /// </summary>
    public static bool ShouldSuppress(Diagnostic diagnostic, SemanticModel semanticModel)
    {
        if (diagnostic.Id != CS0649)
        {
            return false;
        }

        var root = semanticModel.SyntaxTree.GetRoot();
        var node = root.FindNode(diagnostic.Location.SourceSpan);

        // Find the variable declarator or field declaration
        var variableDeclarator = node.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        var fieldDeclaration = node.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault();

        if (fieldDeclaration is null && variableDeclarator is null)
        {
            return false;
        }

        // Check declared symbol attributes
        ISymbol? symbol = null;
        if (variableDeclarator is not null)
        {
            symbol = semanticModel.GetDeclaredSymbol(variableDeclarator);
        }

        if (symbol is IFieldSymbol fieldSymbol)
        {
            return HasSerializeFieldAttribute(fieldSymbol);
        }

        // Fallback: check syntax attribute lists directly
        if (fieldDeclaration is not null)
        {
            return HasSerializeFieldSyntax(fieldDeclaration);
        }

        return false;
    }

    private static bool HasSerializeFieldAttribute(IFieldSymbol field)
    {
        foreach (var attr in field.GetAttributes())
        {
            var name = attr.AttributeClass?.Name;
            if (name == "SerializeField" || name == "SerializeFieldAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSerializeFieldSyntax(FieldDeclarationSyntax fieldDeclaration)
    {
        foreach (var attrList in fieldDeclaration.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (name == "SerializeField" || name == "SerializeFieldAttribute")
                {
                    return true;
                }
            }
        }

        return false;
    }
}
