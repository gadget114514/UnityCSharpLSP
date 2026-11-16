using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles call hierarchy requests (LSP 3.16): prepare, incoming calls, and outgoing calls.
/// </summary>
public sealed class CallHierarchyHandler
{
    private readonly Func<UnityWorkspaceManager?> _workspaceManagerProvider;

    public CallHierarchyHandler(Func<UnityWorkspaceManager?> workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(CallHierarchyMethods.PrepareName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CallHierarchyItem[]?> PrepareCallHierarchy(
        TextDocumentPositionParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var (symbol, _) = await ws.GetSymbolAtPositionAsync(
            @params.TextDocument.Uri.LocalPath,
            @params.Position.Line,
            @params.Position.Character,
            ct).ConfigureAwait(false);

        symbol = NormalizeCallable(symbol);
        if (symbol is null)
        {
            return null;
        }

        var item = CreateItem(symbol);
        return item is null ? null : new[] { item };
    }

    [JsonRpcMethod(CallHierarchyMethods.IncomingCallsName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CallHierarchyIncomingCall[]?> IncomingCalls(
        CallHierarchyIncomingCallsParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null || @params?.Item is null)
        {
            return null;
        }

        var symbol = await ResolveItemAsync(ws, @params.Item, ct).ConfigureAwait(false);
        if (symbol is null)
        {
            return null;
        }

        IEnumerable<SymbolCallerInfo> callers;
        try
        {
            callers = await SymbolFinder.FindCallersAsync(
                symbol,
                ws.CurrentSolution,
                ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var results = new List<CallHierarchyIncomingCall>();

        foreach (var caller in callers)
        {
            var callerSymbol = NormalizeCallable(caller.CallingSymbol);
            if (callerSymbol is null)
            {
                continue;
            }

            var from = CreateItem(callerSymbol);
            if (from is null)
            {
                continue;
            }

            var ranges = caller.Locations
                .Where(location => location.IsInSource)
                .Select(LspSymbolMapper.ToRange)
                .ToArray();

            if (ranges.Length == 0)
            {
                continue;
            }

            results.Add(new CallHierarchyIncomingCall
            {
                From = from,
                FromRanges = ranges,
            });
        }

        return results.Count > 0 ? results.ToArray() : null;
    }

    [JsonRpcMethod(CallHierarchyMethods.OutgoingCallsName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CallHierarchyOutgoingCall[]?> OutgoingCalls(
        CallHierarchyOutgoingCallsParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null || @params?.Item is null)
        {
            return null;
        }

        var symbol = await ResolveItemAsync(ws, @params.Item, ct).ConfigureAwait(false);
        if (symbol is null)
        {
            return null;
        }

        var calls = new Dictionary<ISymbol, List<LspRange>>(SymbolEqualityComparer.Default);

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var filePath = reference.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath))
            {
                continue;
            }

            var document = ws.GetDocument(filePath);
            if (document is null)
            {
                continue;
            }

            var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (semanticModel is null)
            {
                continue;
            }

            var declaration = await reference.GetSyntaxAsync(ct).ConfigureAwait(false);

            foreach (var node in declaration.DescendantNodesAndSelf())
            {
                if (node is not InvocationExpressionSyntax &&
                    node is not BaseObjectCreationExpressionSyntax)
                {
                    continue;
                }

                var target = NormalizeCallable(semanticModel.GetSymbolInfo(node, ct).Symbol);
                if (target is null)
                {
                    continue;
                }

                if (!target.Locations.Any(location => location.IsInSource))
                {
                    continue;
                }

                if (!calls.TryGetValue(target, out var ranges))
                {
                    ranges = new List<LspRange>();
                    calls[target] = ranges;
                }

                ranges.Add(LspSymbolMapper.ToRange(node.GetLocation().GetLineSpan()));
            }
        }

        var results = new List<CallHierarchyOutgoingCall>();

        foreach (var (target, ranges) in calls)
        {
            var to = CreateItem(target);
            if (to is null)
            {
                continue;
            }

            results.Add(new CallHierarchyOutgoingCall
            {
                To = to,
                FromRanges = ranges.ToArray(),
            });
        }

        return results.Count > 0 ? results.ToArray() : null;
    }

    private static async Task<ISymbol?> ResolveItemAsync(
        UnityWorkspaceManager ws,
        CallHierarchyItem item,
        CancellationToken ct)
    {
        if (item.Uri is not null && item.SelectionRange?.Start is not null)
        {
            var (symbol, _) = await ws.GetSymbolAtPositionAsync(
                item.Uri.LocalPath,
                item.SelectionRange.Start.Line,
                item.SelectionRange.Start.Character,
                ct).ConfigureAwait(false);

            symbol = NormalizeCallable(symbol);
            if (symbol is not null)
            {
                return symbol;
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
            if (symbol is not null)
            {
                return NormalizeCallable(symbol);
            }
        }

        return null;
    }

    private static ISymbol? NormalizeCallable(ISymbol? symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            if (method.MethodKind is MethodKind.PropertyGet or
                MethodKind.PropertySet or
                MethodKind.EventAdd or
                MethodKind.EventRemove or
                MethodKind.EventRaise)
            {
                return method.AssociatedSymbol;
            }

            return method;
        }

        if (symbol is IPropertySymbol or IEventSymbol)
        {
            return symbol;
        }

        return null;
    }

    private static CallHierarchyItem? CreateItem(ISymbol symbol)
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

        return new CallHierarchyItem
        {
            Name = LspSymbolMapper.GetDisplayName(symbol),
            Kind = LspSymbolMapper.ToSymbolKind(symbol),
            Detail = LspSymbolMapper.GetDetail(symbol),
            Uri = new Uri(Path.GetFullPath(tree.FilePath)),
            Range = fullRange,
            SelectionRange = selectionRange,
            Data = new CallHierarchyData { Id = symbol.GetDocumentationCommentId() },
        };
    }
}
