using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;
using LspSymbolKind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/documentSymbol</c> requests to provide the symbol outline of a document.
/// </summary>
public sealed class DocumentSymbolHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;
    private readonly ClientCapabilityStore _clientCapabilities;

    public DocumentSymbolHandler(
        WorkspaceManagerProvider workspaceManagerProvider,
        ClientCapabilityStore clientCapabilities)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
        _clientCapabilities = clientCapabilities;
    }

    [JsonRpcMethod(Methods.TextDocumentDocumentSymbolName, UseSingleObjectParameterDeserialization = true)]
    public async Task<object?> GetDocumentSymbols(DocumentSymbolParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null)
        {
            return null;
        }

        var filePath = @params.TextDocument.Uri.LocalPath;
        var doc = ws.GetDocument(filePath);
        if (doc is null)
        {
            if (!UnityAssetFileHelper.IsYamlAssetFile(filePath))
            {
                return null;
            }

            var hierarchy = await ws.AssetService.GetHierarchyAsync(
                filePath,
                maxDepth: 16,
                includeSerializedValues: false,
                ct).ConfigureAwait(false);

            if (hierarchy.Length == 0)
            {
                return null;
            }

            var assetSymbols = hierarchy.Select(ToDocumentSymbol).ToList();
            return FormatResult(assetSymbols, @params.TextDocument.Uri);
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

        return FormatResult(topLevel, @params.TextDocument.Uri);
    }

    private object? FormatResult(List<DocumentSymbol> topLevel, Uri documentUri)
    {
        if (topLevel.Count == 0)
        {
            return null;
        }

        if (_clientCapabilities.SupportsHierarchicalDocumentSymbols)
        {
            return topLevel.ToArray();
        }

        var flatSymbols = new List<SymbolInformation>();
        foreach (var symbol in topLevel)
        {
            Flatten(symbol, documentUri, containerName: null, flatSymbols);
        }

        return flatSymbols.ToArray();
    }

    private static DocumentSymbol ToDocumentSymbol(AssetHierarchyNodeDto node)
    {
        var line = Math.Max(0, node.Line);
        var pos = new Position(line, 0);
        var range = new LspRange { Start = pos, End = pos };

        var children = new List<DocumentSymbol>();

        foreach (var comp in node.Components)
        {
            var compLine = Math.Max(0, comp.Line);
            var compPos = new Position(compLine, 0);
            var compRange = new LspRange { Start = compPos, End = compPos };
            var typeName = string.IsNullOrWhiteSpace(comp.TypeName)
                ? $"Component_{comp.ClassId}"
                : comp.TypeName;

            children.Add(new DocumentSymbol
            {
                Name = typeName,
                Detail = !string.IsNullOrWhiteSpace(comp.ScriptPath) ? comp.ScriptPath : typeName,
                Kind = LspSymbolKind.Class,
                Range = compRange,
                SelectionRange = compRange,
            });
        }

        foreach (var child in node.Children)
        {
            children.Add(ToDocumentSymbol(child));
        }

        var detail = node.IsPrefabInstance && !string.IsNullOrWhiteSpace(node.SourcePrefabPath)
            ? node.SourcePrefabPath
            : null;

        return new DocumentSymbol
        {
            Name = string.IsNullOrWhiteSpace(node.Name) ? "GameObject" : node.Name,
            Detail = detail,
            Kind = LspSymbolKind.Object,
            Range = range,
            SelectionRange = range,
            Children = children.Count > 0 ? children.ToArray() : null,
        };
    }

    private static void Flatten(
        DocumentSymbol symbol,
        Uri documentUri,
        string? containerName,
        List<SymbolInformation> results)
    {
        results.Add(new SymbolInformation
        {
            Name = symbol.Name,
            Kind = symbol.Kind,
            ContainerName = containerName,
            Location = new Microsoft.VisualStudio.LanguageServer.Protocol.Location
            {
                Uri = documentUri,
                Range = symbol.Range,
            },
        });

        if (symbol.Children is null)
        {
            return;
        }

        foreach (var child in symbol.Children)
        {
            Flatten(child, documentUri, symbol.Name, results);
        }
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
