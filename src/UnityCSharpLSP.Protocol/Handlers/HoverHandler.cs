using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        if (ws.GetDocument(filePath) is null && UnityAssetFileHelper.IsYamlAssetFile(filePath))
        {
            return await HoverYamlAssetAsync(
                ws,
                filePath,
                @params.Position.Line,
                @params.Position.Character,
                _clientCapabilities.HoverDocumentationKind,
                ct).ConfigureAwait(false);
        }

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

    private static async Task<Hover?> HoverYamlAssetAsync(
        UnityWorkspaceManager ws,
        string filePath,
        int line,
        int character,
        MarkupKind documentationKind,
        CancellationToken ct)
    {
        var assetHover = await ws.AssetService.GetHoverAsync(filePath, line, character, ct).ConfigureAwait(false);
        if (assetHover is null || string.IsNullOrWhiteSpace(assetHover.Markdown))
        {
            return null;
        }

        ISymbol? targetSymbol = null;
        if (!string.IsNullOrWhiteSpace(assetHover.ScriptGuid))
        {
            var (typeSymbol, fieldSymbol) = await UnityAssetFileHelper.ResolveScriptSymbolAsync(
                ws,
                assetHover.ScriptGuid,
                assetHover.TargetFullPath,
                assetHover.FieldName,
                ct).ConfigureAwait(false);

            targetSymbol = !string.IsNullOrWhiteSpace(assetHover.FieldName)
                ? fieldSymbol
                : typeSymbol;
        }

        var value = assetHover.Markdown;
        if (targetSymbol is not null)
        {
            var csharpSection = FormatHover(
                targetSymbol.ToDisplayString(HoverDisplayFormat),
                targetSymbol,
                documentationKind,
                includeTooltip: true);

            if (!string.IsNullOrWhiteSpace(csharpSection))
            {
                value = $"{csharpSection}\n\n---\n\n{assetHover.Markdown}";
            }
        }

        return new Hover
        {
            Contents = new MarkupContent
            {
                Kind = documentationKind,
                Value = value,
            },
        };
    }

    private static string FormatHover(
        string signature,
        ISymbol symbol,
        MarkupKind documentationKind,
        bool includeTooltip = false)
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

        if (includeTooltip)
        {
            var tooltip = ExtractTooltip(symbol);
            if (!string.IsNullOrWhiteSpace(tooltip))
            {
                sb.AppendLine();
                sb.AppendLine(documentationKind == MarkupKind.Markdown
                    ? $"**Tooltip:** {tooltip}"
                    : $"Tooltip: {tooltip}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string? ExtractTooltip(ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            var name = attr.AttributeClass?.Name;
            if (name is not ("Tooltip" or "TooltipAttribute"))
            {
                continue;
            }

            if (attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is string tooltip &&
                !string.IsNullOrWhiteSpace(tooltip))
            {
                return tooltip;
            }

            if (attr.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax attrSyntax &&
                attrSyntax.ArgumentList?.Arguments.Count > 0 &&
                attrSyntax.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.StringLiteralExpression) &&
                !string.IsNullOrWhiteSpace(literal.Token.ValueText))
            {
                return literal.Token.ValueText;
            }
        }

        return null;
    }

    private static string? ExtractSummary(string xml)
    {
        try
        {
            var doc = XDocument.Parse($"<root>{xml}</root>");
            var container = doc.Root?.Element("member") ?? doc.Root;
            var summary = container?.Element("summary")?.Value;
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
