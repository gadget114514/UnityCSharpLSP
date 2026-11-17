using Newtonsoft.Json;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// LSP <c>SemanticTokensOptions</c> with spec-compliant camelCase property names.
/// The protocol package emits PascalCase for the nested members, which makes
/// clients that inspect <c>full</c> / <c>range</c> treat the provider as absent.
/// </summary>
public sealed class LspSemanticTokensOptions
{
    [JsonProperty("legend")]
    public LspSemanticTokensLegend Legend { get; set; } = new();

    [JsonProperty("full", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Full { get; set; }

    [JsonProperty("range", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Range { get; set; }

    [JsonProperty("workDoneProgress", NullValueHandling = NullValueHandling.Ignore)]
    public bool? WorkDoneProgress { get; set; }
}

/// <summary>
/// LSP <c>SemanticTokensLegend</c> with camelCase property names.
/// </summary>
public sealed class LspSemanticTokensLegend
{
    [JsonProperty("tokenTypes")]
    public string[] TokenTypes { get; set; } = Array.Empty<string>();

    [JsonProperty("tokenModifiers")]
    public string[] TokenModifiers { get; set; } = Array.Empty<string>();
}
