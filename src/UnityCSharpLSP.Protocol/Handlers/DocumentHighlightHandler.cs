using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/documentHighlight</c> requests for declarations and same-file references.
/// </summary>
public sealed class DocumentHighlightHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public DocumentHighlightHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentDocumentHighlightName, UseSingleObjectParameterDeserialization = true)]
    public async Task<DocumentHighlight[]?> GetDocumentHighlights(
        DocumentHighlightParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var filePath = @params.TextDocument.Uri.LocalPath;
        var (symbol, _) = await ws.GetSymbolAtPositionAsync(
            filePath,
            @params.Position.Line,
            @params.Position.Character,
            ct).ConfigureAwait(false);

        if (symbol is null)
        {
            return null;
        }

        var results = new List<DocumentHighlight>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedFilePath = Path.GetFullPath(filePath);

        void AddHighlight(LspLocation? location)
        {
            if (location is null ||
                !PathsEqual(location.Uri.LocalPath, normalizedFilePath))
            {
                return;
            }

            var range = location.Range;
            var key = $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";
            if (!seen.Add(key))
            {
                return;
            }

            results.Add(new DocumentHighlight
            {
                Range = range,
                Kind = DocumentHighlightKind.Text,
            });
        }

        foreach (var location in symbol.Locations)
        {
            AddHighlight(LspSymbolMapper.ToLocation(location));
        }

        var referencedSymbols = await SymbolFinder.FindReferencesAsync(
            symbol,
            ws.CurrentSolution,
            ct).ConfigureAwait(false);

        foreach (var referencedSymbol in referencedSymbols)
        {
            foreach (var reference in referencedSymbol.Locations)
            {
                if (string.IsNullOrEmpty(reference.Document.FilePath) ||
                    !PathsEqual(reference.Document.FilePath, normalizedFilePath))
                {
                    continue;
                }

                AddHighlight(LspSymbolMapper.ToLocation(reference.Location));
            }
        }

        return results.Count > 0 ? results.ToArray() : null;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
