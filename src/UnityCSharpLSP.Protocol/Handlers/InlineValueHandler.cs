using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles LSP 3.17 inline values for locally constant-evaluable initializers.
/// </summary>
public sealed class InlineValueHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public InlineValueHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(TextDocumentMethods.InlineValueName, UseSingleObjectParameterDeserialization = true)]
    public async Task<InlineValueText[]?> InlineValue(InlineValueParams @params, CancellationToken ct = default)
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

        var values = new List<InlineValueText>();

        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer is null || !declarator.Span.IntersectsWith(span))
            {
                continue;
            }

            var constantValue = semanticModel.GetConstantValue(declarator.Initializer.Value, ct);
            if (!constantValue.HasValue)
            {
                continue;
            }

            var offset = declarator.Span.End;
            var position = text.Lines.GetLinePosition(offset);
            values.Add(new InlineValueText
            {
                Range = new LspRange
                {
                    Start = new Position(position.Line, position.Character),
                    End = new Position(position.Line, position.Character),
                },
                Text = "= " + FormatConstant(constantValue.Value),
            });
        }

        return values.Count > 0 ? values.ToArray() : null;
    }

    private static string FormatConstant(object? value)
    {
        return value switch
        {
            null => "null",
            string s => SymbolDisplay.FormatLiteral(s, quote: true),
            char c => SymbolDisplay.FormatLiteral(c, quote: true),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }
}
