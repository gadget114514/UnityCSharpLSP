using System.Text;
using Microsoft.Data.Sqlite;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class AssetSqliteStoreTests
{
    private static readonly AssetGuid PlayerControllerGuid = ParseGuid("c5c9d31933d241c4b9047a5783f7f380");
    private static readonly AssetGuid PlayerPrefabGuid = ParseGuid("d1e2f3a4b5c64758697a8b9c0d1e2f30");
    private static readonly AssetGuid MainSceneGuid = ParseGuid("e1f2a3b4c5d64758697a8b9c0d1e2f40");

    [Fact]
    public async Task SyncWorkspaceAsync_DummyProject_IndexesAllAssetsAndSkipsUnchangedOnSecondRun()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");
        string projectRoot = GetDummyProjectRoot();

        using (var store = new AssetSqliteStore(projectRoot, dbPath))
        {
            AssetSyncResult firstSync = await store.SyncWorkspaceAsync();
            Assert.Equal(10, firstSync.TotalAssets);
            Assert.Equal(10, firstSync.UpdatedOrAdded);
            Assert.Equal(0, firstSync.Removed);
            Assert.Equal(0, firstSync.SkippedUnchanged);

            AssetStatusCounts counts = store.GetStatusCounts();
            Assert.Equal(10, counts.TotalAssets);
            Assert.Equal(3, counts.TotalScripts);
            Assert.Equal(1, counts.TotalPrefabs);
            Assert.Equal(1, counts.TotalScenes);

            AssetEntryDto? scriptByGuid = store.GetAssetByGuid(PlayerControllerGuid);
            Assert.NotNull(scriptByGuid);
            Assert.Equal("Assets/Scripts/PlayerController.cs", scriptByGuid.RelativePath);
            Assert.Equal(AssetKind.CSharpScript, scriptByGuid.Kind);

            AssetEntryDto? prefabByRelPath = store.GetAssetByPath("Assets/Prefabs/Player.prefab");
            Assert.NotNull(prefabByRelPath);
            Assert.Equal(PlayerPrefabGuid.ToHexString(), prefabByRelPath.Guid);
            Assert.Equal(AssetKind.Prefab, prefabByRelPath.Kind);

            string sceneFullPath = Path.Combine(projectRoot, "Assets", "Scenes", "MainScene.unity");
            AssetEntryDto? sceneByFullPath = store.GetAssetByPath(sceneFullPath);
            Assert.NotNull(sceneByFullPath);
            Assert.Equal(MainSceneGuid.ToHexString(), sceneByFullPath.Guid);
            Assert.Equal(AssetKind.Scene, sceneByFullPath.Kind);

            AssetSyncResult secondSync = await store.SyncWorkspaceAsync();
            Assert.Equal(10, secondSync.TotalAssets);
            Assert.Equal(0, secondSync.UpdatedOrAdded);
            Assert.Equal(0, secondSync.Removed);
            Assert.Equal(10, secondSync.SkippedUnchanged);
        }

        // Reopening existing DB should preserve indexed state and rebuild in-memory graph immediately.
        using (var reopened = new AssetSqliteStore(projectRoot, dbPath))
        {
            ScriptReferenceCountDto[] counts = reopened.Graph.GetScriptReferenceCounts([PlayerControllerGuid.ToHexString()]);
            ScriptReferenceCountDto playerCounts = Assert.Single(counts);
            Assert.Equal(1, playerCounts.DirectPrefabCount);
            Assert.Equal(1, playerCounts.SceneCount);

            AssetSyncResult warmSync = await reopened.SyncWorkspaceAsync();
            Assert.Equal(0, warmSync.UpdatedOrAdded);
            Assert.Equal(10, warmSync.SkippedUnchanged);
        }
    }

    [Fact]
    public async Task Graph_GetScriptReferenceCounts_DummyProject_ReturnsExpectedCountsForPlayerController()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");

        using var store = new AssetSqliteStore(GetDummyProjectRoot(), dbPath);
        await store.SyncWorkspaceAsync();

        ScriptReferenceCountDto[] results = store.Graph.GetScriptReferenceCounts(
        [
            PlayerControllerGuid.ToHexString(),
            "00000000000000000000000000000000",
            "not-a-guid",
        ]);

        Assert.Equal(3, results.Length);

        Assert.Equal(PlayerControllerGuid.ToHexString(), results[0].ScriptGuid);
        Assert.Equal(1, results[0].DirectPrefabCount);
        Assert.Equal(0, results[0].TransitivePrefabCount);
        Assert.Equal(1, results[0].SceneCount);
        Assert.Equal(2, results[0].TotalAssetCount);

        Assert.Equal(0, results[1].TotalAssetCount);
        Assert.Equal(0, results[2].TotalAssetCount);
    }

    [Fact]
    public async Task GetPrefabModificationsByTarget_DummyProject_FindsOverrideInScene()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");

        using var store = new AssetSqliteStore(GetDummyProjectRoot(), dbPath);
        await store.SyncWorkspaceAsync();

        PrefabOverrideModification[] mods = store.GetPrefabModificationsByTarget(PlayerPrefabGuid, 100014);
        PrefabOverrideModification mod = Assert.Single(mods);
        Assert.Equal("config", mod.PropertyPath);
        Assert.Equal("config", mod.RootFieldName);
        Assert.Equal(43, mod.LineNumber);
        Assert.Equal("Assets/Scenes/MainScene.unity", mod.OwningAssetRelativePath);
        Assert.Equal(PlayerPrefabGuid, mod.TargetGuid);
        Assert.Equal(100014, mod.TargetFileId);

        Assert.Empty(store.GetPrefabModificationsByTarget(PlayerPrefabGuid, 999999999));
        Assert.Empty(store.GetPrefabModificationsByTarget(AssetGuid.Zero, 100014));
    }

    [Fact]
    public async Task QueryMethods_DummyProject_ReturnClassFieldAndMethodReferences()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");

        using var store = new AssetSqliteStore(GetDummyProjectRoot(), dbPath);
        await store.SyncWorkspaceAsync();

        TransitiveUsageResult usage = store.Graph.ResolveTransitiveUsages(PlayerControllerGuid);

        // Class references (including transitive scenes)
        AssetReferenceResultDto classRefs = store.QueryClassReferences(
            PlayerControllerGuid,
            usage.AllPrefabGuidsInChain,
            includeTransitiveScenes: true,
            maxResults: 500);

        Assert.False(classRefs.Truncated);
        Assert.Collection(
            classRefs.Locations,
            prefabLoc =>
            {
                Assert.Equal("Assets/Prefabs/Player.prefab", prefabLoc.RelativePath);
                Assert.Equal(AssetKind.Prefab, prefabLoc.AssetKind);
                Assert.Equal(45, prefabLoc.Line);
                Assert.Equal("Player", prefabLoc.GameObjectName);
                Assert.False(prefabLoc.IsOverride);
            },
            sceneLoc =>
            {
                Assert.Equal("Assets/Scenes/MainScene.unity", sceneLoc.RelativePath);
                Assert.Equal(AssetKind.Scene, sceneLoc.AssetKind);
                Assert.Equal(54, sceneLoc.Line);
                Assert.False(sceneLoc.IsOverride);
            });

        // Class references without transitive scenes
        AssetReferenceResultDto directOnly = store.QueryClassReferences(
            PlayerControllerGuid,
            usage.AllPrefabGuidsInChain,
            includeTransitiveScenes: false,
            maxResults: 500);
        AssetReferenceLocationDto onlyPrefab = Assert.Single(directOnly.Locations);
        Assert.Equal("Assets/Prefabs/Player.prefab", onlyPrefab.RelativePath);

        // Truncation check
        AssetReferenceResultDto truncatedClassRefs = store.QueryClassReferences(
            PlayerControllerGuid,
            usage.AllPrefabGuidsInChain,
            includeTransitiveScenes: true,
            maxResults: 1);
        Assert.True(truncatedClassRefs.Truncated);
        Assert.Single(truncatedClassRefs.Locations);

        // Field references ("config" in Player.prefab + MainScene.unity override)
        AssetReferenceResultDto fieldRefs = store.QueryFieldReferences(PlayerControllerGuid, "config", maxResults: 500);
        Assert.False(fieldRefs.Truncated);
        Assert.Collection(
            fieldRefs.Locations,
            defLoc =>
            {
                Assert.Equal("Assets/Prefabs/Player.prefab", defLoc.RelativePath);
                Assert.Equal(AssetKind.Prefab, defLoc.AssetKind);
                Assert.Equal(48, defLoc.Line);
                Assert.Equal("Player", defLoc.GameObjectName);
                Assert.Equal("config", defLoc.PropertyPath);
                Assert.False(defLoc.IsOverride);
            },
            overrideLoc =>
            {
                Assert.Equal("Assets/Scenes/MainScene.unity", overrideLoc.RelativePath);
                Assert.Equal(AssetKind.Scene, overrideLoc.AssetKind);
                Assert.Equal(43, overrideLoc.Line);
                Assert.Equal("Player", overrideLoc.GameObjectName);
                Assert.Equal("config", overrideLoc.PropertyPath);
                Assert.True(overrideLoc.IsOverride);
            });

        // Method references ("ApplyDamage" via UnityEvent)
        AssetReferenceResultDto methodRefs = store.QueryMethodReferences(PlayerControllerGuid, "ApplyDamage", maxResults: 500);
        Assert.False(methodRefs.Truncated);
        AssetReferenceLocationDto methodLoc = Assert.Single(methodRefs.Locations);
        Assert.Equal("Assets/Prefabs/Player.prefab", methodLoc.RelativePath);
        Assert.Equal(AssetKind.Prefab, methodLoc.AssetKind);
        Assert.Equal(54, methodLoc.Line);
        Assert.Equal("Player", methodLoc.GameObjectName);
        Assert.Equal("ApplyDamage", methodLoc.PropertyPath);
        Assert.False(methodLoc.IsOverride);

        Assert.True(store.IsMethodReferencedByUnityEvent(PlayerControllerGuid, "ApplyDamage"));
        Assert.False(store.IsMethodReferencedByUnityEvent(PlayerControllerGuid, "DoesNotExist"));
    }

    [Fact]
    public async Task GetMissingScriptAttachments_ReturnsOnlyAttachmentsMissingFromAssetsTable()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");

        using var store = new AssetSqliteStore(GetDummyProjectRoot(), dbPath);
        await store.SyncWorkspaceAsync();

        AssetEntryDto? playerPrefab = store.GetAssetByPath("Assets/Prefabs/Player.prefab");
        Assert.NotNull(playerPrefab);
        Assert.Empty(store.GetMissingScriptAttachments(playerPrefab.AssetId));

        AssetEntryDto? volumeProfile = store.GetAssetByPath("Assets/DefaultVolumeProfile.asset");
        Assert.NotNull(volumeProfile);
        MissingScriptAttachment missing = Assert.Single(store.GetMissingScriptAttachments(volumeProfile.AssetId));
        Assert.Equal("d7fd9488000d3734a9e00ee676215985", missing.ScriptGuid.ToHexString());
        Assert.Equal(11, missing.LineNumber);
    }

    [Fact]
    public async Task UpdateSingleAssetAsync_And_RemoveSingleAssetAsync_UpdateDatabaseAndGraphIncrementally()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "UnityProj");
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");

        const string heroScriptGuid = "11111111111111111111111111111111";
        const string heroPrefabGuid = "22222222222222222222222222222222";
        const string bossPrefabGuid = "33333333333333333333333333333333";
        const string levelSceneGuid = "44444444444444444444444444444444";

        WriteAssetWithMeta(projectRoot, "Assets/Scripts/Hero.cs", heroScriptGuid, "public class Hero {}");
        WriteAssetWithMeta(
            projectRoot,
            "Assets/Prefabs/Hero.prefab",
            heroPrefabGuid,
            BuildMonoBehaviourPrefabYaml("Hero", heroScriptGuid, "health", "100", "OnDie"));
        WriteAssetWithMeta(
            projectRoot,
            "Assets/Scenes/Level1.unity",
            levelSceneGuid,
            BuildSceneWithPrefabInstanceYaml(heroPrefabGuid, "health"));

        // Files under ignored directories must not be indexed.
        WriteAssetWithMeta(
            projectRoot,
            "Library/Ignored/ShouldIgnore.prefab",
            "55555555555555555555555555555555",
            BuildMonoBehaviourPrefabYaml("Ignored", heroScriptGuid, "health", "1", "OnDie"));
        WriteAssetWithMeta(
            projectRoot,
            ".git/Ignored.cs",
            "66666666666666666666666666666666",
            "public class GitIgnored {}");

        using var store = new AssetSqliteStore(projectRoot, dbPath);
        AssetSyncResult sync = await store.SyncWorkspaceAsync();
        Assert.Equal(3, sync.TotalAssets);
        Assert.Null(store.GetAssetByPath("Library/Ignored/ShouldIgnore.prefab"));

        ScriptReferenceCountDto initialCount = Assert.Single(store.Graph.GetScriptReferenceCounts([heroScriptGuid]));
        Assert.Equal(1, initialCount.DirectPrefabCount);
        Assert.Equal(1, initialCount.SceneCount);

        // Incremental add via UpdateSingleAssetAsync
        string bossPrefabPath = WriteAssetWithMeta(
            projectRoot,
            "Assets/Prefabs/Boss.prefab",
            bossPrefabGuid,
            BuildMonoBehaviourPrefabYaml("Boss", heroScriptGuid, "health", "500", "OnDie"));
        await store.UpdateSingleAssetAsync(bossPrefabPath);

        Assert.NotNull(store.GetAssetByPath("Assets/Prefabs/Boss.prefab"));
        ScriptReferenceCountDto afterAdd = Assert.Single(store.Graph.GetScriptReferenceCounts([heroScriptGuid]));
        Assert.Equal(2, afterAdd.DirectPrefabCount);
        Assert.Equal(1, afterAdd.SceneCount);

        // Incremental modify (script detached) via .meta or asset path
        WriteAssetWithMeta(
            projectRoot,
            "Assets/Prefabs/Boss.prefab",
            bossPrefabGuid,
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &100000\nGameObject:\n  m_Name: Boss\n");
        await store.UpdateSingleAssetAsync(bossPrefabPath + ".meta");

        ScriptReferenceCountDto afterDetach = Assert.Single(store.Graph.GetScriptReferenceCounts([heroScriptGuid]));
        Assert.Equal(1, afterDetach.DirectPrefabCount);
        Assert.Equal(1, afterDetach.SceneCount);

        // Incremental remove
        string levelScenePath = Path.Combine(projectRoot, "Assets", "Scenes", "Level1.unity");
        File.Delete(levelScenePath);
        File.Delete(levelScenePath + ".meta");
        await store.RemoveSingleAssetAsync(levelScenePath);

        Assert.Null(store.GetAssetByPath("Assets/Scenes/Level1.unity"));
        ScriptReferenceCountDto afterRemove = Assert.Single(store.Graph.GetScriptReferenceCounts([heroScriptGuid]));
        Assert.Equal(1, afterRemove.DirectPrefabCount);
        Assert.Equal(0, afterRemove.SceneCount);

        AssetReferenceResultDto fieldRefsAfterRemove = store.QueryFieldReferences(ParseGuid(heroScriptGuid), "health", 100);
        AssetReferenceLocationDto singleFieldRef = Assert.Single(fieldRefsAfterRemove.Locations);
        Assert.Equal("Assets/Prefabs/Hero.prefab", singleFieldRef.RelativePath);
    }

    [Fact]
    public async Task Constructor_MismatchedSchemaVersion_RebuildsDatabaseCleanly()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");
        string projectRoot = GetDummyProjectRoot();

        using (var store = new AssetSqliteStore(projectRoot, dbPath))
        {
            await store.SyncWorkspaceAsync();
            Assert.Equal(10, store.GetStatusCounts().TotalAssets);
        }

        // Corrupt schema_meta version to simulate an older schema.
        string connStr = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        using (var conn = new SqliteConnection(connStr))
        {
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE schema_meta SET version = 999;";
            cmd.ExecuteNonQuery();
        }

        using (var rebuilt = new AssetSqliteStore(projectRoot, dbPath))
        {
            Assert.Equal(0, rebuilt.GetStatusCounts().TotalAssets);
            await rebuilt.SyncWorkspaceAsync();
            Assert.Equal(10, rebuilt.GetStatusCounts().TotalAssets);
        }
    }

    private static string WriteAssetWithMeta(string projectRoot, string relPath, string guidHex, string content)
    {
        string fullPath = Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            fullPath + ".meta",
            $"fileFormatVersion: 2\nguid: {guidHex}\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fullPath;
    }

    private static string BuildMonoBehaviourPrefabYaml(
        string goName,
        string scriptGuid,
        string fieldName,
        string fieldValue,
        string methodName)
    {
        return string.Join("\n",
        [
            "%YAML 1.1",
            "%TAG !u! tag:unity3d.com,2011:",
            "--- !u!1 &100000",
            "GameObject:",
            $"  m_Name: {goName}",
            "--- !u!114 &100014",
            "MonoBehaviour:",
            "  m_GameObject: {fileID: 100000}",
            $"  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}",
            $"  {fieldName}: {fieldValue}",
            "  onHit:",
            "    m_PersistentCalls:",
            "      m_Calls:",
            "      - m_Target: {fileID: 100014}",
            $"        m_MethodName: {methodName}",
        ]);
    }

    private static string BuildSceneWithPrefabInstanceYaml(string sourcePrefabGuid, string propertyPath)
    {
        return string.Join("\n",
        [
            "%YAML 1.1",
            "%TAG !u! tag:unity3d.com,2011:",
            "--- !u!1001 &300000",
            "PrefabInstance:",
            "  m_Modification:",
            "    m_Modifications:",
            $"    - target: {{fileID: 100014, guid: {sourcePrefabGuid}, type: 3}}",
            $"      propertyPath: {propertyPath}",
            "      value: 999",
            $"  m_SourcePrefab: {{fileID: 100100000, guid: {sourcePrefabGuid}, type: 3}}",
        ]);
    }

    private static AssetGuid ParseGuid(string hex)
    {
        Assert.True(AssetGuid.TryParseHex(hex, out AssetGuid guid));
        return guid;
    }

    private static string GetDummyProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "DummyProject");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DummyProject from test base directory.");
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        public string Path { get; }

        public TempDirectoryScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"UnityCSharpLSP_StoreTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
