using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/typeDefinition</c> requests.
/// </summary>
public sealed class TypeDefinitionHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public TypeDefinitionHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentTypeDefinitionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<LspLocation[]?> GotoTypeDefinition(
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

        var type = GetSymbolType(symbol);
        if (type is null)
        {
            return null;
        }

        var results = new List<LspLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in type.OriginalDefinition.Locations)
        {
            var lspLocation = LspSymbolMapper.ToLocation(location);
            if (lspLocation is null)
            {
                continue;
            }

            var key = GetLocationKey(lspLocation);
            if (seen.Add(key))
            {
                results.Add(lspLocation);
            }
        }

        return results.Count > 0 ? results.ToArray() : null;
    }

    private static ITypeSymbol? GetSymbolType(ISymbol? symbol)
    {
        return symbol switch
        {
            IAliasSymbol alias => alias.Target as ITypeSymbol,
            IMethodSymbol method => method.ReturnType,
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            IEventSymbol @event => @event.Type,
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            ITypeSymbol type => type,
            _ => null,
        };
    }

    private static string GetLocationKey(LspLocation location)
    {
        return $"{location.Uri.LocalPath}:{location.Range.Start.Line}:{location.Range.Start.Character}:" +
               $"{location.Range.End.Line}:{location.Range.End.Character}";
    }
}
