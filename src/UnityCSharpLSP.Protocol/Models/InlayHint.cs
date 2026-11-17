using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using LspPosition = Microsoft.VisualStudio.LanguageServer.Protocol.Position;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Method name and kind constants for inlay hints (LSP 3.17).
/// The protocol package in use does not define these.
/// </summary>
public static class InlayHintMethods
{
    public const string Name = "textDocument/inlayHint";
}

public static class InlayHintKind
{
    public const int Type = 1;
    public const int Parameter = 2;
}

/// <summary>
/// Parameters for <c>textDocument/inlayHint</c>.
/// </summary>
public sealed class InlayHintParams
{
    [JsonProperty("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    [JsonProperty("range")]
    public LspRange Range { get; set; } = new();
}

/// <summary>
/// An inlay hint (LSP 3.17).
/// </summary>
public sealed class InlayHint
{
    [JsonProperty("position")]
    public LspPosition Position { get; set; } = new(0, 0);

    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;

    [JsonProperty("kind", NullValueHandling = NullValueHandling.Ignore)]
    public int? Kind { get; set; }

    [JsonProperty("paddingLeft", NullValueHandling = NullValueHandling.Ignore)]
    public bool? PaddingLeft { get; set; }

    [JsonProperty("paddingRight", NullValueHandling = NullValueHandling.Ignore)]
    public bool? PaddingRight { get; set; }

    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    public object? Data { get; set; }
}

/// <summary>
/// Capability object advertised for <c>textDocument/inlayHint</c>.
/// </summary>
public sealed class InlayHintOptions
{
    [JsonProperty("resolveProvider")]
    public bool ResolveProvider { get; set; }
}
