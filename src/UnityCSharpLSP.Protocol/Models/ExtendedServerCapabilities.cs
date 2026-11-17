using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Extends the protocol's <see cref="ServerCapabilities"/> with missing LSP 3.16/3.17 capabilities.
/// </summary>
public sealed class ExtendedServerCapabilities : ServerCapabilities
{
    [JsonProperty("callHierarchyProvider", NullValueHandling = NullValueHandling.Ignore)]
    public bool? CallHierarchyProvider { get; set; }

    [JsonProperty("declarationProvider", NullValueHandling = NullValueHandling.Ignore)]
    public bool? DeclarationProvider { get; set; }

    [JsonProperty("selectionRangeProvider", NullValueHandling = NullValueHandling.Ignore)]
    public bool? SelectionRangeProvider { get; set; }

    [JsonProperty("typeHierarchyProvider", NullValueHandling = NullValueHandling.Ignore)]
    public bool? TypeHierarchyProvider { get; set; }

    [JsonProperty("monikerProvider", NullValueHandling = NullValueHandling.Ignore)]
    public bool? MonikerProvider { get; set; }

    [JsonProperty("inlayHintProvider", NullValueHandling = NullValueHandling.Ignore)]
    public InlayHintOptions? InlayHintProvider { get; set; }

    [JsonProperty("inlineValueProvider", NullValueHandling = NullValueHandling.Ignore)]
    public bool? InlineValueProvider { get; set; }

    [JsonProperty("diagnosticProvider", NullValueHandling = NullValueHandling.Ignore)]
    public DiagnosticOptions? DiagnosticProvider { get; set; }

    [JsonProperty("positionEncoding", NullValueHandling = NullValueHandling.Ignore)]
    public string? PositionEncoding { get; set; }

    [JsonProperty("workspace", NullValueHandling = NullValueHandling.Ignore)]
    public WorkspaceServerCapabilities? Workspace { get; set; }

    // Hides the base bool? property so the wire format can advertise resolve support.
    [JsonProperty("workspaceSymbolProvider", NullValueHandling = NullValueHandling.Ignore)]
    public new WorkspaceSymbolResolveOptions? WorkspaceSymbolProvider { get; set; }

    // Hides the base option so the nested keys are emitted in camelCase.
    [JsonProperty("semanticTokensProvider", NullValueHandling = NullValueHandling.Ignore)]
    public new LspSemanticTokensOptions? SemanticTokensOptions { get; set; }
}

public sealed class WorkspaceSymbolResolveOptions
{
    [JsonProperty("resolveProvider")]
    public bool ResolveProvider { get; set; }

    [JsonProperty("workDoneProgress", NullValueHandling = NullValueHandling.Ignore)]
    public bool? WorkDoneProgress { get; set; }
}
