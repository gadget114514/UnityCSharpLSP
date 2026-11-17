using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
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
            return null;
        }

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        var documentDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var links = new List<DocumentLink>();
        var seen = new HashSet<int>();

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
            }
        }

        return links.Count > 0 ? links.ToArray() : null;
    }
}
