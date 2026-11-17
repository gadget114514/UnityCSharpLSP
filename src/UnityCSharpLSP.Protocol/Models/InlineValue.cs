using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Method name for inline values (LSP 3.17).
/// The protocol package in use does not define this constant.
/// </summary>
public static class InlineValueMethods
{
    public const string Name = "textDocument/inlineValue";
}

/// <summary>
/// Parameters for <c>textDocument/inlineValue</c>.
/// </summary>
public sealed class InlineValueParams
{
    [JsonProperty("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    [JsonProperty("range")]
    public LspRange Range { get; set; } = new();
}

/// <summary>
/// An inline value displaying text at a given range (LSP 3.17).
/// </summary>
public sealed class InlineValueText
{
    [JsonProperty("range")]
    public LspRange Range { get; set; } = new();

    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;
}
