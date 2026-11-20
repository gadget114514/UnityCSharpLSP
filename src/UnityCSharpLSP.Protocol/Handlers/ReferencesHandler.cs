using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Parsing;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/references</c> requests to locate all references to a symbol across the solution.
/// </summary>
public sealed class ReferencesHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public ReferencesHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentReferencesName, UseSingleObjectParameterDeserialization = true)]
    public async Task<LspLocation[]?> FindReferences(UnityReferenceParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        var textDocument = @params.TextDocument;
        var position = @params.Position;
        if (ws is null || textDocument?.Uri is null || position is null)
        {
            return null;
        }

        var filePath = textDocument.Uri.LocalPath;
        var (symbol, _) = await ws.GetSymbolAtPositionAsync(
            filePath,
            position.Line,
            position.Character,
            ct).ConfigureAwait(false);

        if (symbol is null)
        {
            return null;
        }

        var solution = ws.CurrentSolution;
        var referencedSymbols = await SymbolFinder.FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false);

        var results = new List<LspLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include declaration if requested
        if (@params.Context?.IncludeDeclaration == true)
        {
            foreach (var loc in symbol.Locations)
            {
                if (!loc.IsInSource || loc.SourceTree is null || string.IsNullOrEmpty(loc.SourceTree.FilePath))
                {
                    continue;
                }

                var lineSpan = loc.GetLineSpan();
                var key = $"{loc.SourceTree.FilePath}:{lineSpan.StartLinePosition.Line}:{lineSpan.StartLinePosition.Character}";
                if (seen.Add(key))
                {
                    results.Add(new LspLocation
                    {
                        Uri = new Uri(Path.GetFullPath(loc.SourceTree.FilePath)),
                        Range = new LspRange
                        {
                            Start = new Position(lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
                            End = new Position(lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character),
                        },
                    });
                }
            }
        }

        // Add all reference locations
        foreach (var refSymbol in referencedSymbols)
        {
            foreach (var refLocation in refSymbol.Locations)
            {
                var doc = refLocation.Document;
                if (string.IsNullOrEmpty(doc.FilePath))
                {
                    continue;
                }

                var lineSpan = refLocation.Location.GetLineSpan();
                var key = $"{doc.FilePath}:{lineSpan.StartLinePosition.Line}:{lineSpan.StartLinePosition.Character}";
                if (seen.Add(key))
                {
                    results.Add(new LspLocation
                    {
                        Uri = new Uri(Path.GetFullPath(doc.FilePath)),
                        Range = new LspRange
                        {
                            Start = new Position(lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
                            End = new Position(lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character),
                        },
                    });
                }
            }
        }

        // Add Unity asset (Prefab / Scene) references when applicable
        var assetQuery = CreateAssetReferenceQuery(symbol);
        if (assetQuery is not null)
        {
            assetQuery.IncludePatterns = @params.IncludePatterns;
            assetQuery.ExcludePatterns = @params.ExcludePatterns;
            var assetRefs = await ws.AssetService.FindReferencesAsync(assetQuery, ct).ConfigureAwait(false);
            foreach (var loc in assetRefs.Locations)
            {
                LspSymbolMapper.AddUniqueLocation(results, seen, LspSymbolMapper.ToLocation(loc));
            }
        }

        var filtered = results
            .Where(location => GlobPatternMatcher.MatchesAny(location.Uri.LocalPath, @params.IncludePatterns) &&
                               !(@params.ExcludePatterns is { Length: > 0 } &&
                                  GlobPatternMatcher.MatchesAny(location.Uri.LocalPath, @params.ExcludePatterns)))
            .ToArray();

        return filtered.Length > 0 ? filtered : null;
    }

    private static AssetReferenceQueryDto? CreateAssetReferenceQuery(ISymbol symbol)
    {
        if (!UnitySymbolGuidHelper.TryGetScriptGuid(symbol, out var scriptGuid))
        {
            return null;
        }

        return symbol switch
        {
            INamedTypeSymbol namedType when UnitySymbolGuidHelper.IsUnityObjectType(namedType) =>
                new AssetReferenceQueryDto
                {
                    ScriptGuid = scriptGuid,
                    MemberKind = AssetMemberKind.Class,
                    IncludeTransitiveScenes = true,
                },
            IFieldSymbol field when UnitySymbolGuidHelper.IsUnityObjectType(field.ContainingType) &&
                                    UnitySymbolGuidHelper.IsSerializableField(field) =>
                new AssetReferenceQueryDto
                {
                    ScriptGuid = scriptGuid,
                    MemberKind = AssetMemberKind.Field,
                    MemberName = field.Name,
                },
            IMethodSymbol method when method.MethodKind == MethodKind.Ordinary &&
                                      UnitySymbolGuidHelper.IsUnityObjectType(method.ContainingType) =>
                new AssetReferenceQueryDto
                {
                    ScriptGuid = scriptGuid,
                    MemberKind = AssetMemberKind.Method,
                    MemberName = method.Name,
                },
            _ => null,
        };
    }
}
