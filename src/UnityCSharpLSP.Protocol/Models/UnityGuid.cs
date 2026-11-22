using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Custom Unity GUID methods (shared names with UnityCSharpFastLSP).
/// </summary>
public static class UnityGuidMethods
{
    /// <summary>Returns the <c>.meta</c> GUID position and the asset for a GUID (or asset path).</summary>
    public const string GuidDefinitionName = "unitylsp/guidDefinition";

    /// <summary>Returns the text-asset locations (scenes, prefabs, .meta, .asmdef...) that reference a GUID.</summary>
    public const string GuidReferencesName = "unitylsp/guidReferences";
}

/// <summary>Either <see cref="Guid"/> (32 hex chars) or <see cref="Asset"/> (file URI / absolute / workspace-relative path).</summary>
public sealed class GuidQueryParams
{
    [JsonProperty("guid", NullValueHandling = NullValueHandling.Ignore)]
    public string? Guid { get; set; }

    [JsonProperty("asset", NullValueHandling = NullValueHandling.Ignore)]
    public string? Asset { get; set; }

    [JsonProperty("maxResults", NullValueHandling = NullValueHandling.Ignore)]
    public int? MaxResults { get; set; }
}

public sealed class GuidDefinitionResult
{
    [JsonProperty("guid")]
    public string Guid { get; set; } = string.Empty;

    [JsonProperty("assetUri")]
    public string AssetUri { get; set; } = string.Empty;

    [JsonProperty("meta")]
    public Location Meta { get; set; } = new();
}

public sealed class GuidReference
{
    [JsonProperty("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonProperty("range")]
    public LspRange Range { get; set; } = new();

    [JsonProperty("lineText")]
    public string LineText { get; set; } = string.Empty;

    [JsonProperty("classId", NullValueHandling = NullValueHandling.Ignore)]
    public int? ClassId { get; set; }

    [JsonProperty("objectFileId", NullValueHandling = NullValueHandling.Ignore)]
    public long? ObjectFileId { get; set; }

    [JsonProperty("fileId", NullValueHandling = NullValueHandling.Ignore)]
    public long? FileId { get; set; }
}

public sealed class GuidReferencesResult
{
    [JsonProperty("guid")]
    public string Guid { get; set; } = string.Empty;

    [JsonProperty("assetUri")]
    public string? AssetUri { get; set; }

    [JsonProperty("references")]
    public List<GuidReference> References { get; set; } = new();

    [JsonProperty("truncated")]
    public bool Truncated { get; set; }
}
