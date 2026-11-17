using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Workspace symbol parameters with the wire-format partial result token preserved.
/// </summary>
public sealed class WorkspaceSymbolRequestParams
{
    [JsonProperty("query")]
    public string? Query { get; set; }

    [JsonProperty("partialResultToken", NullValueHandling = NullValueHandling.Ignore)]
    public JToken? PartialResultToken { get; set; }
}

/// <summary>
/// Parameters for an LSP <c>$/progress</c> notification.
/// </summary>
public sealed class ProgressParams<T>
{
    [JsonProperty("token")]
    public JToken Token { get; set; } = JValue.CreateNull();

    [JsonProperty("value")]
    public T Value { get; set; } = default!;
}
