using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Publish diagnostics parameters with the document version added in LSP 3.15.
/// The protocol package's <see cref="PublishDiagnosticParams"/> does not expose Version.
/// </summary>
public sealed class VersionedPublishDiagnosticsParams
{
    [JsonProperty("uri")]
    public Uri Uri { get; set; } = null!;

    [JsonProperty("diagnostics")]
    public Diagnostic[] Diagnostics { get; set; } = Array.Empty<Diagnostic>();

    [JsonProperty("version", NullValueHandling = NullValueHandling.Ignore)]
    public int? Version { get; set; }
}
