using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;
using LspSelectionRange = UnityCSharpLSP.Protocol.Models.SelectionRange;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/selectionRange</c> requests with syntax-node ancestry.
/// </summary>
public sealed class SelectionRangeHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public SelectionRangeHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(TextDocumentMethods.SelectionRangeName, UseSingleObjectParameterDeserialization = true)]
    public async Task<LspSelectionRange[]?> GetSelectionRanges(
        SelectionRangeParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null ||
            @params.TextDocument?.Uri is null ||
            @params.Positions is null)
        {
            return null;
        }

        var document = ws.GetDocument(@params.TextDocument.Uri.LocalPath);
        if (document is null)
        {
            return null;
        }

        var sourceText = await document.GetTextAsync(ct).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        var results = new List<LspSelectionRange>(@params.Positions.Length);
        foreach (var position in @params.Positions)
        {
            var offset = GetOffset(sourceText, position);
            var node = offset.HasValue
                ? root.FindNode(
                    new TextSpan(offset.Value, 0),
                    findInsideTrivia: true,
                    getInnermostNodeForTie: true)
                : root;

            var spans = new List<TextSpan>();
            for (var current = node; current is not null; current = current.Parent)
            {
                if (spans.Count == 0 || spans[^1] != current.Span)
                {
                    spans.Add(current.Span);
                }
            }

            // Structured trivia can have a separate root, so explicitly retain the compilation unit.
            if (spans.Count == 0 || spans[^1] != root.Span)
            {
                spans.Add(root.Span);
            }

            LspSelectionRange? parent = null;
            for (var i = spans.Count - 1; i >= 0; i--)
            {
                parent = new LspSelectionRange
                {
                    Range = ToRange(sourceText, spans[i]),
                    Parent = parent,
                };
            }

            if (parent is not null)
            {
                results.Add(parent);
            }
        }

        return results.ToArray();
    }

    private static int? GetOffset(SourceText sourceText, Position position)
    {
        if (position.Line < 0 || position.Line >= sourceText.Lines.Count || position.Character < 0)
        {
            return null;
        }

        var line = sourceText.Lines[position.Line];
        return line.Start + Math.Min(position.Character, line.End - line.Start);
    }

    private static LspRange ToRange(SourceText sourceText, TextSpan span)
    {
        var lineSpan = sourceText.Lines.GetLinePositionSpan(span);
        return new LspRange
        {
            Start = new Position(lineSpan.Start.Line, lineSpan.Start.Character),
            End = new Position(lineSpan.End.Line, lineSpan.End.Character),
        };
    }
}
