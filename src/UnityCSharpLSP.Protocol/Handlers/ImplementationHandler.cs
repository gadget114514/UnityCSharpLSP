using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/implementation</c> requests to navigate to interface or virtual member implementations.
/// </summary>
public sealed class ImplementationHandler
{
    private readonly Func<UnityWorkspaceManager?> _workspaceManagerProvider;

    public ImplementationHandler(Func<UnityWorkspaceManager?> workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentImplementationName, UseSingleObjectParameterDeserialization = true)]
    public async Task<LspLocation[]?> GotoImplementation(TextDocumentPositionParams @params, CancellationToken ct = default)
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

        IEnumerable<ISymbol> implementations;
        try
        {
            implementations = await SymbolFinder.FindImplementationsAsync(
                symbol,
                ws.CurrentSolution,
                projects: null,
                ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var results = new List<LspLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var implementation in implementations)
        {
            foreach (var location in implementation.Locations)
            {
                var lspLocation = LspSymbolMapper.ToLocation(location);
                if (lspLocation is null)
                {
                    continue;
                }

                var key = $"{lspLocation.Uri.LocalPath}:{lspLocation.Range.Start.Line}:{lspLocation.Range.Start.Character}";
                if (seen.Add(key))
                {
                    results.Add(lspLocation);
                }
            }
        }

        return results.Count > 0 ? results.ToArray() : null;
    }
}
