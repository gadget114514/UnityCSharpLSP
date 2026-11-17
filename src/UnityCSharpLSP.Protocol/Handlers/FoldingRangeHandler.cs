using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/foldingRange</c> requests using C# syntax and directive trivia.
/// </summary>
public sealed class FoldingRangeHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public FoldingRangeHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentFoldingRangeName, UseSingleObjectParameterDeserialization = true)]
    public async Task<FoldingRange[]?> GetFoldingRanges(
        FoldingRangeParams @params,
        CancellationToken ct = default)
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
        if (root is null)
        {
            return null;
        }

        var collector = new FoldingRangeCollector();
        collector.AddSyntaxFolds(root);
        collector.AddCommentFolds(root);
        collector.AddDirectiveFolds(root);
        collector.AddUsingFolds(root);

        return collector.Count > 0 ? collector.ToArray() : null;
    }

    private sealed class FoldingRangeCollector
    {
        private readonly List<FoldingRange> _results = new();
        private readonly HashSet<(int StartLine, int EndLine, string Kind)> _seen = new();

        public int Count => _results.Count;

        public FoldingRange[] ToArray()
        {
            return _results.ToArray();
        }

        public void AddSyntaxFolds(SyntaxNode root)
        {
            foreach (var node in root.DescendantNodes())
            {
                if (node is BaseNamespaceDeclarationSyntax or
                    BaseTypeDeclarationSyntax or
                    BaseMethodDeclarationSyntax or
                    AccessorDeclarationSyntax or
                    BlockSyntax)
                {
                    var span = node.GetLocation().GetLineSpan();
                    AddRange(span.StartLinePosition.Line, span.EndLinePosition.Line);
                }
            }
        }

        public void AddCommentFolds(SyntaxNode root)
        {
            SyntaxTrivia firstLineComment = default;
            SyntaxTrivia previousLineComment = default;
            var lineCommentCount = 0;

            void FlushComments()
            {
                if (lineCommentCount > 1)
                {
                    var start = firstLineComment.GetLocation().GetLineSpan().StartLinePosition.Line;
                    var end = previousLineComment.GetLocation().GetLineSpan().EndLinePosition.Line;
                    AddRange(start, end, FoldingRangeKind.Comment);
                }

                firstLineComment = default;
                previousLineComment = default;
                lineCommentCount = 0;
            }

            foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
            {
                if (trivia.IsKind(SyntaxKind.WhitespaceTrivia) ||
                    trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    continue;
                }

                if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
                {
                    var span = trivia.GetLocation().GetLineSpan();
                    var startLine = span.StartLinePosition.Line;
                    if (lineCommentCount > 0)
                    {
                        var previousEndLine = previousLineComment.GetLocation()
                            .GetLineSpan().EndLinePosition.Line;
                        if (startLine > previousEndLine + 1)
                        {
                            FlushComments();
                        }
                    }

                    if (lineCommentCount == 0)
                    {
                        firstLineComment = trivia;
                    }

                    previousLineComment = trivia;
                    lineCommentCount++;
                    continue;
                }

                FlushComments();

                if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                    trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                    trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    var span = trivia.GetLocation().GetLineSpan();
                    AddRange(
                        span.StartLinePosition.Line,
                        span.EndLinePosition.Line,
                        FoldingRangeKind.Comment);
                }
            }

            FlushComments();
        }

        public void AddDirectiveFolds(SyntaxNode root)
        {
            var regions = new Stack<RegionDirectiveTriviaSyntax>();
            var conditionals = new Stack<IfDirectiveTriviaSyntax>();

            foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
            {
                var directive = trivia.GetStructure();
                switch (directive)
                {
                    case RegionDirectiveTriviaSyntax region:
                        regions.Push(region);
                        break;
                    case EndRegionDirectiveTriviaSyntax endRegion when regions.Count > 0:
                        AddDirectiveRange(regions.Pop(), endRegion);
                        break;
                    case IfDirectiveTriviaSyntax ifDirective:
                        conditionals.Push(ifDirective);
                        break;
                    case EndIfDirectiveTriviaSyntax endIf when conditionals.Count > 0:
                        AddDirectiveRange(conditionals.Pop(), endIf);
                        break;
                }
            }
        }

        public void AddUsingFolds(SyntaxNode root)
        {
            UsingDirectiveSyntax? first = null;
            UsingDirectiveSyntax? previous = null;

            void Flush()
            {
                if (first is not null && previous is not null && !ReferenceEquals(first, previous))
                {
                    var startLine = first.GetLocation().GetLineSpan().StartLinePosition.Line;
                    var endLine = previous.GetLocation().GetLineSpan().EndLinePosition.Line;
                    AddRange(startLine, endLine, FoldingRangeKind.Imports);
                }

                first = null;
                previous = null;
            }

            foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (previous is not null)
                {
                    var previousEndLine = previous.GetLocation().GetLineSpan().EndLinePosition.Line;
                    var startLine = usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line;
                    if (!ReferenceEquals(previous.Parent, usingDirective.Parent) ||
                        startLine > previousEndLine + 1)
                    {
                        Flush();
                    }
                }

                first ??= usingDirective;
                previous = usingDirective;
            }

            Flush();
        }

        private void AddDirectiveRange(DirectiveTriviaSyntax start, DirectiveTriviaSyntax end)
        {
            var startLine = start.GetLocation().GetLineSpan().StartLinePosition.Line;
            var endLine = end.GetLocation().GetLineSpan().EndLinePosition.Line;
            AddRange(startLine, endLine, FoldingRangeKind.Region);
        }

        private void AddRange(int startLine, int endLine, FoldingRangeKind? kind = null)
        {
            if (endLine <= startLine)
            {
                return;
            }

            var kindKey = kind?.ToString() ?? string.Empty;
            if (!_seen.Add((startLine, endLine, kindKey)))
            {
                return;
            }

            _results.Add(new FoldingRange
            {
                StartLine = startLine,
                EndLine = endLine,
                Kind = kind,
            });
        }
    }
}
