using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Semantics.Analyzers;

/// <summary>
/// Suppresses standard C# compiler and analyzer warnings that conflict with Unity patterns:
/// <list type="bullet">
/// <item><description><c>CS0649</c> ("Field is never assigned to...") for fields marked with <c>[SerializeField]</c>.</description></item>
/// <item><description><c>IDE0051</c> ("Private member is unused") for methods referenced by <c>UnityEvent</c>s in scenes/prefabs.</description></item>
/// </list>
/// </summary>
public static class SerializeFieldSuppressor
{
    public const string CS0649 = "CS0649";
    public const string IDE0051 = "IDE0051";

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

    /// <summary>
    /// Checks whether a diagnostic should be suppressed based on Unity semantics, including
    /// <c>UnityEvent</c> method references queried via <paramref name="assetService"/>.
    /// </summary>
    public static async Task<bool> ShouldSuppressAsync(
        Diagnostic diagnostic,
        SemanticModel semanticModel,
        IAssetDataService? assetService,
        CancellationToken ct = default)
    {
        if (ShouldSuppress(diagnostic, semanticModel))
        {
            return true;
        }

        if (assetService is null || diagnostic.Id != IDE0051)
        {
            return false;
        }

        if (!diagnostic.Location.IsInSource || diagnostic.Location.SourceTree != semanticModel.SyntaxTree)
        {
            return false;
        }

        var root = await semanticModel.SyntaxTree.GetRootAsync(ct).ConfigureAwait(false);
        var node = root.FindNode(diagnostic.Location.SourceSpan);

        // Ensure the diagnostic targets the method declaration itself, not parameters or method body.
        if (node.AncestorsAndSelf().Any(a => a is BlockSyntax or ArrowExpressionClauseSyntax or ParameterListSyntax))
        {
            return false;
        }

        var methodDeclaration = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDeclaration is null)
        {
            return false;
        }

        if (semanticModel.GetDeclaredSymbol(methodDeclaration, ct) is not IMethodSymbol methodSymbol ||
            methodSymbol.MethodKind != MethodKind.Ordinary ||
            !UnitySymbolGuidHelper.IsUnityObjectType(methodSymbol.ContainingType) ||
            !UnitySymbolGuidHelper.TryGetScriptGuid(methodSymbol, out var scriptGuid))
        {
            return false;
        }

        return await assetService.IsMethodReferencedByUnityEventAsync(scriptGuid, methodSymbol.Name, ct)
            .ConfigureAwait(false);
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
