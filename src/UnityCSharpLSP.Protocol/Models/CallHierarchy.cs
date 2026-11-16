using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// LSP method names for call hierarchy requests.
/// The protocol package in use does not define these constants (LSP 3.16).
/// </summary>
public static class CallHierarchyMethods
{
    public const string PrepareName = "textDocument/prepareCallHierarchy";
    public const string IncomingCallsName = "callHierarchy/incomingCalls";
    public const string OutgoingCallsName = "callHierarchy/outgoingCalls";
}

/// <summary>
/// Represents an item in a call hierarchy (LSP 3.16).
/// </summary>
public sealed class CallHierarchyItem
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
    public CallHierarchyData? Data { get; set; }
}

/// <summary>
/// Opaque symbol identifier carried between call hierarchy requests.
/// </summary>
public sealed class CallHierarchyData
{
    [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)]
    public string? Id { get; set; }
}

/// <summary>
/// Parameters for <c>callHierarchy/incomingCalls</c>.
/// </summary>
public sealed class CallHierarchyIncomingCallsParams
{
    [JsonProperty("item")]
    public CallHierarchyItem Item { get; set; } = null!;
}

/// <summary>
/// Parameters for <c>callHierarchy/outgoingCalls</c>.
/// </summary>
public sealed class CallHierarchyOutgoingCallsParams
{
    [JsonProperty("item")]
    public CallHierarchyItem Item { get; set; } = null!;
}

/// <summary>
/// An incoming call (a caller of the requested item).
/// </summary>
public sealed class CallHierarchyIncomingCall
{
    [JsonProperty("from")]
    public CallHierarchyItem From { get; set; } = null!;

    [JsonProperty("fromRanges")]
    public LspRange[] FromRanges { get; set; } = Array.Empty<LspRange>();
}

/// <summary>
/// An outgoing call (a callee invoked by the requested item).
/// </summary>
public sealed class CallHierarchyOutgoingCall
{
    [JsonProperty("to")]
    public CallHierarchyItem To { get; set; } = null!;

    [JsonProperty("fromRanges")]
    public LspRange[] FromRanges { get; set; } = Array.Empty<LspRange>();
}
