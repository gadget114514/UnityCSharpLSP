using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Extends the protocol's <see cref="ServerCapabilities"/> with LSP 3.16 call hierarchy support.
/// </summary>
public sealed class ExtendedServerCapabilities : ServerCapabilities
{
    [JsonProperty("callHierarchyProvider", NullValueHandling = NullValueHandling.Ignore)]
    public bool? CallHierarchyProvider { get; set; }
}
