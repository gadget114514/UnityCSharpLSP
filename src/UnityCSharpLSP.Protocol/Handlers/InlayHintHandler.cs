using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles LSP 3.17 inlay hints for inferred types and argument parameter names.
/// </summary>
public sealed class InlayHintHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public InlayHintHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(TextDocumentMethods.InlayHintName, UseSingleObjectParameterDeserialization = true)]
    public async Task<InlayHint[]?> InlayHint(InlayHintParams @params, CancellationToken ct = default)
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

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return null;
        }

        var span = @params.Range is null
            ? new TextSpan(0, text.Length)
            : LspTextMapper.ToSpan(text, @params.Range);
        if (span.IsEmpty)
        {
            span = new TextSpan(0, text.Length);
        }

        var hints = new List<InlayHint>();

        foreach (var declaration in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            if (!declaration.Type.IsVar)
            {
                continue;
            }

            var type = semanticModel.GetTypeInfo(declaration.Type, ct).Type;
            if (type is null || type.TypeKind is TypeKind.Error or TypeKind.Unknown)
            {
                continue;
            }

            var offset = declaration.Type.Span.End;
            if (offset < span.Start || offset > span.End)
            {
                continue;
            }

            hints.Add(new InlayHint
            {
                Position = ToPosition(text, offset),
                Label = ": " + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Kind = InlayHintKind.Type,
                PaddingLeft = true,
            });
        }

        foreach (var node in root.DescendantNodes())
        {
            IMethodSymbol? method = null;
            ArgumentListSyntax? argumentList = null;

            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation, ct);
                    method = symbolInfo.Symbol as IMethodSymbol
                             ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    argumentList = invocation.ArgumentList;
                    break;
                case ObjectCreationExpressionSyntax creation:
                    var creationInfo = semanticModel.GetSymbolInfo(creation, ct);
                    method = creationInfo.Symbol as IMethodSymbol
                             ?? creationInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    argumentList = creation.ArgumentList;
                    break;
            }

            if (method is null || argumentList is null)
            {
                continue;
            }

            for (var i = 0; i < argumentList.Arguments.Count && i < method.Parameters.Length; i++)
            {
                var argument = argumentList.Arguments[i];
                var parameter = method.Parameters[i];

                if (argument.NameColon is not null ||
                    argument.Expression is IdentifierNameSyntax identifier &&
                    string.Equals(identifier.Identifier.ValueText, parameter.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                var offset = argument.Span.End;
                if (offset < span.Start || offset > span.End)
                {
                    continue;
                }

                hints.Add(new InlayHint
                {
                    Position = ToPosition(text, offset),
                    Label = parameter.Name + ":",
                    Kind = InlayHintKind.Parameter,
                    PaddingRight = true,
                });
            }
        }

        return hints.Count > 0 ? hints.ToArray() : null;
    }

    private static Position ToPosition(Microsoft.CodeAnalysis.Text.SourceText text, int offset)
    {
        var position = text.Lines.GetLinePosition(offset);
        return new Position(position.Line, position.Character);
    }
}
