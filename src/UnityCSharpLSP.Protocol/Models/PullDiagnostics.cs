using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Method name for pull diagnostics (LSP 3.17).
/// The protocol package in use does not define this constant.
/// </summary>
public static class DiagnosticMethods
{
    public const string Name = "textDocument/diagnostic";

    public const string WorkspaceName = "workspace/diagnostic";
}

/// <summary>
/// Parameters for <c>textDocument/diagnostic</c>.
/// </summary>
public sealed class DocumentDiagnosticParams
{
    [JsonProperty("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    [JsonProperty("identifier", NullValueHandling = NullValueHandling.Ignore)]
    public string? Identifier { get; set; }

    [JsonProperty("previousResultId", NullValueHandling = NullValueHandling.Ignore)]
    public object? PreviousResultId { get; set; }

    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    public object? Data { get; set; }
}

/// <summary>
/// A document diagnostic report (full or unchanged).
/// </summary>
public sealed class DocumentDiagnosticReport
{
    [JsonProperty("kind")]
    public string Kind { get; set; } = "full";

    [JsonProperty("resultId", NullValueHandling = NullValueHandling.Ignore)]
    public string? ResultId { get; set; }

    [JsonProperty("diagnostics", NullValueHandling = NullValueHandling.Ignore)]
    public Diagnostic[]? Diagnostics { get; set; }
}

/// <summary>
/// Parameters for <c>workspace/diagnostic</c> (LSP 3.17).
/// </summary>
public sealed class WorkspaceDiagnosticParams
{
    [JsonProperty("identifier", NullValueHandling = NullValueHandling.Ignore)]
    public string? Identifier { get; set; }

    [JsonProperty("previousResultIds", NullValueHandling = NullValueHandling.Ignore)]
    public PreviousResultId[]? PreviousResultIds { get; set; }

    [JsonProperty("partialResultToken", NullValueHandling = NullValueHandling.Ignore)]
    public JToken? PartialResultToken { get; set; }

    [JsonProperty("workDoneToken", NullValueHandling = NullValueHandling.Ignore)]
    public JToken? WorkDoneToken { get; set; }
}

/// <summary>
/// A result id previously returned for one document URI.
/// </summary>
public sealed class PreviousResultId
{
    [JsonProperty("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonProperty("value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Result of <c>workspace/diagnostic</c>.
/// </summary>
public sealed class WorkspaceDiagnosticReport
{
    [JsonProperty("items")]
    public WorkspaceDocumentDiagnosticReport[] Items { get; set; } = Array.Empty<WorkspaceDocumentDiagnosticReport>();
}

/// <summary>
/// A full or unchanged diagnostic report for one document in the workspace.
/// </summary>
public sealed class WorkspaceDocumentDiagnosticReport
{
    [JsonProperty("kind")]
    public string Kind { get; set; } = "full";

    [JsonProperty("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonProperty("version")]
    public int? Version { get; set; }

    [JsonProperty("resultId", NullValueHandling = NullValueHandling.Ignore)]
    public string? ResultId { get; set; }

    [JsonProperty("diagnostics", NullValueHandling = NullValueHandling.Ignore)]
    public Diagnostic[]? Diagnostics { get; set; }
}

/// <summary>
/// Capability object advertised for <c>textDocument/diagnostic</c>.
/// </summary>
public sealed class DiagnosticOptions
{
    [JsonProperty("identifier")]
    public string Identifier { get; set; } = "unitycsharp";

    [JsonProperty("interFileDependencies")]
    public bool InterFileDependencies { get; set; }

    [JsonProperty("workspaceDiagnostics")]
    public bool WorkspaceDiagnostics { get; set; }
}
