using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles reference-count code lenses for type and method declarations.
/// </summary>
public sealed class CodeLensHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public CodeLensHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentCodeLensName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CodeLens[]?> CodeLens(CodeLensParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null)
        {
            return null;
        }

        var document = ws.GetDocument(@params.TextDocument.Uri.LocalPath);
        if (document is null)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return null;
        }

        var lenses = new List<CodeLens>();

        foreach (var node in root.DescendantNodes())
        {
            SyntaxToken identifier;
            switch (node)
            {
                case BaseTypeDeclarationSyntax typeDeclaration:
                    identifier = typeDeclaration.Identifier;
                    break;
                case MethodDeclarationSyntax methodDeclaration:
                    identifier = methodDeclaration.Identifier;
                    break;
                default:
                    continue;
            }

            if (identifier.IsMissing || identifier.Span.IsEmpty)
            {
                continue;
            }

            var symbol = semanticModel.GetDeclaredSymbol(node, ct);
            if (symbol is null)
            {
                continue;
            }

            IReadOnlyList<ReferencedSymbol> references;
            try
            {
                references = (await SymbolFinder.FindReferencesAsync(symbol, ws.CurrentSolution, ct)
                    .ConfigureAwait(false)).ToList();
            }
            catch (ArgumentException)
            {
                references = [];
            }

            var locations = new List<LspLocation>();
            foreach (var reference in references)
            {
                foreach (var location in reference.Locations)
                {
                    var lspLocation = LspSymbolMapper.ToLocation(location.Location);
                    if (lspLocation is not null)
                    {
                        locations.Add(lspLocation);
                    }
                }
            }

            var linePosition = text.Lines.GetLinePosition(identifier.SpanStart);
            lenses.Add(new CodeLens
            {
                Range = LspTextMapper.ToRange(text, identifier.Span),
                Command = new Command
                {
                    Title = $"{locations.Count} references",
                    CommandIdentifier = "editor.action.showReferences",
                    Arguments =
                    [
                        new Uri(Path.GetFullPath(document.FilePath!)),
                        new Position(linePosition.Line, linePosition.Character),
                        locations.ToArray(),
                    ],
                },
            });
        }

        return lenses.Count > 0 ? lenses.ToArray() : null;
    }

    [JsonRpcMethod(Methods.CodeLensResolveName, UseSingleObjectParameterDeserialization = true)]
    public Task<CodeLens> Resolve(CodeLens lens, CancellationToken ct = default)
    {
        // The command is populated eagerly; resolution is a no-op.
        return Task.FromResult(lens);
    }
}
