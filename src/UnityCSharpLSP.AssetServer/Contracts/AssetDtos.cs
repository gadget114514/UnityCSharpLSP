using Newtonsoft.Json;

namespace UnityCSharpLSP.AssetServer.Contracts;

public enum AssetKind : int
{
    Unknown = 0,
    CSharpScript = 1,
    Prefab = 2,
    Scene = 3,
    ScriptableObject = 4,
    OtherAsset = 5,
}

public enum AssetMemberKind : int
{
    Class = 0,
    Field = 1,
    Method = 2,
}

public sealed class DaemonHandshakeRequest
{
    [JsonProperty("clientVersion")]
    public string ClientVersion { get; set; } = string.Empty;

    [JsonProperty("clientPid")]
    public int ClientPid { get; set; }

    [JsonProperty("projectRoot")]
    public string ProjectRoot { get; set; } = string.Empty;
}

public sealed class DaemonHandshakeResponse
{
    [JsonProperty("daemonVersion")]
    public string DaemonVersion { get; set; } = string.Empty;

    [JsonProperty("daemonPid")]
    public int DaemonPid { get; set; }

    [JsonProperty("projectRoot")]
    public string ProjectRoot { get; set; } = string.Empty;

    [JsonProperty("state")]
    public string State { get; set; } = "Ready"; // "Indexing" | "Ready"
}

public sealed class DaemonStatusResponse
{
    [JsonProperty("state")]
    public string State { get; set; } = "Ready";

    [JsonProperty("totalAssets")]
    public int TotalAssets { get; set; }

    [JsonProperty("totalScripts")]
    public int TotalScripts { get; set; }

    [JsonProperty("totalPrefabs")]
    public int TotalPrefabs { get; set; }

    [JsonProperty("totalScenes")]
    public int TotalScenes { get; set; }

    [JsonProperty("databasePath")]
    public string DatabasePath { get; set; } = string.Empty;
}

public sealed class AssetEntryDto
{
    [JsonProperty("assetId")]
    public int AssetId { get; set; }

    [JsonProperty("guid")]
    public string Guid { get; set; } = string.Empty;

    [JsonProperty("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonProperty("fullPath")]
    public string FullPath { get; set; } = string.Empty;

    [JsonProperty("kind")]
    public AssetKind Kind { get; set; }
}

public sealed class ScriptReferenceCountDto
{
    [JsonProperty("scriptGuid")]
    public string ScriptGuid { get; set; } = string.Empty;

    [JsonProperty("directPrefabCount")]
    public int DirectPrefabCount { get; set; }

    [JsonProperty("transitivePrefabCount")]
    public int TransitivePrefabCount { get; set; }

    [JsonProperty("sceneCount")]
    public int SceneCount { get; set; }

    [JsonProperty("totalAssetCount")]
    public int TotalAssetCount => DirectPrefabCount + TransitivePrefabCount + SceneCount;
}

public sealed class AssetReferenceQueryDto
{
    [JsonProperty("scriptGuid")]
    public string ScriptGuid { get; set; } = string.Empty;

    [JsonProperty("memberKind")]
    public AssetMemberKind MemberKind { get; set; } = AssetMemberKind.Class;

    [JsonProperty("memberName")]
    public string? MemberName { get; set; }

    [JsonProperty("includeTransitiveScenes")]
    public bool IncludeTransitiveScenes { get; set; } = true;

    [JsonProperty("maxResults")]
    public int MaxResults { get; set; } = 500;
}

public sealed class AssetReferenceLocationDto
{
    [JsonProperty("fullPath")]
    public string FullPath { get; set; } = string.Empty;

    [JsonProperty("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonProperty("assetKind")]
    public AssetKind AssetKind { get; set; }

    [JsonProperty("line")]
    public int Line { get; set; }

    [JsonProperty("character")]
    public int Character { get; set; }

    [JsonProperty("endCharacter")]
    public int EndCharacter { get; set; }

    [JsonProperty("gameObjectName")]
    public string? GameObjectName { get; set; }

    [JsonProperty("propertyPath")]
    public string? PropertyPath { get; set; }

    [JsonProperty("isOverride")]
    public bool IsOverride { get; set; }
}

public sealed class AssetReferenceResultDto
{
    [JsonProperty("locations")]
    public AssetReferenceLocationDto[] Locations { get; set; } = Array.Empty<AssetReferenceLocationDto>();

    [JsonProperty("truncated")]
    public bool Truncated { get; set; }
}

public sealed class AssetRenameQueryDto
{
    [JsonProperty("scriptGuid")]
    public string ScriptGuid { get; set; } = string.Empty;

    [JsonProperty("memberKind")]
    public AssetMemberKind MemberKind { get; set; }

    [JsonProperty("oldName")]
    public string OldName { get; set; } = string.Empty;

    [JsonProperty("newName")]
    public string NewName { get; set; } = string.Empty;
}

public sealed class AssetTextEditDto
{
    [JsonProperty("line")]
    public int Line { get; set; }

    [JsonProperty("startCharacter")]
    public int StartCharacter { get; set; }

    [JsonProperty("endCharacter")]
    public int EndCharacter { get; set; }

    [JsonProperty("newText")]
    public string NewText { get; set; } = string.Empty;
}

public sealed class AssetFileEditDto
{
    [JsonProperty("fullPath")]
    public string FullPath { get; set; } = string.Empty;

    [JsonProperty("edits")]
    public AssetTextEditDto[] Edits { get; set; } = Array.Empty<AssetTextEditDto>();
}

public sealed class AssetWorkspaceEditDto
{
    [JsonProperty("fileEdits")]
    public AssetFileEditDto[] FileEdits { get; set; } = Array.Empty<AssetFileEditDto>();
}

public sealed class AssetDocumentLinkItemDto
{
    [JsonProperty("line")]
    public int Line { get; set; }

    [JsonProperty("startCharacter")]
    public int StartCharacter { get; set; }

    [JsonProperty("endCharacter")]
    public int EndCharacter { get; set; }

    [JsonProperty("targetFullPath")]
    public string TargetFullPath { get; set; } = string.Empty;

    [JsonProperty("targetGuid")]
    public string TargetGuid { get; set; } = string.Empty;
}

public sealed class AssetDocumentLinksDto
{
    [JsonProperty("links")]
    public AssetDocumentLinkItemDto[] Links { get; set; } = Array.Empty<AssetDocumentLinkItemDto>();
}

public sealed class AssetHoverDto
{
    [JsonProperty("markdown")]
    public string Markdown { get; set; } = string.Empty;

    [JsonProperty("targetFullPath")]
    public string? TargetFullPath { get; set; }

    [JsonProperty("scriptGuid")]
    public string? ScriptGuid { get; set; }

    [JsonProperty("fieldName")]
    public string? FieldName { get; set; }
}

public sealed class AssetLocationDto
{
    [JsonProperty("fullPath")]
    public string FullPath { get; set; } = string.Empty;

    [JsonProperty("line")]
    public int Line { get; set; }

    [JsonProperty("character")]
    public int Character { get; set; }
}

public sealed class AssetComponentSummaryDto
{
    [JsonProperty("componentFileId")]
    public long ComponentFileId { get; set; }

    [JsonProperty("classId")]
    public int ClassId { get; set; }

    [JsonProperty("typeName")]
    public string TypeName { get; set; } = string.Empty;

    [JsonProperty("scriptGuid")]
    public string? ScriptGuid { get; set; }

    [JsonProperty("scriptPath")]
    public string? ScriptPath { get; set; }

    [JsonProperty("line")]
    public int Line { get; set; }

    [JsonProperty("serializedFields")]
    public Dictionary<string, string>? SerializedFields { get; set; }
}

public sealed class AssetHierarchyNodeDto
{
    [JsonProperty("gameObjectFileId")]
    public long GameObjectFileId { get; set; }

    [JsonProperty("transformFileId")]
    public long TransformFileId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("line")]
    public int Line { get; set; }

    [JsonProperty("isPrefabInstance")]
    public bool IsPrefabInstance { get; set; }

    [JsonProperty("sourcePrefabGuid")]
    public string? SourcePrefabGuid { get; set; }

    [JsonProperty("sourcePrefabPath")]
    public string? SourcePrefabPath { get; set; }

    [JsonProperty("components")]
    public AssetComponentSummaryDto[] Components { get; set; } = Array.Empty<AssetComponentSummaryDto>();

    [JsonProperty("children")]
    public AssetHierarchyNodeDto[] Children { get; set; } = Array.Empty<AssetHierarchyNodeDto>();
}

public sealed class BuildSceneDto
{
    [JsonProperty("buildIndex")]
    public int BuildIndex { get; set; }

    [JsonProperty("enabled")]
    public bool Enabled { get; set; }

    [JsonProperty("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonProperty("fullPath")]
    public string FullPath { get; set; } = string.Empty;

    [JsonProperty("sceneName")]
    public string SceneName { get; set; } = string.Empty;

    [JsonProperty("guid")]
    public string Guid { get; set; } = string.Empty;
}

public sealed class AssetDiagnosticDto
{
    [JsonProperty("code")]
    public string Code { get; set; } = string.Empty; // "UNITY0001" (Missing Script) | "UNITY0002" (Dangling Ref)

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("line")]
    public int Line { get; set; }

    [JsonProperty("startCharacter")]
    public int StartCharacter { get; set; }

    [JsonProperty("endCharacter")]
    public int EndCharacter { get; set; }
}
