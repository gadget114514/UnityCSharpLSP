using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Extra data carried by workspace symbols so <c>workspaceSymbol/resolve</c> can
/// reconstruct the full symbol without re-running the search.
/// </summary>
public sealed class WorkspaceSymbolData
{
    [JsonProperty("containerName", NullValueHandling = NullValueHandling.Ignore)]
    public string? ContainerName { get; set; }

    [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
    public string? Detail { get; set; }

    [JsonProperty("kind")]
    public SymbolKind Kind { get; set; }

    [JsonProperty("workspaces", NullValueHandling = NullValueHandling.Ignore)]
    public string[]? Workspaces { get; set; }
}

/// <summary>
/// A <see cref="SymbolInformation"/> that carries LSP 3.17-style <c>data</c> and a detail string.
/// </summary>
public sealed class WorkspaceSymbolInformation : SymbolInformation
{
    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    public WorkspaceSymbolData? Data { get; set; }

    [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
    public string? Detail { get; set; }
}

/// <summary>
/// LSP 3.17 <c>WorkspaceSymbol</c> shape. The bundled protocol package only
/// defines <see cref="SymbolInformation"/>, so this DTO is defined locally.
/// </summary>
public sealed class WorkspaceSymbol
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("kind")]
    public SymbolKind Kind { get; set; }

    [JsonProperty("tags", NullValueHandling = NullValueHandling.Ignore)]
    public int[]? Tags { get; set; }

    [JsonProperty("location")]
    public Location Location { get; set; } = null!;

    [JsonProperty("containerName", NullValueHandling = NullValueHandling.Ignore)]
    public string? ContainerName { get; set; }

    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    public WorkspaceSymbolData? Data { get; set; }

    [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
    public string? Detail { get; set; }
}

/// <summary>
/// Parameters of the custom <c>workspaceSymbol/resolve</c> request.
/// </summary>
public sealed class WorkspaceSymbolResolveParams
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("kind")]
    public SymbolKind? Kind { get; set; }

    [JsonProperty("location", NullValueHandling = NullValueHandling.Ignore)]
    public Location? Location { get; set; }

    [JsonProperty("containerName", NullValueHandling = NullValueHandling.Ignore)]
    public string? ContainerName { get; set; }

    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    public WorkspaceSymbolData? Data { get; set; }
}
