using Newtonsoft.Json;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// LSP method name for moniker requests.
/// The protocol package in use does not define this constant.
/// </summary>
public static class MonikerMethods
{
    public const string TextDocumentMonikerName = "textDocument/moniker";
}

/// <summary>
/// A language-specific, stable identifier for a symbol.
/// </summary>
public sealed class Moniker
{
    [JsonProperty("scheme")]
    public string Scheme { get; set; } = string.Empty;

    [JsonProperty("identifier")]
    public string Identifier { get; set; } = string.Empty;

    [JsonProperty("unique")]
    public string Unique { get; set; } = string.Empty;
}
