using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/hover</c> requests to provide quick info and documentation.
/// </summary>
public sealed class HoverHandler
{
    private readonly Func<UnityWorkspaceManager?> _workspaceManagerProvider;

    private static readonly SymbolDisplayFormat HoverDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeType |
                       SymbolDisplayMemberOptions.IncludeModifiers |
                       SymbolDisplayMemberOptions.IncludeAccessibility |
                       SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                          SymbolDisplayParameterOptions.IncludeName |
                          SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public HoverHandler(Func<UnityWorkspaceManager?> workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentHoverName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover?> Hover(TextDocumentPositionParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var filePath = @params.TextDocument.Uri.LocalPath;
        var (symbol, _) = await ws.GetSymbolAtPositionAsync(
            filePath,
            @params.Position.Line,
            @params.Position.Character,
            ct).ConfigureAwait(false);

        if (symbol is null)
        {
            return null;
        }

        var signature = symbol.ToDisplayString(HoverDisplayFormat);
        var markdown = FormatHoverMarkdown(signature, symbol);

        return new Hover
        {
            Contents = new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = markdown,
            },
        };
    }

    private static string FormatHoverMarkdown(string signature, ISymbol symbol)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```csharp");
        sb.AppendLine(signature);
        sb.AppendLine("```");

        var docXml = symbol.GetDocumentationCommentXml();
        if (!string.IsNullOrWhiteSpace(docXml))
        {
            var summary = ExtractSummary(docXml);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                sb.AppendLine();
                sb.AppendLine(summary);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string? ExtractSummary(string xml)
    {
        try
        {
            var doc = XDocument.Parse($"<root>{xml}</root>");
            var summary = doc.Root?.Element("summary")?.Value;
            if (!string.IsNullOrWhiteSpace(summary))
            {
                // Clean up leading/trailing whitespace and normalize indentation
                var lines = summary.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l));
                return string.Join(" ", lines);
            }
        }
        catch
        {
            // Ignore malformed XML
        }

        return null;
    }
}
