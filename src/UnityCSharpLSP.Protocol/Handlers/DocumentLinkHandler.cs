using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.Workspace;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles document links for file paths found in comments and string literals.
/// </summary>
public sealed class DocumentLinkHandler
{
    private static readonly Regex PathPattern = new(
        @"(?:[A-Za-z]:\\?[/\\]|\\\\|\.\.?[/\\]|[A-Za-z0-9_.-]+[/\\])[^\s""'<>|*?\r\n]*?\.(?:cs|txt|md|json|asmdef|unity|prefab|asset|xml)\b",
        RegexOptions.Compiled);

    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public DocumentLinkHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentDocumentLinkName, UseSingleObjectParameterDeserialization = true)]
    public async Task<DocumentLink[]?> DocumentLink(DocumentLinkParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null)
        {
            return null;
        }

        var filePath = @params.TextDocument.Uri.LocalPath;
        var document = ws.GetDocument(filePath);
        if (document is null)
        {
            return UnityAssetFileHelper.IsYamlAssetFile(filePath)
                ? await GetYamlAssetLinksAsync(ws, filePath, ct).ConfigureAwait(false)
                : null;
        }

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var documentDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var links = new List<DocumentLink>();
        var seen = new HashSet<int>();
        BuildSceneDto[]? buildScenes = null;

        void AddLinks(string value, int startOffset)
        {
            foreach (Match match in PathPattern.Matches(value))
            {
                var candidate = match.Value;
                string fullPath;
                try
                {
                    fullPath = Path.IsPathRooted(candidate)
                        ? Path.GetFullPath(candidate)
                        : Path.GetFullPath(Path.Combine(documentDirectory, candidate));
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var offset = startOffset + match.Index;
                if (!seen.Add(offset))
                {
                    continue;
                }

                links.Add(new DocumentLink
                {
                    Range = LspTextMapper.ToRange(text, new Microsoft.CodeAnalysis.Text.TextSpan(offset, match.Length)),
                    Target = new Uri(fullPath),
                });
            }
        }

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                AddLinks(trivia.ToString(), trivia.SpanStart);
            }
        }

        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.StringLiteralToken))
            {
                AddLinks(token.Text, token.SpanStart);

                if (SceneManagerCallHelper.TryGetSceneNameArgumentLiteral(token, semanticModel, ct, out var sceneName))
                {
                    buildScenes ??= await ws.AssetService.GetBuildScenesAsync(ct).ConfigureAwait(false);
                    var matchedScene = SceneManagerCallHelper.FindMatchingScene(buildScenes, sceneName);
                    if (matchedScene is not null &&
                        !string.IsNullOrWhiteSpace(matchedScene.FullPath) &&
                        File.Exists(matchedScene.FullPath))
                    {
                        var contentSpan = SceneManagerCallHelper.GetStringLiteralContentSpan(token);
                        if (seen.Add(contentSpan.Start))
                        {
                            links.Add(new DocumentLink
                            {
                                Range = LspTextMapper.ToRange(text, contentSpan),
                                Target = new Uri(Path.GetFullPath(matchedScene.FullPath)),
                            });
                        }
                    }
                }
            }
        }

        return links.Count > 0 ? links.ToArray() : null;
    }

    private static async Task<DocumentLink[]?> GetYamlAssetLinksAsync(
        UnityWorkspaceManager ws,
        string filePath,
        CancellationToken ct)
    {
        var assetLinks = await ws.AssetService.GetDocumentLinksAsync(filePath, ct).ConfigureAwait(false);
        if (assetLinks.Links.Length == 0)
        {
            return null;
        }

        var results = new List<DocumentLink>(assetLinks.Links.Length);
        foreach (var item in assetLinks.Links)
        {
            if (string.IsNullOrWhiteSpace(item.TargetFullPath))
            {
                continue;
            }

            results.Add(new DocumentLink
            {
                Range = new LspRange
                {
                    Start = new Position(Math.Max(0, item.Line), Math.Max(0, item.StartCharacter)),
                    End = new Position(Math.Max(0, item.Line), Math.Max(0, item.EndCharacter)),
                },
                Target = new Uri(Path.GetFullPath(item.TargetFullPath)),
            });
        }

        return results.Count > 0 ? results.ToArray() : null;
    }
}
