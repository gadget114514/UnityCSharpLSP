# Unity Prefab / Scene 常駐データサーバ & C# LSP 詳細設計書 (実装仕様書)

本ドキュメントは、[doc/prefabserver.md](prefabserver.md) の基本設計を、実装担当エンジニアおよびワーカーLLMが迷わずコードに落とし込める粒度まで具体化した詳細設計書です。

---

## 1. ソリューション・プロジェクト構成仕様

### 1.1 新規追加プロジェクト

既存の `UnityCSharpLSP.sln` に対して、以下の2プロジェクトを追加します。

1. **`src/UnityCSharpLSP.AssetServer/UnityCSharpLSP.AssetServer.csproj`**
   * 役割: SQLite ストレージ、ゼロアロケーション YAML スキャナ、Hierarchy パーサ、`IAssetDataService` RPC 契約およびデーモンホスト/クライアント実装。
   * Roslyn (`Microsoft.CodeAnalysis`) への参照は**持たない**（デーモンプロセスのメモリフットプリントを最小化するため）。
2. **`tests/UnityCSharpLSP.AssetServer.Tests/UnityCSharpLSP.AssetServer.Tests.csproj`**
   * 役割: `AssetGuid`, `FastUnityYamlScanner`, `AssetSqliteStore`, `HierarchyBuilder`, `BuildSettingsParser`, Named Pipe デーモン通信の単体・結合テスト。

#### `src/UnityCSharpLSP.AssetServer/UnityCSharpLSP.AssetServer.csproj` 定義
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.10" />
    <PackageReference Include="StreamJsonRpc" Version="2.26.150" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>

</Project>
```

#### 既存プロジェクトの参照更新
* `src/UnityCSharpLSP.Workspace/UnityCSharpLSP.Workspace.csproj`:
  * `<ProjectReference Include="..\UnityCSharpLSP.AssetServer\UnityCSharpLSP.AssetServer.csproj" />` を追加。
* `src/UnityCSharpLSP.Protocol/UnityCSharpLSP.Protocol.csproj`:
  * `<ProjectReference Include="..\UnityCSharpLSP.AssetServer\UnityCSharpLSP.AssetServer.csproj" />` を追加。
* `src/UnityCSharpLSP.Server/UnityCSharpLSP.Server.csproj`:
  * `<ProjectReference Include="..\UnityCSharpLSP.AssetServer\UnityCSharpLSP.AssetServer.csproj" />` を追加。

---

## 2. 値型・RPC契約・DTO完全仕様 (`UnityCSharpLSP.AssetServer.Contracts`)

### 2.1 `AssetGuid` 構造体 (`src/UnityCSharpLSP.AssetServer/Storage/AssetGuid.cs`)

Unity の `.meta` が持つ32桁の16進数GUIDを、ヒープアロケーションなしで扱う16バイト値型です。SQLite には `byte[16]` (`BLOB(16)`) として格納します。

```csharp
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UnityCSharpLSP.AssetServer.Storage;

/// <summary>
/// Represents a 128-bit (32-hex-character) Unity asset GUID without string allocations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct AssetGuid(ulong High, ulong Low) : IComparable<AssetGuid>
{
    public static readonly AssetGuid Zero = new(0UL, 0UL);

    public bool IsZero => High == 0UL && Low == 0UL;

    /// <summary>
    /// Built-in Unity GUIDs (e.g. 0000000000000000f000000000000000) have High == 0.
    /// </summary>
    public bool IsUnityBuiltin => High == 0UL && Low != 0UL;

    public static bool TryParseHex(ReadOnlySpan<byte> utf8Hex, out AssetGuid guid)
    {
        guid = Zero;
        if (utf8Hex.Length < 32)
        {
            return false;
        }

        ulong high = 0;
        ulong low = 0;
        for (int i = 0; i < 16; i++)
        {
            int nibble = HexVal(utf8Hex[i]);
            if (nibble < 0) return false;
            high = (high << 4) | (uint)nibble;
        }
        for (int i = 16; i < 32; i++)
        {
            int nibble = HexVal(utf8Hex[i]);
            if (nibble < 0) return false;
            low = (low << 4) | (uint)nibble;
        }

        guid = new AssetGuid(high, low);
        return true;
    }

    public static bool TryParseHex(ReadOnlySpan<char> hex, out AssetGuid guid)
    {
        guid = Zero;
        if (hex.Length < 32)
        {
            return false;
        }

        ulong high = 0;
        ulong low = 0;
        for (int i = 0; i < 16; i++)
        {
            int nibble = HexVal(hex[i]);
            if (nibble < 0) return false;
            high = (high << 4) | (uint)nibble;
        }
        for (int i = 16; i < 32; i++)
        {
            int nibble = HexVal(hex[i]);
            if (nibble < 0) return false;
            low = (low << 4) | (uint)nibble;
        }

        guid = new AssetGuid(high, low);
        return true;
    }

    public byte[] ToByteArray()
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), Low);
        return bytes;
    }

    public static AssetGuid FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) return Zero;
        var high = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(0, 8));
        var low = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8));
        return new AssetGuid(high, low);
    }

    public string ToHexString() => $"{High:x16}{Low:x16}";

    public override string ToString() => ToHexString();

    public int CompareTo(AssetGuid other)
    {
        int cmp = High.CompareTo(other.High);
        return cmp != 0 ? cmp : Low.CompareTo(other.Low);
    }

    private static int HexVal(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
```

### 2.2 `AssetKind` 列挙型 (`src/UnityCSharpLSP.AssetServer/Contracts/AssetDtos.cs`)

```csharp
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
```

### 2.3 DTO 定義一覧 (`src/UnityCSharpLSP.AssetServer/Contracts/AssetDtos.cs`)

`StreamJsonRpc` の `JsonMessageFormatter` (Newtonsoft.Json) でシリアライズされるため、すべての DTO はデフォルトコンストラクタまたは `init`/`set` プロパティを持つクラスとして定義します。

```csharp
using Newtonsoft.Json;

namespace UnityCSharpLSP.AssetServer.Contracts;

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
    public int BuildIndex { get; set; } // Enabled シーンの連番 (無効な場合は -1)

    [JsonProperty("enabled")]
    public bool Enabled { get; set; }

    [JsonProperty("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonProperty("fullPath")]
    public string FullPath { get; set; } = string.Empty;

    [JsonProperty("sceneName")]
    public string SceneName { get; set; } = string.Empty; // Path.GetFileNameWithoutExtension(RelativePath)

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
```

### 2.4 `IAssetDataService` インターフェース (`src/UnityCSharpLSP.AssetServer/Contracts/IAssetDataService.cs`)

```csharp
using StreamJsonRpc;

namespace UnityCSharpLSP.AssetServer.Contracts;

public interface IAssetDataService
{
    [JsonRpcMethod("assetd/handshake", UseSingleObjectParameterDeserialization = true)]
    Task<DaemonHandshakeResponse> HandshakeAsync(DaemonHandshakeRequest request, CancellationToken ct = default);

    [JsonRpcMethod("assetd/status")]
    Task<DaemonStatusResponse> GetStatusAsync(CancellationToken ct = default);

    [JsonRpcMethod("assetd/getAssetByGuid")]
    Task<AssetEntryDto?> GetAssetByGuidAsync(string guidHex, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getAssetByPath")]
    Task<AssetEntryDto?> GetAssetByPathAsync(string filePath, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getScriptReferenceCounts")]
    Task<ScriptReferenceCountDto[]> GetScriptReferenceCountsAsync(string[] scriptGuids, CancellationToken ct = default);

    [JsonRpcMethod("assetd/findReferences", UseSingleObjectParameterDeserialization = true)]
    Task<AssetReferenceResultDto> FindReferencesAsync(AssetReferenceQueryDto query, CancellationToken ct = default);

    [JsonRpcMethod("assetd/computeRenameEdits", UseSingleObjectParameterDeserialization = true)]
    Task<AssetWorkspaceEditDto> ComputeRenameEditsAsync(AssetRenameQueryDto query, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getDocumentLinks")]
    Task<AssetDocumentLinksDto> GetDocumentLinksAsync(string assetPath, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getHover")]
    Task<AssetHoverDto?> GetHoverAsync(string assetPath, int line, int character, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getDefinition")]
    Task<AssetLocationDto?> GetDefinitionAsync(string assetPath, int line, int character, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getHierarchy")]
    Task<AssetHierarchyNodeDto[]> GetHierarchyAsync(string assetPath, int maxDepth, bool includeSerializedValues, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getBuildScenes")]
    Task<BuildSceneDto[]> GetBuildScenesAsync(CancellationToken ct = default);

    [JsonRpcMethod("assetd/getAssetDiagnostics")]
    Task<AssetDiagnosticDto[]> GetAssetDiagnosticsAsync(string assetPath, CancellationToken ct = default);

    [JsonRpcMethod("assetd/isMethodReferencedByUnityEvent")]
    Task<bool> IsMethodReferencedByUnityEventAsync(string scriptGuid, string methodName, CancellationToken ct = default);

    [JsonRpcMethod("assetd/rescan")]
    Task<DaemonStatusResponse> RescanAsync(CancellationToken ct = default);

    [JsonRpcMethod("assetd/shutdown")]
    Task ShutdownAsync(CancellationToken ct = default);
}
```

---

## 3. SQLite ストレージ & オンメモリグラフ詳細仕様 (`UnityCSharpLSP.AssetServer.Storage`)

### 3.1 `AssetSqliteStore` (`src/UnityCSharpLSP.AssetServer/Storage/AssetSqliteStore.cs`)

#### 3.1.1 DB パスと接続文字列
* デフォルトのDBパス: `Path.Combine(projectRoot, "Library", "UnityCSharpLSP", "asset-db.sqlite")`
* ディレクトリが存在しない場合は `Directory.CreateDirectory` で作成します。
* 接続文字列:
  ```csharp
  var connectionString = new SqliteConnectionStringBuilder
  {
      DataSource = dbPath,
      Mode = SqliteOpenMode.ReadWriteCreate,
      Cache = SqliteCacheMode.Shared,
      Pooling = true,
  }.ToString();
  ```
* 書き込み排他制御: デーモンプロセス内で `SemaphoreSlim _writeLock = new(1, 1)` を持ち、すべての `INSERT` / `UPDATE` / `DELETE` トランザクションを直列化します。読み取りクエリは WAL モードによりロックなしで並列実行可能です。

#### 3.1.2 テーブル初期化 SQL
`InitializeDatabase()` で以下を実行します（スキーマバージョン不一致時は `DROP TABLE` して再構築）：

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA temp_store = MEMORY;
PRAGMA mmap_size = 268435456;

CREATE TABLE IF NOT EXISTS schema_meta (
    version INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS assets (
    asset_id    INTEGER PRIMARY KEY AUTOINCREMENT,
    guid        BLOB(16) NOT NULL UNIQUE,
    rel_path    TEXT NOT NULL UNIQUE,
    asset_kind  INTEGER NOT NULL,
    meta_mtime  INTEGER NOT NULL,
    asset_mtime INTEGER NOT NULL,
    asset_size  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS script_attachments (
    asset_id         INTEGER NOT NULL,
    component_fid    INTEGER NOT NULL,
    game_object_fid  INTEGER NOT NULL,
    game_object_name TEXT,
    script_guid      BLOB(16) NOT NULL,
    line_number      INTEGER NOT NULL,
    PRIMARY KEY (asset_id, component_fid)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS idx_script_attachments_guid ON script_attachments(script_guid);

CREATE TABLE IF NOT EXISTS prefab_instances (
    asset_id            INTEGER NOT NULL,
    prefab_instance_fid INTEGER NOT NULL,
    source_prefab_guid  BLOB(16) NOT NULL,
    line_number         INTEGER NOT NULL,
    PRIMARY KEY (asset_id, prefab_instance_fid)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS idx_prefab_instances_source ON prefab_instances(source_prefab_guid);

CREATE TABLE IF NOT EXISTS prefab_modifications (
    asset_id            INTEGER NOT NULL,
    prefab_instance_fid INTEGER NOT NULL,
    target_guid         BLOB(16) NOT NULL,
    target_fid          INTEGER NOT NULL,
    root_field_name     TEXT NOT NULL,
    property_path       TEXT NOT NULL,
    line_number         INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_modifications_target ON prefab_modifications(target_guid, target_fid, root_field_name);
CREATE INDEX IF NOT EXISTS idx_modifications_asset ON prefab_modifications(asset_id);

CREATE TABLE IF NOT EXISTS unity_event_calls (
    asset_id        INTEGER NOT NULL,
    component_fid   INTEGER NOT NULL,
    target_guid     BLOB(16),
    target_fid      INTEGER NOT NULL,
    method_name     TEXT NOT NULL,
    line_number     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_unity_event_method ON unity_event_calls(method_name);
CREATE INDEX IF NOT EXISTS idx_unity_event_asset ON unity_event_calls(asset_id);

CREATE TABLE IF NOT EXISTS serialized_fields (
    asset_id        INTEGER NOT NULL,
    component_fid   INTEGER NOT NULL,
    field_name      TEXT NOT NULL,
    ref_guid        BLOB(16),
    ref_fid         INTEGER,
    line_number     INTEGER NOT NULL,
    PRIMARY KEY (asset_id, component_fid, field_name)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS idx_serialized_fields_ref_guid ON serialized_fields(ref_guid) WHERE ref_guid IS NOT NULL;
```

#### 3.1.3 `FileSystemEnumerable` による差分スキャン (`SyncWorkspaceAsync`)
1. `SELECT asset_id, guid, rel_path, meta_mtime, asset_mtime, asset_size FROM assets` を実行し、既存の `Dictionary<string, CachedAssetRow>(StringComparer.OrdinalIgnoreCase)` を取得。
2. プロジェクトルート配下の `Assets` ディレクトリと `Packages` ディレクトリ（存在しない場合はプロジェクトルート直下、ただし `Library`, `Temp`, `Logs`, `obj`, `bin`, `.git`, `.hancho`, `.vs` はディレクトリ侵入自体を除外）を `System.IO.Enumeration.FileSystemEnumerable<(string FullPath, long LastWriteTicks, long Length, bool IsDirectory)>` で走査。
3. 走査結果から `.meta` ファイルのマップ `metaMap[assetFullPath] = metaLastWriteTicks` と、アセット本体ファイル `assetMap[assetFullPath] = (assetLastWriteTicks, assetLength)` を構築。
4. `.meta` が存在する各アセットファイルについて：
   * `relPath = Path.GetRelativePath(projectRoot, assetFullPath).Replace('\\', '/')`
   * 拡張子から `AssetKind` を判定：
     * `.cs` → `AssetKind.CSharpScript`
     * `.prefab` → `AssetKind.Prefab`
     * `.unity` → `AssetKind.Scene`
     * `.asset` → `AssetKind.ScriptableObject`
     * その他 (`.asmdef`, `.mat`, `.controller`, `.anim` 等) → `AssetKind.OtherAsset`
   * DB上の `CachedAssetRow` と `(meta_mtime, asset_mtime, asset_size)` がすべて一致する場合は **未変更** としてスキップ。
   * 変更または新規の場合：
     * `MetaFileParser.TryReadGuid(assetFullPath + ".meta", out var guid)` で GUID を取得（先頭256バイトを `RandomAccess.Read` で読み、`guid:` の後ろの32文字を `AssetGuid.TryParseHex` でパース）。
     * `AssetKind` が `Prefab`, `Scene`, `ScriptableObject` のいずれかであれば、`FastUnityYamlScanner.ScanFile(assetFullPath)` を実行して内部の `ScriptAttachments`, `PrefabInstances`, `PrefabModifications`, `UnityEventCalls`, `SerializedFields` を抽出。
5. 単一の `SqliteTransaction` 内で：
   * 削除された `asset_id` 群と更新対象の `asset_id` 群について、子テーブル5つ (`script_attachments`, `prefab_instances`, `prefab_modifications`, `unity_event_calls`, `serialized_fields`) から `DELETE WHERE asset_id = $id` を実行。
   * 削除された `asset_id` を `assets` から `DELETE`。
   * 新規・更新されたアセットを `assets` に `INSERT OR REPLACE`（または `UPDATE`）して `asset_id` を確定し、子テーブル5つへレコードを一括 `INSERT`。
6. トランザクション完了後、`InMemoryAssetGraph.RebuildFromStore(this)` を呼び出してオンメモリグラフを更新。

### 3.2 `InMemoryAssetGraph` (`src/UnityCSharpLSP.AssetServer/Storage/InMemoryAssetGraph.cs`)

SQLite から以下の軽量構造を読み込み、`volatile Snapshot _current` に保持します。

```csharp
public sealed class AssetGraphSnapshot
{
    public required IReadOnlyDictionary<int, AssetNode> AssetsById { get; init; }
    public required IReadOnlyDictionary<AssetGuid, int> AssetIdByGuid { get; init; }
    public required IReadOnlyDictionary<string, int> AssetIdByRelPath { get; init; }
    public required IReadOnlyDictionary<AssetGuid, int[]> ScriptToDirectAssetIds { get; init; }
    public required IReadOnlyDictionary<AssetGuid, int[]> PrefabGuidToInstantiatingAssetIds { get; init; }
}

public readonly record struct AssetNode(
    int AssetId,
    AssetGuid Guid,
    string RelativePath,
    string FullPath,
    AssetKind Kind);
```

#### トランジティブ参照解決アルゴリズム (`ResolveTransitiveUsages(AssetGuid scriptGuid)`)
1. `ScriptToDirectAssetIds.TryGetValue(scriptGuid, out var directIds)` で直接アタッチされている `asset_id` 一覧を取得。
2. `directPrefabs` (`Kind == Prefab`), `directScenes` (`Kind == Scene`), `otherAssets` に分類。
3. BFS キューに `directPrefabs` の `AssetGuid` を投入し、`visitedPrefabGuids` (`HashSet<AssetGuid>`) で循環を防ぎながら `PrefabGuidToInstantiatingAssetIds` を辿る：
   * インスタンス化先が `Kind == Prefab` なら `transitivePrefabs` に追加し、さらにその Prefab の `AssetGuid` を BFS キューへ追加。
   * インスタンス化先が `Kind == Scene` なら `scenes` (`HashSet<int>`) に追加。
4. これにより `(DirectPrefabIds, TransitivePrefabIds, SceneIds, AllPrefabGuidsInChain)` をマイクロ秒単位で返します。

---

## 4. ゼロアロケーション YAML スキャナ & Hierarchy パーサ仕様 (`UnityCSharpLSP.AssetServer.Parsing`)

### 4.1 Unity YAML のフォーマット規約と抽出対象

Unity のテキストシリアライズアセット（`.prefab`, `.unity`, `.asset`）は以下の構造を持ちます。

```yaml
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &100000
GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  serializedVersion: 6
  m_Component:
  - component: {fileID: 100004}
  - component: {fileID: 100014}
  m_Name: Player
--- !u!4 &100004
Transform:
  m_GameObject: {fileID: 100000}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_Children:
  - {fileID: 200004}
  m_Father: {fileID: 0}
  m_RootOrder: 0
--- !u!114 &100014
MonoBehaviour:
  m_GameObject: {fileID: 100000}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: c5c9d31933d241c4b9047a5783f7f380, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  config: {fileID: 11400000, guid: a1b2c3d4e5f60718293a4b5c6d7e8f90, type: 2}
  moveSpeed: 5.5
  onDamaged:
    m_PersistentCalls:
      m_Calls:
      - m_Target: {fileID: 100014}
        m_TargetAssemblyTypeName: Game.Runtime.PlayerController, Game.Runtime
        m_MethodName: ApplyDamage
--- !u!1001 &300000
PrefabInstance:
  m_Modification:
    m_TransformParent: {fileID: 0}
    m_Modifications:
    - target: {fileID: 100014, guid: 99887766554433221100aabbccddeeff, type: 3}
      propertyPath: config
      value: 
      objectReference: {fileID: 11400000, guid: a1b2c3d4e5f60718293a4b5c6d7e8f90, type: 2}
    - target: {fileID: 100014, guid: 99887766554433221100aabbccddeeff, type: 3}
      propertyPath: moveSpeed
      value: 12
      objectReference: {fileID: 0}
  m_SourcePrefab: {fileID: 100100000, guid: 99887766554433221100aabbccddeeff, type: 3}
```

### 4.2 `FastUnityYamlScanner` (`src/UnityCSharpLSP.AssetServer/Parsing/FastUnityYamlScanner.cs`)

#### 入力と出力
* 入力: `string filePath` (または `ReadOnlySpan<byte> utf8Bytes`)
* 出力: `YamlScanResult`
  ```csharp
  public sealed class YamlScanResult
  {
      public bool IsValidYaml { get; set; }
      public List<ScannedScriptAttachment> ScriptAttachments { get; } = new();
      public List<ScannedPrefabInstance> PrefabInstances { get; } = new();
      public List<ScannedPrefabModification> PrefabModifications { get; } = new();
      public List<ScannedUnityEventCall> UnityEventCalls { get; } = new();
      public List<ScannedSerializedField> SerializedFields { get; } = new();
  }
  ```

#### 1パス抽出アルゴリズム
1. ファイル先頭が `%YAML` で始まらない場合は `IsValidYaml = false` を返して即終了。
2. UTF-8 バイト列を行ごとに `ReadOnlySpan<byte>` で切り出しながら 0-based の `lineNumber` をインクリメント。
3. `--- !u!` で始まる行を検出したら、現在のドキュメントコンテキスト (`currentClassId`, `currentFileId`, `isStripped`) を切り替える：
   * `--- !u!<classId> &<fileId>` から `classId` (`int`) と `fileId` (`long`、負数もあり得るため符号付き64bit整数) をパース。行末に `stripped` が付いている場合は `isStripped = true`。
4. **`currentClassId == 1` (`GameObject`) の場合**:
   * `  m_Name: ` で始まる行から GameObject 名を取得し、`gameObjectNames[currentFileId] = name` に記録。
5. **`currentClassId == 114` (`MonoBehaviour`) の場合**:
   * `  m_GameObject: {fileID: <fid>}` から所属 `gameObjectFileId` を取得。
   * `  m_Script: ` 行から `guid: <32hex>` を抽出し (`AssetGuid.TryParseHex`)、`m_Script` の行番号 `lineNumber` を記録。
   * インデントが半角スペース2個（`  <ident>:`）のフィールド行を検査：
     * 以下の Unity 内部予約キーはカスタムシリアライズフィールドから除外する：
       `m_ObjectHideFlags`, `m_CorrespondingSourceObject`, `m_PrefabInstance`, `m_PrefabAsset`, `m_GameObject`, `m_Enabled`, `m_EditorHideFlags`, `m_Script`, `m_Name`, `m_EditorClassIdentifier`, `serializedVersion`
     * それ以外のキー（例: `  config: {fileID: 11400000, guid: ...}` や `  moveSpeed: 5`）は `ScannedSerializedField` として `(currentFileId, fieldName, refGuid, refFileId, lineNumber)` を記録。
   * `m_PersistentCalls:` ブロック内に入った場合：
     * `- m_Target: ` または `  m_Target: ` から `(targetFileId, targetGuid)` を保持し、直後の `  m_MethodName: <methodName>` 行（空でない場合）で `ScannedUnityEventCall` `(currentFileId, targetGuid, targetFileId, methodName, lineNumber)` を記録。
6. **`currentClassId == 1001` (`PrefabInstance`) の場合**:
   * `  m_SourcePrefab: ` 行から `guid: <32hex>` を抽出し、`ScannedPrefabInstance` `(currentFileId, sourcePrefabGuid, lineNumber)` を記録。
   * `    m_Modifications:` セクション内の各要素（`- target: {fileID: <fid>, guid: <guid>...}` と直後の `  propertyPath: <path>`）から：
     * `propertyPath` が `m_LocalPosition.`, `m_LocalRotation.`, `m_LocalScale.`, `m_RootOrder`, `m_LocalEulerAnglesHint.` 等の Transform 標準プロパティでない場合（または `m_Name` 以外のすべてのプロパティ）について、`rootFieldName = ExtractRootFieldName(propertyPath)`（`.` または `[` の手前まで）を切り出し、`ScannedPrefabModification` `(currentFileId, targetGuid, targetFid, rootFieldName, propertyPath, propertyPathLineNumber)` を記録。
7. ファイル末尾到達後、`ScannedScriptAttachment` の `gameObjectFileId` を `gameObjectNames` 辞書で解決して `GameObjectName` を埋める。

### 4.3 `HierarchyBuilder` (`src/UnityCSharpLSP.AssetServer/Parsing/HierarchyBuilder.cs`)

`textDocument/documentSymbol` や `unitycsharp.getSceneOrPrefabHierarchy` が呼ばれた際、対象の `.prefab` または `.unity` ファイル1つをパースしてツリー構造 `AssetHierarchyNodeDto[]` を組み立てます。

1. ファイル内の以下のブロックを収集：
   * `!u!1` (`GameObject`): `fileId`, `m_Name`, `line`
   * `!u!4` (`Transform`) / `!u!224` (`RectTransform`): `fileId`, `m_GameObject`, `m_Father`, `m_Children` (`List<long>`), `m_RootOrder`
   * `!u!114` (`MonoBehaviour`): `fileId`, `m_GameObject`, `m_Script` (`guid`), カスタムフィールドの `key: value` 辞書（`includeSerializedValues == true` の場合）
   * その他のコンポーネント（`!u!20` Camera, `!u!23` MeshRenderer, `!u!54` Rigidbody, `!u!65` BoxCollider 等）: `fileId`, `classId`, YAMLのヘッダ直後の型名（例: `Camera:`, `Rigidbody:`）と `m_GameObject`
   * `!u!1001` (`PrefabInstance`): `fileId`, `m_SourcePrefab` (`guid`), `m_TransformParent`, および `m_Modifications` 内の `propertyPath: m_Name` のオーバーライド値
   * `!u!1660057539` (`SceneRoots`): `m_Roots` (`List<long>` Transform/PrefabInstance fileID リスト)
2. 各 `MonoBehaviour` の `scriptGuid` を `AssetSqliteStore`（または `InMemoryAssetGraph`）で引き、対応する `.cs` ファイルパスとクラス名（`Path.GetFileNameWithoutExtension(scriptPath)`）を `TypeName` に設定（見つからない場合は `"Missing Script"`）。
3. `Transform` の `m_Children`（および `PrefabInstance` の `m_TransformParent`）を辿って親子ツリーを構築。
4. ルート順序は、`SceneRoots` ブロックが存在すればその `m_Roots` 順、なければ `m_Father == 0` のノードを `m_RootOrder` 昇順でソートして返します。

### 4.4 `BuildSettingsParser` (`src/UnityCSharpLSP.AssetServer/Storage/BuildSettingsParser.cs`)

`<ProjectRoot>/ProjectSettings/EditorBuildSettings.asset` を読み込み、`m_Scenes:` リストから `BuildSceneDto[]` を抽出します。

```yaml
  m_Scenes:
  - enabled: 1
    path: Assets/Scenes/SampleScene.unity
    guid: 9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c
```

* `enabled == 1` のシーンには `0, 1, 2...` の連番 `BuildIndex` を割り当て、`enabled == 0` のシーンには `BuildIndex = -1` を設定します。

---

## 5. 常駐データサーバ & クライアント自動スポーン仕様 (`UnityCSharpLSP.AssetServer.Daemon`)

### 5.1 パイプ名と Mutex 名の決定 (`AssetPipeNaming`)

```csharp
using System.Security.Cryptography;
using System.Text;

namespace UnityCSharpLSP.AssetServer.Daemon;

public static class AssetPipeNaming
{
    public static string ComputeProjectHash(string projectRoot)
    {
        var normalized = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToLowerInvariant();
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant(); // 16 hex chars
    }

    public static string GetPipeName(string projectRoot)
        => $"unitycsharplsp-assetd-{ComputeProjectHash(projectRoot)}";

    public static string GetMutexName(string projectRoot)
        => $"Global\\UnityCSharpLSP.AssetDaemon.{ComputeProjectHash(projectRoot)}";
}
```

### 5.2 `AssetDataServiceImpl` (`src/UnityCSharpLSP.AssetServer/Daemon/AssetDataServiceImpl.cs`)

* `IAssetDataService` の実体クラス。`AssetSqliteStore`, `InMemoryAssetGraph`, `AssetFileWatcher` を内部に保持します。
* **重要設計**: `AssetDataServiceImpl` は Named Pipe サーバ (`AssetDaemonHost`) の RPC ターゲットとして使われるだけでなく、**テスト時やパイプ非使用モードでは Frontend LSP から直接インプロセスで注入（DI）して呼び出せる** ようにします。

### 5.3 `AssetDaemonHost` (`src/UnityCSharpLSP.AssetServer/Daemon/AssetDaemonHost.cs`)

* `RunAsync(string projectRoot, TimeSpan idleTimeout, CancellationToken ct)`:
  1. `AssetDataServiceImpl` を初期化し、バックグラウンドタスクで `InitializeAndSyncAsync()` を開始（Named Pipe のリッスン自体はスキャン完了を待たずに即座に開始する）。
  2. `while (!ct.IsCancellationRequested)` ループで `new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)` を生成し、`await serverStream.WaitForConnectionAsync(ct)` で待機。
  3. 接続が確立したらアクティブ接続数をインクリメントし、`HeaderDelimitedMessageHandler` + `JsonRpc` をアタッチして `StartListening()`。クライアント切断 (`jsonRpc.Completion`) 時にアクティブ接続数をデクリメント。
  4. アクティブ接続数が `0` になった時点から `idleTimeout`（デフォルト30分）経過するか、`ShutdownAsync` が呼ばれたらループを抜けてプロセスを終了。

### 5.4 `AssetDaemonClient` & 自動スポーン (`src/UnityCSharpLSP.AssetServer/Daemon/AssetDaemonClient.cs`)

フロントエンドLSP側の `UnityWorkspaceManager` が保持するクライアントラッパーです。

1. **接続・自動起動フロー (`ConnectOrSpawnAsync`)**:
   * まず `NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)` で `ConnectAsync(150)`（150msタイムアウト）を試行。
   * 失敗した場合：
     * `Environment.ProcessPath` が自身の `UnityCSharpLSP.Server` 実行ファイルを指している場合は、`Process.Start`（`CreateNoWindow = true`, `UseShellExecute = false`）で `--asset-daemon --project-root "<projectRoot>"` を起動し、最大3秒間リトライ接続。
     * 万一、テストランナー（`testhost.exe`）からの実行やサンドボックス制約等で別プロセス起動・Named Pipe 接続ができない場合は、**自動的にインプロセスの `AssetDataServiceImpl` にフォールバック**します。これにより、`dotnet test` 実行時や任意の環境で100%確実に動作します。

---

## 6. フロントエンド C# LSP との統合仕様 (`UnityCSharpLSP.Workspace` & `UnityCSharpLSP.Protocol`)

### 6.1 `UnityWorkspaceManager` への統合 (`src/UnityCSharpLSP.Workspace/UnityWorkspaceManager.cs`)

* `UnityWorkspaceManager` に以下のプロパティとヘルパーを追加します：
  ```csharp
  public IAssetDataService AssetService { get; }
  ```
* `UnityWorkspaceManager.Create(string projectRoot, string? explicitEditorPath = null, IAssetDataService? assetService = null)`:
  * `assetService` が渡されなかった場合は、`AssetDaemonClient.CreateOrFallbackInProcess(projectRoot)` を使用。
* **C# シンボル ⇔ ScriptGuid 変換ヘルパー (`UnitySymbolGuidHelper`)**:
  * `TryGetScriptGuid(ISymbol symbol, out string scriptGuid)`:
    1. `symbol` が `INamedTypeSymbol typeSymbol` ならその型、`IFieldSymbol` や `IMethodSymbol` なら `symbol.ContainingType` を取得。
    2. その型の構文宣言ファイルパス (`loc.SourceTree?.FilePath`) を取得し、`<filePath>.meta` から `MetaFileParser.TryReadGuid`（または `AssetService.GetAssetByPathAsync`）で 32桁小文字 hex の `scriptGuid` を取得。
  * `IsSerializableField(IFieldSymbol field)`:
    1. `field.IsStatic || field.IsConst || field.IsReadOnly` なら `false`。
    2. `[NonSerialized]` 属性があれば `false`。
    3. `field.DeclaredAccessibility == Accessibility.Public` なら `true`。
    4. `[SerializeField]` または `[SerializeReference]` 属性があれば `true`。

### 6.2 各 LSP ハンドラの拡張仕様

#### 1. `ReferencesHandler.cs`
* `FindReferences` 内で Roslyn の `SymbolFinder.FindReferencesAsync` を実行した後、`UnitySymbolGuidHelper.TryGetScriptGuid(symbol, out var scriptGuid)` が成功した場合：
  * `symbol is INamedTypeSymbol` → `MemberKind = AssetMemberKind.Class`
  * `symbol is IFieldSymbol field && IsSerializableField(field)` → `MemberKind = AssetMemberKind.Field, MemberName = field.Name`
  * `symbol is IMethodSymbol method` → `MemberKind = AssetMemberKind.Method, MemberName = method.Name`
* `ws.AssetService.FindReferencesAsync(...)` を呼び出し、返ってきた `AssetReferenceLocationDto` を `LspLocation` (`Uri = new Uri(loc.FullPath)`, `Range = (loc.Line, loc.Character)-(loc.Line, loc.EndCharacter)`) に変換して `results` に追加します。

#### 2. `CodeLensHandler.cs`
* ファイル内の `BaseTypeDeclarationSyntax` のうち、`MonoBehaviour` または `ScriptableObject` を継承している型について `scriptGuid` を収集。
* `ws.AssetService.GetScriptReferenceCountsAsync(scriptGuids, ct)` を一括呼び出し。
* `TotalAssetCount > 0` の場合、既存の `"N references"` レンズのタイトルを `"N references (P prefabs, S scenes)"` に拡張する（または `FindReferencesAsync` で取得したアセットロケーションも `locations` 配列に含める）。

#### 3. `RenameHandler.cs` & `CodeActionHandler.cs`
* `RenameHandler.Rename`:
  * リネーム対象 `target.Symbol` がシリアライズ対象フィールド (`IFieldSymbol`) またはメソッド (`IMethodSymbol`) の場合、Roslyn の `CreateWorkspaceEditAsync` で生成した `WorkspaceEdit` に対し、`ws.AssetService.ComputeRenameEditsAsync` で取得した `.prefab` / `.unity` / `.asset` の `TextEdit[]` を `WorkspaceEdit.Changes` にマージします。
* `CodeActionHandler`:
  * カーソル位置が `[SerializeField]` / `public` フィールド宣言上にあり、まだ `[FormerlySerializedAs]` が付いていない場合、`Add [FormerlySerializedAs("<fieldName>")]` の QuickFix / Refactoring CodeAction を提供します。

#### 4. `DocumentLinkHandler.cs` / `HoverHandler.cs` / `DefinitionHandler.cs` / `DocumentSymbolHandler.cs`
* **非C#アセットファイル（`.prefab`, `.unity`, `.asset`）への対応**:
  * これまで `var document = ws.GetDocument(filePath); if (document is null) return null;` で即 return していた箇所で、拡張子が `.prefab`, `.unity`, `.asset` の場合は `ws.AssetService` に処理を委譲します：
    * `DocumentLinkHandler`: `ws.AssetService.GetDocumentLinksAsync(filePath, ct)` を呼び、YAML内の `guid: <32hex>` を対応するアセットファイルの `Uri` へリンク化。また、C# ファイル内の `SceneManager.LoadScene("SceneName")` の文字列リテラルも、一致するビルドシーンの `.unity` ファイルへの `DocumentLink` として追加。
    * `DefinitionHandler`:
      * `.prefab` / `.unity` / `.asset` ファイル上の場合: `ws.AssetService.GetDefinitionAsync(filePath, line, character, ct)` を呼び、`guid:` 上なら対象 `.cs` / `.prefab` へ、シリアライズフィールド行上なら対応する C# クラスのフィールド宣言位置へジャンプ。
      * `.cs` ファイル内の `SceneManager.LoadScene("...")` / `LoadSceneAsync("...")` の文字列リテラル上の場合: `GetBuildScenesAsync` から一致する `.unity` ファイルの先頭 (0, 0) を返す。
    * `HoverHandler`:
      * `.prefab` / `.unity` / `.asset` ファイル上の場合: `ws.AssetService.GetHoverAsync(filePath, line, character, ct)` を呼び出し、さらに `scriptGuid` + `fieldName` が返ってきた場合は Roslyn の Solution から該当 C# シンボルの型・XML コメント・`[Tooltip("...")]` 属性値を引いて Markdown に追記して返す。
    * `DocumentSymbolHandler`:
      * `.prefab` / `.unity` ファイルの場合: `ws.AssetService.GetHierarchyAsync(filePath, maxDepth: 16, includeSerializedValues: false, ct)` を呼び出し、`GameObject` を `SymbolKind.Object`、アタッチされたコンポーネントを子の `SymbolKind.Class`（Detail に `TypeName`）として `DocumentSymbol[]` ツリーに変換して返す。

#### 5. `CompletionHandler.cs` & `DiagnosticsHandler.cs` / `SerializeFieldSuppressor.cs`
* **`SceneManager.LoadScene` 補完**:
  * C# ファイル内で `SceneManager.LoadScene("` または `SceneManager.LoadSceneAsync("` の第1引数文字列リテラル内にある場合、`ws.AssetService.GetBuildScenesAsync(ct)` から有効なシーン名 (`SceneName` および `RelativePath`) を `CompletionItem` (`CompletionItemKind.File`) として提供。
* **`UnityEvent` 参照メソッドの未使用警告 (`IDE0051`) 抑制**:
  * Diagnostic 抑制ロジックで `IDE0051` 等が出た際、対象がメソッドであれば `ws.AssetService.IsMethodReferencedByUnityEventAsync(scriptGuid, method.Name)` を確認して抑制。
* **`.prefab` / `.unity` の Missing Script 診断**:
  * `TextDocumentSyncHandler` / `PullDiagnosticsHandler` で `.prefab` / `.unity` が指定された場合、`ws.AssetService.GetAssetDiagnosticsAsync(filePath, ct)` を呼び出し、Missing Script (`UNITY0001`) 等を LSP `Diagnostic` (`DiagnosticSeverity.Warning`) として返却。

#### 6. `ExecuteCommandHandler.cs` & CLI (`Program.cs`)
* `ExecuteCommandHandler.Commands` に以下の3コマンドを追加：
  1. `"unitycsharp.getSceneOrPrefabHierarchy"`: 引数 `arguments[0]` (`{ path, maxDepth?, includeSerializedValues? }`) を受け取り、`GetHierarchyAsync` の結果を返す。
  2. `"unitycsharp.findSymbolUsagesInAssets"`: 引数 `arguments[0]` (`{ scriptGuid?, symbolName?, memberKind?, memberName?, maxResults? }`) を受け取り、`FindReferencesAsync` の結果を返す。
  3. `"unitycsharp.assetServerStatus"`: `GetStatusAsync` の結果を返す。
* `Program.cs` に以下のオプションを追加：
  * `--asset-daemon`: 常駐データサーバモードで起動。
  * `--project-root <path>`: `--asset-daemon` 時の対象プロジェクトルート。
  * `--daemon-idle-minutes <int>`: デフォルト `30`。

---

## 7. テストフィクスチャ仕様 (`DummyProject`)

既存の `DummyProject` に以下のテスト用アセットを追加し、E2E で Prefab / Scene 解析を検証できるようにします。

1. **`DummyProject/Assets/Prefabs/Player.prefab` & `Player.prefab.meta`**
   * `guid: d1e2f3a4b5c64758697a8b9c0d1e2f30`
   * ルート `GameObject` (`m_Name: Player`, `&100000`) + 子 `GameObject` (`m_Name: WeaponSlot`, `&200000`)
   * `PlayerController` (`guid: c5c9d31933d241c4b9047a5783f7f380`, `&100014`) をアタッチし、`config: {fileID: 11400000, guid: ...}` フィールドおよび `UnityEvent` (`m_MethodName: ApplyDamage`) を記述。
2. **`DummyProject/Assets/Scenes/MainScene.unity` & `MainScene.unity.meta`**
   * `guid: e1f2a3b4c5d64758697a8b9c0d1e2f40`
   * ルート `GameObject` (`m_Name: GameManager`) と、`Player.prefab` (`guid: d1e2f3a4b5c64758697a8b9c0d1e2f30`) を配置する `PrefabInstance` (`!u!1001 &300000`) を含み、`m_Modifications` で `target: {fileID: 100014, guid: d1e2f3a4b5c64758697a8b9c0d1e2f30}`, `propertyPath: config` をオーバーライド。
3. **`DummyProject/ProjectSettings/EditorBuildSettings.asset`**
   * `Assets/Scenes/MainScene.unity` (`enabled: 1`) を登録。

---

## 8. Hancho 実装タスク分割計画 (全10ステップ: Task 07 〜 Task 16)

ワーカーLLMが1ジョブあたり1〜3クラス程度の小さなスコープで確実にビルド・テストを通しながら実装できるよう、以下の **10個の細粒度タスクカード（`07` 〜 `16`）** に分割します。

| Job ID | タスクカードファイル | 実装対象クラス / スコープ | 完了検証テスト |
| :--- | :--- | :--- | :--- |
| **`07-asset-contracts-and-guid`** | `.hancho-work/tasks/07-asset-contracts-and-guid.md` | `UnityCSharpLSP.AssetServer` & `.Tests` プロジェクト新設、`.sln` 追加、`AssetGuid` (128bit値型)、`MetaFileParser`、全 DTO (`AssetDtos.cs`)、`IAssetDataService` 定義、`DummyProject` 用 `.prefab` / `.unity` / `EditorBuildSettings.asset` フィクスチャ配置 | `AssetGuidTests`, `MetaFileParserTests` |
| **`08-fast-yaml-scanner`** | `.hancho-work/tasks/08-fast-yaml-scanner.md` | `FastUnityYamlScanner`（`%YAML` 判定、`!u!1` GameObject名、`!u!114` `m_Script` + シリアライズフィールド + `UnityEvent` `m_MethodName`、`!u!1001` `m_SourcePrefab` + `m_Modifications` `propertyPath`、および生 `guid:` トークン位置スキャン） | `FastUnityYamlScannerTests` |
| **`09-hierarchy-and-buildsettings`** | `.hancho-work/tasks/09-hierarchy-and-buildsettings.md` | `HierarchyBuilder`（GameObject / Transform / RectTransform / SceneRoots / Component / PrefabInstance ツリー構築）および `BuildSettingsParser`（`EditorBuildSettings.asset` 解析） | `HierarchyBuilderTests`, `BuildSettingsParserTests` |
| **`10-sqlite-store-and-graph`** | `.hancho-work/tasks/10-sqlite-store-and-graph.md` | `AssetSqliteStore`（WAL+mmap 6テーブルDDL、`FileSystemEnumerable` 差分 `SyncWorkspaceAsync`、単一ファイル更新/削除、クラス/フィールド/メソッド参照SQLクエリ、Missing Script クエリ）および `InMemoryAssetGraph`（BFS トランジティブ参照解決） | `AssetSqliteStoreTests`, `InMemoryAssetGraphTests` |
| **`11-asset-data-service-impl`** | `.hancho-work/tasks/11-asset-data-service-impl.md` | `AssetDataServiceImpl`（`IAssetDataService` の全16メソッド実装、`ComputeRenameEditsAsync` のYAML行カラム計算、`GetDocumentLinks`/`Hover`/`Definition` 解決）および `AssetFileWatcher`（`Channel<string>` デバウンス監視） | `AssetDataServiceImplTests` |
| **`12-asset-daemon-ipc-and-cli`** | `.hancho-work/tasks/12-asset-daemon-ipc-and-cli.md` | `AssetPipeNaming`、`AssetDaemonHost`（マルチクライアント `NamedPipeServerStream` + `StreamJsonRpc` + Idle タイマー）、`AssetDaemonClient`（接続・自動スポーン・インプロセスフォールバック）、`Program.cs` (`--asset-daemon` オプション) | `AssetDaemonIpcTests` |
| **`13-lsp-csharp-references-codelens`** | `.hancho-work/tasks/13-lsp-csharp-references-codelens.md` | `UnityWorkspaceManager.AssetService` 統合、`UnitySymbolGuidHelper`（C# `ISymbol` ⇔ `scriptGuid` & シリアライズ可能フィールド判定）、`ReferencesHandler`（Prefab/Scene 参照マージ）、`CodeLensHandler`（Prefab/Scene カウント表示） | Protocol Tests (`References`, `CodeLens`) |
| **`14-lsp-rename-codeaction-suppressor`** | `.hancho-work/tasks/14-lsp-rename-codeaction-suppressor.md` | `RenameHandler`（フィールド・メソッドリネーム時の YAML `TextEdit` マージ）、`CodeActionHandler`（`[FormerlySerializedAs]` 付与）、`DiagnosticsHandler` / `SerializeFieldSuppressor`（`UnityEvent` 参照メソッドの `IDE0051` 抑制） | Protocol Tests (`Rename`, `CodeAction`, `Diagnostics`) |
| **`15-lsp-yaml-navigation-symbols`** | `.hancho-work/tasks/15-lsp-yaml-navigation-symbols.md` | `.prefab` / `.unity` / `.asset` ファイル直接操作時の `DocumentLinkHandler`、`DefinitionHandler`、`HoverHandler`（Roslyn の型/XML doc/`[Tooltip]` 結合）、`DocumentSymbolHandler`（Hierarchyアウトライン）、`PullDiagnosticsHandler` (`UNITY0001`) | Protocol Tests (YAML `DocumentLink`, `Hover`, `Definition`, `DocumentSymbol`) |
| **`16-lsp-scene-completion-commands`** | `.hancho-work/tasks/16-lsp-scene-completion-commands.md` | C# 内 `SceneManager.LoadScene` 文字列リテラルの `CompletionHandler` / `DocumentLinkHandler` / `DefinitionHandler`、`ExecuteCommandHandler`（`getSceneOrPrefabHierarchy`, `findSymbolUsagesInAssets`, `assetServerStatus`）、`doc/functions.md` & `README.md` 更新 | Protocol Tests (`LoadScene`, `ExecuteCommand`) |
