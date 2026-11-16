using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/documentSymbol</c> requests to provide the symbol outline of a document.
/// </summary>
public sealed class DocumentSymbolHandler
{
    private readonly Func<UnityWorkspaceManager?> _workspaceManagerProvider;

    public DocumentSymbolHandler(Func<UnityWorkspaceManager?> workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentDocumentSymbolName, UseSingleObjectParameterDeserialization = true)]
    public async Task<DocumentSymbol[]?> GetDocumentSymbols(DocumentSymbolParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null || @params.TextDocument?.Uri is null)
        {
            return null;
        }

        var doc = ws.GetDocument(@params.TextDocument.Uri.LocalPath);
        if (doc is null)
        {
            return null;
        }

        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var semanticModel = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var tree = await doc.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        if (root is null || semanticModel is null || tree is null)
        {
            return null;
        }

        var entries = CollectDeclarations(root, semanticModel, ct);
        if (entries.Count == 0)
        {
            return null;
        }

        var symbolsByNode = new Dictionary<SyntaxNode, DocumentSymbol>(ReferenceEqualityComparer.Instance);
        var childrenByNode = new Dictionary<SyntaxNode, List<DocumentSymbol>>(ReferenceEqualityComparer.Instance);
        var topLevel = new List<DocumentSymbol>();

        foreach (var entry in entries)
        {
            var fullRange = LspSymbolMapper.ToRange(entry.RangeNode.GetLocation().GetLineSpan());
            var documentSymbol = new DocumentSymbol
            {
                Name = LspSymbolMapper.GetDisplayName(entry.Symbol),
                Detail = LspSymbolMapper.GetDetail(entry.Symbol),
                Kind = LspSymbolMapper.ToSymbolKind(entry.Symbol),
                Range = fullRange,
                SelectionRange = LspSymbolMapper.GetSelectionRange(entry.Symbol, tree, fullRange),
            };

            symbolsByNode[entry.Node] = documentSymbol;

            var parentNode = FindParentDeclaration(entry.Node, symbolsByNode);
            if (parentNode is null)
            {
                topLevel.Add(documentSymbol);
            }
            else
            {
                if (!childrenByNode.TryGetValue(parentNode, out var children))
                {
                    children = new List<DocumentSymbol>();
                    childrenByNode[parentNode] = children;
                }

                children.Add(documentSymbol);
            }
        }

        foreach (var (node, children) in childrenByNode)
        {
            if (symbolsByNode.TryGetValue(node, out var parent))
            {
                parent.Children = children.ToArray();
            }
        }

        return topLevel.Count > 0 ? topLevel.ToArray() : null;
    }

    private static List<DeclarationEntry> CollectDeclarations(
        SyntaxNode root,
        SemanticModel semanticModel,
        CancellationToken ct)
    {
        var entries = new List<DeclarationEntry>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax typeDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(typeDeclaration, ct), node);
                    break;
                case DelegateDeclarationSyntax delegateDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(delegateDeclaration, ct), node);
                    break;
                case NamespaceDeclarationSyntax namespaceDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(namespaceDeclaration, ct), node);
                    break;
                case FileScopedNamespaceDeclarationSyntax fileScopedNamespace:
                    Add(semanticModel.GetDeclaredSymbol(fileScopedNamespace, ct), node);
                    break;
                case MethodDeclarationSyntax methodDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(methodDeclaration, ct), node);
                    break;
                case ConstructorDeclarationSyntax constructorDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(constructorDeclaration, ct), node);
                    break;
                case DestructorDeclarationSyntax destructorDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(destructorDeclaration, ct), node);
                    break;
                case OperatorDeclarationSyntax operatorDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(operatorDeclaration, ct), node);
                    break;
                case ConversionOperatorDeclarationSyntax conversionDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(conversionDeclaration, ct), node);
                    break;
                case PropertyDeclarationSyntax propertyDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(propertyDeclaration, ct), node);
                    break;
                case IndexerDeclarationSyntax indexerDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(indexerDeclaration, ct), node);
                    break;
                case EventDeclarationSyntax eventDeclaration:
                    Add(semanticModel.GetDeclaredSymbol(eventDeclaration, ct), node);
                    break;
                case EventFieldDeclarationSyntax eventFieldDeclaration:
                    foreach (var variable in eventFieldDeclaration.Declaration.Variables)
                    {
                        Add(semanticModel.GetDeclaredSymbol(variable, ct), variable, fieldOrEventNode: node);
                    }
                    break;
                case FieldDeclarationSyntax fieldDeclaration:
                    foreach (var variable in fieldDeclaration.Declaration.Variables)
                    {
                        Add(semanticModel.GetDeclaredSymbol(variable, ct), variable, fieldOrEventNode: node);
                    }
                    break;
                case EnumMemberDeclarationSyntax enumMember:
                    Add(semanticModel.GetDeclaredSymbol(enumMember, ct), node);
                    break;
            }
        }

        return entries;

        void Add(ISymbol? symbol, SyntaxNode declarationNode, SyntaxNode? fieldOrEventNode = null)
        {
            if (symbol is null || symbol.IsImplicitlyDeclared)
            {
                return;
            }

            entries.Add(new DeclarationEntry(symbol, declarationNode, fieldOrEventNode ?? declarationNode));
        }
    }

    private static SyntaxNode? FindParentDeclaration(
        SyntaxNode node,
        Dictionary<SyntaxNode, DocumentSymbol> symbolsByNode)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (symbolsByNode.ContainsKey(parent))
            {
                return parent;
            }
        }

        return null;
    }

    private readonly record struct DeclarationEntry(ISymbol Symbol, SyntaxNode Node, SyntaxNode RangeNode);
}
