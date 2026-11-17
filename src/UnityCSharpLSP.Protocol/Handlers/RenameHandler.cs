using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/prepareRename</c> and <c>textDocument/rename</c>.
/// </summary>
public sealed class RenameHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public RenameHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(RenameMethods.PrepareName, UseSingleObjectParameterDeserialization = true)]
    public async Task<PrepareRenameResult?> PrepareRename(
        TextDocumentPositionParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var document = ws.GetDocument(@params.TextDocument.Uri.LocalPath);
        if (document is null)
        {
            return null;
        }

        var target = await ResolveRenameTargetAsync(
            document,
            @params.Position.Line,
            @params.Position.Character,
            ct).ConfigureAwait(false);

        if (target is null)
        {
            return null;
        }

        return new PrepareRenameResult
        {
            Range = target.Range,
            Placeholder = target.Placeholder,
        };
    }

    [JsonRpcMethod(Methods.TextDocumentRenameName, UseSingleObjectParameterDeserialization = true)]
    public async Task<WorkspaceEdit?> Rename(
        RenameParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null ||
            string.IsNullOrWhiteSpace(@params.NewName))
        {
            return null;
        }

        var solution = ws.CurrentSolution;
        var document = ws.GetDocument(@params.TextDocument.Uri.LocalPath);
        if (document is null)
        {
            return null;
        }

        var target = await ResolveRenameTargetAsync(
            document,
            @params.Position.Line,
            @params.Position.Character,
            ct).ConfigureAwait(false);

        if (target is null)
        {
            return null;
        }

        Solution renamedSolution;
        try
        {
            renamedSolution = await Renamer.RenameSymbolAsync(
                solution,
                target.Symbol,
                new SymbolRenameOptions(),
                @params.NewName,
                ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return await CreateWorkspaceEditAsync(solution, renamedSolution, ct).ConfigureAwait(false);
    }

    private static async Task<WorkspaceEdit> CreateWorkspaceEditAsync(
        Solution oldSolution,
        Solution newSolution,
        CancellationToken ct)
    {
        var changes = new Dictionary<string, TextEdit[]>();

        foreach (var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                var oldDocument = oldSolution.GetDocument(documentId);
                var newDocument = newSolution.GetDocument(documentId);
                if (oldDocument is null || newDocument is null || string.IsNullOrEmpty(oldDocument.FilePath))
                {
                    continue;
                }

                var oldText = await oldDocument.GetTextAsync(ct).ConfigureAwait(false);
                var newText = await newDocument.GetTextAsync(ct).ConfigureAwait(false);

                // SourceText.GetTextChanges merges all differences between the first and last
                // changed line into one edit. Prefer the precise syntax diff to keep edits minimal.
                IEnumerable<TextChange> textChanges;
                var oldTree = await oldDocument.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
                var newTree = await newDocument.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
                if (oldTree is not null && newTree is not null)
                {
                    try
                    {
                        textChanges = newTree.GetChanges(oldTree);
                    }
                    catch (ArgumentException)
                    {
                        textChanges = newText.GetTextChanges(oldText);
                    }
                }
                else
                {
                    textChanges = newText.GetTextChanges(oldText);
                }

                var edits = textChanges
                    .Select(change => new TextEdit
                    {
                        Range = ToRange(oldText, change.Span),
                        NewText = change.NewText ?? string.Empty,
                    })
                    .ToArray();

                if (edits.Length > 0)
                {
                    changes[new Uri(Path.GetFullPath(oldDocument.FilePath)).AbsoluteUri] = edits;
                }
            }
        }

        return new WorkspaceEdit { Changes = changes };
    }

    private static async Task<RenameTarget?> ResolveRenameTargetAsync(
        Document document,
        int line,
        int character,
        CancellationToken ct)
    {
        var sourceText = await document.GetTextAsync(ct).ConfigureAwait(false);
        if (line < 0 || line >= sourceText.Lines.Count || character < 0)
        {
            return null;
        }

        var textLine = sourceText.Lines[line];
        var position = Math.Min(textLine.Start + character, textLine.End);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || semanticModel is null || root.FullSpan.IsEmpty)
        {
            return null;
        }

        var token = FindRenameToken(root, position);
        if (!token.IsKind(SyntaxKind.IdentifierToken) || token.Parent is null)
        {
            return null;
        }

        if (token.Parent is IdentifierNameSyntax { IsVar: true })
        {
            return null;
        }

        var symbol = ResolveSymbol(semanticModel, token.Parent, ct);
        symbol = NormalizeRenameableSymbol(symbol);
        if (symbol is null)
        {
            return null;
        }

        return new RenameTarget(
            symbol,
            ToRange(sourceText, token.Span),
            token.ValueText);
    }

    private static SyntaxToken FindRenameToken(SyntaxNode root, int position)
    {
        var token = root.FindToken(position);
        if (token.IsKind(SyntaxKind.IdentifierToken))
        {
            return token;
        }

        if (position == token.SpanStart)
        {
            var previous = token.GetPreviousToken();
            if (previous.IsKind(SyntaxKind.IdentifierToken) && previous.Span.End == position)
            {
                return previous;
            }
        }

        return token;
    }

    private static ISymbol? ResolveSymbol(
        SemanticModel semanticModel,
        SyntaxNode node,
        CancellationToken ct)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            if (current is ArgumentSyntax or AttributeArgumentSyntax or ArgumentListSyntax or AttributeArgumentListSyntax)
            {
                return null;
            }

            var symbolInfo = semanticModel.GetSymbolInfo(current, ct);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            symbol ??= semanticModel.GetDeclaredSymbol(current, ct);
            if (symbol is not null)
            {
                return symbol;
            }

            if (current is StatementSyntax or MemberDeclarationSyntax or CompilationUnitSyntax)
            {
                break;
            }
        }

        return null;
    }

    private static ISymbol? NormalizeRenameableSymbol(ISymbol? symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor)
            {
                symbol = method.ContainingType;
            }
            else if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or
                     MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise)
            {
                symbol = method.AssociatedSymbol;
            }
        }

        if (symbol is null or IAliasSymbol ||
            symbol.IsImplicitlyDeclared ||
            string.IsNullOrEmpty(symbol.Name) ||
            symbol is INamespaceSymbol { IsGlobalNamespace: true } ||
            !symbol.Locations.Any(location => location.IsInSource))
        {
            return null;
        }

        return symbol;
    }

    private static LspRange ToRange(SourceText text, TextSpan span)
    {
        var lineSpan = text.Lines.GetLinePositionSpan(span);
        return new LspRange
        {
            Start = new Position(lineSpan.Start.Line, lineSpan.Start.Character),
            End = new Position(lineSpan.End.Line, lineSpan.End.Character),
        };
    }

    private sealed record RenameTarget(ISymbol Symbol, LspRange Range, string Placeholder);
}
