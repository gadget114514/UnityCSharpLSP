namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Text-document method names missing from the protocol package in use.
/// </summary>
public static class TextDocumentMethods
{
    public const string DeclarationName = "textDocument/declaration";
    public const string SelectionRangeName = "textDocument/selectionRange";
    public const string InlayHintName = "textDocument/inlayHint";
    public const string InlineValueName = "textDocument/inlineValue";
    public const string DiagnosticName = "textDocument/diagnostic";
}
