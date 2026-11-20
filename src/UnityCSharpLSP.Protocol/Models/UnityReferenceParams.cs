using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Standard <c>textDocument/references</c> parameters plus optional glob filters understood by
/// UnityCSharpLSP. Clients that do not send the extra fields keep the standard behaviour.
/// </summary>
public sealed class UnityReferenceParams
{
    [JsonProperty("textDocument")]
    public TextDocumentIdentifier? TextDocument { get; set; }

    [JsonProperty("position")]
    public Position? Position { get; set; }

    [JsonProperty("context")]
    public ReferenceContext? Context { get; set; }

    [JsonProperty("includePatterns")]
    public string[]? IncludePatterns { get; set; }

    [JsonProperty("excludePatterns")]
    public string[]? ExcludePatterns { get; set; }
}
