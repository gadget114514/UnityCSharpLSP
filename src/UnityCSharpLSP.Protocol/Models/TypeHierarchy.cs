using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// LSP method names for type hierarchy requests (LSP 3.17).
/// The protocol package in use does not define these constants.
/// </summary>
public static class TypeHierarchyMethods
{
    public const string PrepareName = "textDocument/prepareTypeHierarchy";
    public const string SupertypesName = "typeHierarchy/supertypes";
    public const string SubtypesName = "typeHierarchy/subtypes";
}

/// <summary>
/// Represents a type in a type hierarchy (LSP 3.17).
/// </summary>
public sealed class TypeHierarchyItem
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("kind")]
    public SymbolKind Kind { get; set; }

    [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
    public string? Detail { get; set; }

    [JsonProperty("uri")]
    public Uri Uri { get; set; } = null!;

    [JsonProperty("range")]
    public LspRange Range { get; set; } = new();

    [JsonProperty("selectionRange")]
    public LspRange SelectionRange { get; set; } = new();

    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    public TypeHierarchyData? Data { get; set; }
}

/// <summary>
/// Opaque symbol identifier carried between type hierarchy requests.
/// </summary>
public sealed class TypeHierarchyData
{
    [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)]
    public string? Id { get; set; }
}

/// <summary>
/// Parameters for <c>typeHierarchy/supertypes</c>.
/// </summary>
public sealed class TypeHierarchySupertypesParams
{
    [JsonProperty("item")]
    public TypeHierarchyItem Item { get; set; } = null!;
}

/// <summary>
/// Parameters for <c>typeHierarchy/subtypes</c>.
/// </summary>
public sealed class TypeHierarchySubtypesParams
{
    [JsonProperty("item")]
    public TypeHierarchyItem Item { get; set; } = null!;
}
