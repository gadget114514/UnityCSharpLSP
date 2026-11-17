using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// A range with its containing selection ranges (LSP 3.16).
/// </summary>
public sealed class SelectionRange
{
    [JsonProperty("range")]
    public LspRange Range { get; set; } = new();

    [JsonProperty("parent", NullValueHandling = NullValueHandling.Ignore)]
    public SelectionRange? Parent { get; set; }
}

/// <summary>
/// Parameters for <c>textDocument/selectionRange</c>.
/// </summary>
public sealed class SelectionRangeParams
{
    [JsonProperty("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    [JsonProperty("positions")]
    public Position[] Positions { get; set; } = Array.Empty<Position>();
}
