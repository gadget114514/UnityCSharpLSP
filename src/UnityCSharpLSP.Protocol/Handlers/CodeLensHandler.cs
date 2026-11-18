using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;
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

        var candidates = new List<(SyntaxToken Identifier, ISymbol Symbol, string? ScriptGuid)>();
        var scriptGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            string? scriptGuid = null;
            if (node is BaseTypeDeclarationSyntax &&
                symbol is INamedTypeSymbol namedType &&
                UnitySymbolGuidHelper.IsUnityObjectType(namedType) &&
                UnitySymbolGuidHelper.TryGetScriptGuid(namedType, out var resolvedGuid))
            {
                scriptGuid = resolvedGuid;
                scriptGuids.Add(resolvedGuid);
            }

            candidates.Add((identifier, symbol, scriptGuid));
        }

        Dictionary<string, ScriptReferenceCountDto>? assetCountsByGuid = null;
        if (scriptGuids.Count > 0)
        {
            var counts = await ws.AssetService
                .GetScriptReferenceCountsAsync(scriptGuids.ToArray(), ct)
                .ConfigureAwait(false);
            assetCountsByGuid = new Dictionary<string, ScriptReferenceCountDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var count in counts)
            {
                if (!string.IsNullOrEmpty(count.ScriptGuid))
                {
                    assetCountsByGuid[count.ScriptGuid] = count;
                }
            }
        }

        var lenses = new List<CodeLens>(candidates.Count);

        foreach (var (identifier, symbol, scriptGuid) in candidates)
        {
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
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in references)
            {
                foreach (var location in reference.Locations)
                {
                    LspSymbolMapper.AddUniqueLocation(locations, seen, LspSymbolMapper.ToLocation(location.Location));
                }
            }

            string title;
            if (scriptGuid is not null &&
                assetCountsByGuid is not null &&
                assetCountsByGuid.TryGetValue(scriptGuid, out var assetCount) &&
                assetCount.TotalAssetCount > 0)
            {
                var assetRefs = await ws.AssetService.FindReferencesAsync(
                    new AssetReferenceQueryDto
                    {
                        ScriptGuid = scriptGuid,
                        MemberKind = AssetMemberKind.Class,
                        IncludeTransitiveScenes = true,
                    },
                    ct).ConfigureAwait(false);

                foreach (var assetLoc in assetRefs.Locations)
                {
                    LspSymbolMapper.AddUniqueLocation(locations, seen, LspSymbolMapper.ToLocation(assetLoc));
                }

                var prefabCount = assetCount.DirectPrefabCount + assetCount.TransitivePrefabCount;
                var sceneCount = assetCount.SceneCount;
                title = $"{locations.Count} references ({prefabCount} prefabs, {sceneCount} scenes)";
            }
            else
            {
                title = $"{locations.Count} references";
            }

            var linePosition = text.Lines.GetLinePosition(identifier.SpanStart);
            lenses.Add(new CodeLens
            {
                Range = LspTextMapper.ToRange(text, identifier.Span),
                Command = new Command
                {
                    Title = title,
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
