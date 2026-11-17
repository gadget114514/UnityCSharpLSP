using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles the custom <c>textDocument/declaration</c> request.
/// </summary>
public sealed class DeclarationHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public DeclarationHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(TextDocumentMethods.DeclarationName, UseSingleObjectParameterDeserialization = true)]
    public async Task<LspLocation[]?> GotoDeclaration(
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

        if (symbol is null)
        {
            return null;
        }

        var results = new List<LspLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[] { symbol, symbol.OriginalDefinition })
        {
            foreach (var location in candidate.Locations)
            {
                var lspLocation = LspSymbolMapper.ToLocation(location);
                if (lspLocation is null)
                {
                    continue;
                }

                var range = lspLocation.Range;
                var key = $"{lspLocation.Uri.LocalPath}:{range.Start.Line}:{range.Start.Character}:" +
                          $"{range.End.Line}:{range.End.Character}";
                if (seen.Add(key))
                {
                    results.Add(lspLocation);
                }
            }
        }

        return results.Count > 0 ? results.ToArray() : null;
    }
}
