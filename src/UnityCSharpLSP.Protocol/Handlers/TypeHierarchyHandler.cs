using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles type hierarchy requests (LSP 3.17): prepare, supertypes, and subtypes.
/// </summary>
public sealed class TypeHierarchyHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public TypeHierarchyHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(TypeHierarchyMethods.PrepareName, UseSingleObjectParameterDeserialization = true)]
    public async Task<TypeHierarchyItem[]?> PrepareTypeHierarchy(
        TextDocumentPositionParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var (symbol, _) = await ws.GetSymbolAtPositionAsync(
            @params.TextDocument.Uri.LocalPath,
            @params.Position.Line,
            @params.Position.Character,
            ct).ConfigureAwait(false);

        var type = NormalizeType(symbol);
        if (type is null)
        {
            return null;
        }

        var item = CreateItem(type);
        return item is null ? null : new[] { item };
    }

    [JsonRpcMethod(TypeHierarchyMethods.SupertypesName, UseSingleObjectParameterDeserialization = true)]
    public async Task<TypeHierarchyItem[]?> Supertypes(
        TypeHierarchySupertypesParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params?.Item is null)
        {
            return null;
        }

        var type = await ResolveItemAsync(ws, @params.Item, ct).ConfigureAwait(false);
        if (type is null)
        {
            return null;
        }

        var supertypes = new List<INamedTypeSymbol>();
        if (type.BaseType is not null)
        {
            supertypes.Add(type.BaseType);
        }

        supertypes.AddRange(type.Interfaces);

        var results = new List<TypeHierarchyItem>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var supertype in supertypes)
        {
            var normalized = NormalizeType(supertype);
            if (normalized is null || !seen.Add(normalized))
            {
                continue;
            }

            var item = CreateItem(normalized);
            if (item is not null)
            {
                results.Add(item);
            }
        }

        return results.Count > 0 ? results.ToArray() : null;
    }

    [JsonRpcMethod(TypeHierarchyMethods.SubtypesName, UseSingleObjectParameterDeserialization = true)]
    public async Task<TypeHierarchyItem[]?> Subtypes(
        TypeHierarchySubtypesParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params?.Item is null)
        {
            return null;
        }

        var type = await ResolveItemAsync(ws, @params.Item, ct).ConfigureAwait(false);
        if (type is null)
        {
            return null;
        }

        IEnumerable<ISymbol> subtypes;
        try
        {
            if (type.TypeKind == TypeKind.Interface)
            {
                subtypes = await SymbolFinder.FindImplementationsAsync(
                    type,
                    ws.CurrentSolution,
                    projects: null,
                    ct).ConfigureAwait(false);
            }
            else if (type.TypeKind == TypeKind.Class)
            {
                subtypes = await SymbolFinder.FindDerivedClassesAsync(
                    type,
                    ws.CurrentSolution,
                    projects: null,
                    ct).ConfigureAwait(false);
            }
            else
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        var results = new List<TypeHierarchyItem>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var subtype in subtypes)
        {
            var normalized = NormalizeType(subtype);
            if (normalized is null || !seen.Add(normalized))
            {
                continue;
            }

            var item = CreateItem(normalized);
            if (item is not null)
            {
                results.Add(item);
            }
        }

        return results.Count > 0 ? results.ToArray() : null;
    }

    private static async Task<INamedTypeSymbol?> ResolveItemAsync(
        UnityWorkspaceManager ws,
        TypeHierarchyItem item,
        CancellationToken ct)
    {
        if (item.Uri is not null && item.SelectionRange?.Start is not null)
        {
            var (symbol, _) = await ws.GetSymbolAtPositionAsync(
                item.Uri.LocalPath,
                item.SelectionRange.Start.Line,
                item.SelectionRange.Start.Character,
                ct).ConfigureAwait(false);

            var type = NormalizeType(symbol);
            if (type is not null)
            {
                return type;
            }
        }

        var id = item.Data?.Id;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        foreach (var project in ws.CurrentSolution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation);
            var type = NormalizeType(symbol);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }

    private static INamedTypeSymbol? NormalizeType(ISymbol? symbol)
    {
        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        return symbol is INamedTypeSymbol type ? type.OriginalDefinition : null;
    }

    private static TypeHierarchyItem? CreateItem(INamedTypeSymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(
            l => l.IsInSource && l.SourceTree is not null && !string.IsNullOrEmpty(l.SourceTree.FilePath));

        if (location?.SourceTree is null)
        {
            return null;
        }

        var tree = location.SourceTree;
        var selectionRange = LspSymbolMapper.ToRange(location);
        var fullRange = selectionRange;

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (!string.Equals(reference.SyntaxTree.FilePath, tree.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var declaration = reference.GetSyntax();
            fullRange = LspSymbolMapper.ToRange(declaration.GetLocation().GetLineSpan());
            break;
        }

        return new TypeHierarchyItem
        {
            Name = LspSymbolMapper.GetDisplayName(symbol),
            Kind = LspSymbolMapper.ToSymbolKind(symbol),
            Detail = LspSymbolMapper.GetDetail(symbol),
            Uri = new Uri(Path.GetFullPath(tree.FilePath)),
            Range = fullRange,
            SelectionRange = selectionRange,
            Data = new TypeHierarchyData { Id = symbol.GetDocumentationCommentId() },
        };
    }
}
