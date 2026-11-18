using System.Text;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class InMemoryAssetGraphTests
{
    [Fact]
    public async Task ResolveTransitiveUsages_MultiLevelVariantsAndDirectScene_ResolvesWithoutDuplicates()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "Proj");
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");

        const string scriptGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string basePrefabGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string variant1Guid = "cccccccccccccccccccccccccccccccc";
        const string variant2Guid = "dddddddddddddddddddddddddddddddd";
        const string scene1Guid = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string scene2Guid = "ffffffffffffffffffffffffffffffff";
        const string directSceneGuid = "12345678123456781234567812345678";

        WriteAsset(projectRoot, "Assets/Scripts/Enemy.cs", scriptGuid, "public class Enemy {}");
        WriteAsset(
            projectRoot,
            "Assets/Prefabs/EnemyBase.prefab",
            basePrefabGuid,
            $"%YAML 1.1\n--- !u!114 &10\nMonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}\n");
        WriteAsset(
            projectRoot,
            "Assets/Prefabs/EnemyVariant1.prefab",
            variant1Guid,
            $"%YAML 1.1\n--- !u!1001 &20\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {basePrefabGuid}, type: 3}}\n");
        WriteAsset(
            projectRoot,
            "Assets/Prefabs/EnemyVariant2.prefab",
            variant2Guid,
            $"%YAML 1.1\n--- !u!1001 &30\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {variant1Guid}, type: 3}}\n");
        WriteAsset(
            projectRoot,
            "Assets/Scenes/Stage1.unity",
            scene1Guid,
            $"%YAML 1.1\n--- !u!1001 &40\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {variant2Guid}, type: 3}}\n");
        WriteAsset(
            projectRoot,
            "Assets/Scenes/Stage2.unity",
            scene2Guid,
            $"%YAML 1.1\n--- !u!1001 &50\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {basePrefabGuid}, type: 3}}\n--- !u!1001 &51\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {variant1Guid}, type: 3}}\n");
        WriteAsset(
            projectRoot,
            "Assets/Scenes/DirectOnly.unity",
            directSceneGuid,
            $"%YAML 1.1\n--- !u!114 &60\nMonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}\n");

        using var store = new AssetSqliteStore(projectRoot, dbPath);
        await store.SyncWorkspaceAsync();

        Assert.True(AssetGuid.TryParseHex(scriptGuid, out AssetGuid parsedScriptGuid));
        TransitiveUsageResult usage = store.Graph.ResolveTransitiveUsages(parsedScriptGuid);

        Assert.Single(usage.DirectPrefabIds);
        Assert.Equal(2, usage.TransitivePrefabIds.Length);
        Assert.Equal(3, usage.SceneIds.Length);
        Assert.Equal(3, usage.AllPrefabGuidsInChain.Length);

        ScriptReferenceCountDto counts = Assert.Single(store.Graph.GetScriptReferenceCounts([scriptGuid]));
        Assert.Equal(1, counts.DirectPrefabCount);
        Assert.Equal(2, counts.TransitivePrefabCount);
        Assert.Equal(3, counts.SceneCount);
        Assert.Equal(6, counts.TotalAssetCount);

        AssetReferenceResultDto refs = store.QueryClassReferences(
            parsedScriptGuid,
            usage.AllPrefabGuidsInChain,
            includeTransitiveScenes: true,
            maxResults: 100);
        // 2 direct attachments (EnemyBase + DirectOnly) + 5 PrefabInstance placements (Variant1, Variant2, Stage1, Stage2 x2)
        Assert.Equal(7, refs.Locations.Length);
    }

    [Fact]
    public async Task ResolveTransitiveUsages_CyclicPrefabInstantiation_TerminatesWithoutInfiniteLoop()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "Proj");
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");

        const string scriptGuid = "11111111222222223333333344444444";
        const string prefabAGuid = "aaaa1111aaaa1111aaaa1111aaaa1111";
        const string prefabBGuid = "bbbb2222bbbb2222bbbb2222bbbb2222";
        const string sceneGuid = "cccc3333cccc3333cccc3333cccc3333";

        WriteAsset(projectRoot, "Assets/Scripts/Node.cs", scriptGuid, "public class Node {}");
        WriteAsset(
            projectRoot,
            "Assets/Prefabs/PrefabA.prefab",
            prefabAGuid,
            $"%YAML 1.1\n--- !u!114 &10\nMonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}\n--- !u!1001 &11\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {prefabBGuid}, type: 3}}\n");
        WriteAsset(
            projectRoot,
            "Assets/Prefabs/PrefabB.prefab",
            prefabBGuid,
            $"%YAML 1.1\n--- !u!1001 &20\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {prefabAGuid}, type: 3}}\n");
        WriteAsset(
            projectRoot,
            "Assets/Scenes/CycleScene.unity",
            sceneGuid,
            $"%YAML 1.1\n--- !u!1001 &30\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {prefabBGuid}, type: 3}}\n");

        using var store = new AssetSqliteStore(projectRoot, dbPath);
        await store.SyncWorkspaceAsync();

        Assert.True(AssetGuid.TryParseHex(scriptGuid, out AssetGuid parsedScriptGuid));
        TransitiveUsageResult usage = store.Graph.ResolveTransitiveUsages(parsedScriptGuid);

        Assert.Single(usage.DirectPrefabIds);
        Assert.Single(usage.TransitivePrefabIds);
        Assert.Single(usage.SceneIds);
        Assert.Equal(2, usage.AllPrefabGuidsInChain.Length);
    }

    private static void WriteAsset(string projectRoot, string relPath, string guidHex, string content)
    {
        string fullPath = Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(fullPath + ".meta", $"fileFormatVersion: 2\nguid: {guidHex}\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        public string Path { get; }

        public TempDirectoryScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"UnityCSharpLSP_GraphTests_{Guid.NewGuid():N}");
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
