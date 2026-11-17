using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/hover</c> requests to provide quick info and documentation.
/// </summary>
public sealed class HoverHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;
    private readonly ClientCapabilityStore _clientCapabilities;

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

    public HoverHandler(
        WorkspaceManagerProvider workspaceManagerProvider,
        ClientCapabilityStore clientCapabilities)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
        _clientCapabilities = clientCapabilities;
    }

    [JsonRpcMethod(Methods.TextDocumentHoverName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover?> Hover(TextDocumentPositionParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
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
        var documentationKind = _clientCapabilities.HoverDocumentationKind;
        var contents = FormatHover(signature, symbol, documentationKind);

        return new Hover
        {
            Contents = new MarkupContent
            {
                Kind = documentationKind,
                Value = contents,
            },
        };
    }

    private static string FormatHover(string signature, ISymbol symbol, MarkupKind documentationKind)
    {
        var sb = new System.Text.StringBuilder();
        if (documentationKind == MarkupKind.Markdown)
        {
            sb.AppendLine("```csharp");
            sb.AppendLine(signature);
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine(signature);
        }

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
