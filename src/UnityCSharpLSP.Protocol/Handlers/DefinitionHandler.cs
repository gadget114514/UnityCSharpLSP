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
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public DefinitionHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentDefinitionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<LspLocation[]?> GotoDefinition(TextDocumentPositionParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var filePath = @params.TextDocument.Uri.LocalPath;
        if (ws.GetDocument(filePath) is null && UnityAssetFileHelper.IsYamlAssetFile(filePath))
        {
            return await GotoYamlAssetDefinitionAsync(
                ws,
                filePath,
                @params.Position.Line,
                @params.Position.Character,
                ct).ConfigureAwait(false);
        }

        var (symbol, _) = await ws.GetSymbolAtPositionAsync(
            filePath,
            @params.Position.Line,
            @params.Position.Character,
            ct).ConfigureAwait(false);

        if (symbol is null)
        {
            return await GotoSceneLoadDefinitionAsync(
                ws,
                filePath,
                @params.Position,
                ct).ConfigureAwait(false);
        }

        var results = ToSourceLocations(symbol);
        return results.Length > 0 ? results : null;
    }

    private static async Task<LspLocation[]?> GotoSceneLoadDefinitionAsync(
        UnityWorkspaceManager ws,
        string filePath,
        Position position,
        CancellationToken ct)
    {
        var doc = ws.GetDocument(filePath);
        if (doc is null)
        {
            return null;
        }

        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        var semanticModel = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var offset = LspTextMapper.ToOffset(text, position);
        if (!SceneManagerCallHelper.TryGetSceneNameDefinitionLiteral(
                root,
                semanticModel,
                offset,
                ct,
                out _,
                out var sceneName))
        {
            return null;
        }

        var buildScenes = await ws.AssetService.GetBuildScenesAsync(ct).ConfigureAwait(false);
        var matchedScene = SceneManagerCallHelper.FindMatchingScene(buildScenes, sceneName);
        if (matchedScene is null || string.IsNullOrWhiteSpace(matchedScene.FullPath))
        {
            return null;
        }

        var origin = new Position(0, 0);
        return
        [
            new LspLocation
            {
                Uri = new Uri(Path.GetFullPath(matchedScene.FullPath)),
                Range = new LspRange { Start = origin, End = origin },
            },
        ];
    }

    private static async Task<LspLocation[]?> GotoYamlAssetDefinitionAsync(
        UnityWorkspaceManager ws,
        string filePath,
        int line,
        int character,
        CancellationToken ct)
    {
        var hover = await ws.AssetService.GetHoverAsync(filePath, line, character, ct).ConfigureAwait(false);
        if (hover is not null &&
            !string.IsNullOrWhiteSpace(hover.ScriptGuid) &&
            !string.IsNullOrWhiteSpace(hover.FieldName))
        {
            var (_, fieldSymbol) = await UnityAssetFileHelper.ResolveScriptSymbolAsync(
                ws,
                hover.ScriptGuid,
                hover.TargetFullPath,
                hover.FieldName,
                ct).ConfigureAwait(false);

            if (fieldSymbol is not null)
            {
                var fieldLocations = ToSourceLocations(fieldSymbol);
                if (fieldLocations.Length > 0)
                {
                    return fieldLocations;
                }
            }
        }

        var assetLoc = await ws.AssetService.GetDefinitionAsync(filePath, line, character, ct).ConfigureAwait(false);
        if (assetLoc is null || string.IsNullOrWhiteSpace(assetLoc.FullPath))
        {
            return null;
        }

        var position = new Position(Math.Max(0, assetLoc.Line), Math.Max(0, assetLoc.Character));
        return
        [
            new LspLocation
            {
                Uri = new Uri(Path.GetFullPath(assetLoc.FullPath)),
                Range = new LspRange { Start = position, End = position },
            },
        ];
    }

    private static LspLocation[] ToSourceLocations(ISymbol symbol)
    {
        var results = new List<LspLocation>();

        foreach (var location in symbol.Locations)
        {
            var lspLocation = LspSymbolMapper.ToLocation(location);
            if (lspLocation is not null)
            {
                results.Add(lspLocation);
            }
        }

        return results.ToArray();
    }
}
