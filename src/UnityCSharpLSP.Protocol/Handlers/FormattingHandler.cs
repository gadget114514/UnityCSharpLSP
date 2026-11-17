using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;
using LspFormattingOptions = Microsoft.VisualStudio.LanguageServer.Protocol.FormattingOptions;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles document formatting, range formatting, and on-type formatting using Roslyn's formatter.
/// </summary>
public sealed class FormattingHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public FormattingHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentFormattingName, UseSingleObjectParameterDeserialization = true)]
    public Task<TextEdit[]?> Format(DocumentFormattingParams @params, CancellationToken ct = default)
    {
        return FormatDocumentAsync(@params.TextDocument, @params.Options, range: null, ct);
    }

    [JsonRpcMethod(Methods.TextDocumentRangeFormattingName, UseSingleObjectParameterDeserialization = true)]
    public Task<TextEdit[]?> RangeFormat(DocumentRangeFormattingParams @params, CancellationToken ct = default)
    {
        return FormatDocumentAsync(@params.TextDocument, @params.Options, @params.Range, ct);
    }

    [JsonRpcMethod(Methods.TextDocumentOnTypeFormattingName, UseSingleObjectParameterDeserialization = true)]
    public async Task<TextEdit[]?> OnTypeFormat(DocumentOnTypeFormattingParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var document = ws.GetDocument(@params.TextDocument.Uri.LocalPath);
        if (document is null)
        {
            return null;
        }

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var offset = LspTextMapper.ToOffset(text, @params.Position);
        var startLine = text.Lines.GetLineFromPosition(offset);

        // A newline affects the line just completed; other triggers affect the current line.
        var isNewline = @params.Character is "\n" or "\r" or "\r\n";
        var spanStart = startLine.Start;
        if (isNewline && startLine.LineNumber > 0)
        {
            spanStart = text.Lines[startLine.LineNumber - 1].Start;
        }

        var span = TextSpan.FromBounds(spanStart, Math.Max(spanStart, offset));
        return await FormatDocumentAsync(@params.TextDocument, @params.Options, LspTextMapper.ToRange(text, span), ct).ConfigureAwait(false);
    }

    private async Task<TextEdit[]?> FormatDocumentAsync(
        TextDocumentIdentifier textDocument,
        LspFormattingOptions options,
        LspRange? range,
        CancellationToken ct)
    {
        var ws = _workspaceManagerProvider(textDocument?.Uri?.LocalPath);
        if (ws is null || textDocument?.Uri is null)
        {
            return null;
        }

        var document = ws.GetDocument(textDocument.Uri.LocalPath);
        if (document is null)
        {
            return null;
        }

        var originalText = await document.GetTextAsync(ct).ConfigureAwait(false);
        var formatOptions = CreateOptions(document, options, originalText);

        Document formatted;
        if (range is not null)
        {
            var span = LspTextMapper.ToSpan(originalText, range);
            if (span.IsEmpty)
            {
                return Array.Empty<TextEdit>();
            }
            formatted = await Formatter.FormatAsync(document, span, formatOptions, ct).ConfigureAwait(false);
        }
        else
        {
            formatted = await Formatter.FormatAsync(document, formatOptions, ct).ConfigureAwait(false);
        }

        var formattedText = await formatted.GetTextAsync(ct).ConfigureAwait(false);
        return ToTextEdits(originalText, formattedText.GetTextChanges(originalText));
    }

    private static Microsoft.CodeAnalysis.Options.OptionSet CreateOptions(
        Document document,
        LspFormattingOptions? options,
        SourceText originalText)
    {
        var tabSize = options?.TabSize is > 0 ? options.TabSize : 4;
        var insertSpaces = options?.InsertSpaces ?? true;
        var newLine = originalText.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // Nullable context options stay disabled (matching the Unity compilation options);
        // only indentation settings are mapped from the LSP formatting options.
        return document.Project.Solution.Options
            .WithChangedOption(Microsoft.CodeAnalysis.Formatting.FormattingOptions.TabSize, LanguageNames.CSharp, tabSize)
            .WithChangedOption(Microsoft.CodeAnalysis.Formatting.FormattingOptions.IndentationSize, LanguageNames.CSharp, tabSize)
            .WithChangedOption(Microsoft.CodeAnalysis.Formatting.FormattingOptions.UseTabs, LanguageNames.CSharp, !insertSpaces)
            .WithChangedOption(Microsoft.CodeAnalysis.Formatting.FormattingOptions.NewLine, LanguageNames.CSharp, newLine);
    }

    internal static TextEdit[] ToTextEdits(SourceText originalText, IEnumerable<TextChange> changes)
    {
        return changes.Select(change => new TextEdit
        {
            Range = LspTextMapper.ToRange(originalText, change.Span),
            NewText = change.NewText ?? string.Empty,
        }).ToArray();
    }
}
