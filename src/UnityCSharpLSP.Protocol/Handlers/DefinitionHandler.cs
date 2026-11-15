using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/definition</c> requests to navigate to symbol declarations.
/// </summary>
public sealed class DefinitionHandler
{
    private readonly Func<UnityWorkspaceManager?> _workspaceManagerProvider;

    public DefinitionHandler(Func<UnityWorkspaceManager?> workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentDefinitionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<LspLocation[]?> GotoDefinition(TextDocumentPositionParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider();
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

        var results = new List<LspLocation>();

        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource || location.SourceTree is null)
            {
                continue;
            }

            var lineSpan = location.GetLineSpan();
            var targetPath = location.SourceTree.FilePath;
            if (string.IsNullOrEmpty(targetPath))
            {
                continue;
            }

            results.Add(new LspLocation
            {
                Uri = new Uri(Path.GetFullPath(targetPath)),
                Range = new LspRange
                {
                    Start = new Position(lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
                    End = new Position(lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character),
                },
            });
        }

        return results.Count > 0 ? results.ToArray() : null;
    }
}
