using System.Text;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Parsing;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class HierarchyBuilderTests
{
    [Fact]
    public void BuildHierarchy_PlayerPrefab_BuildsTreeWithResolvedScriptAndSerializedFields()
    {
        string prefabPath = GetDummyAssetPath("Assets/Prefabs/Player.prefab");
        AssetGuid playerControllerGuid = AssetGuid.TryParseHex("c5c9d31933d241c4b9047a5783f7f380", out var g)
            ? g
            : throw new InvalidOperationException();

        AssetHierarchyNodeDto[] roots = HierarchyBuilder.BuildHierarchy(
            prefabPath,
            maxDepth: 16,
            includeSerializedValues: true,
            assetResolver: guid => guid == playerControllerGuid
                ? ("Assets/Scripts/PlayerController.cs", "PlayerController")
                : (null, null));

        AssetHierarchyNodeDto player = Assert.Single(roots);
        Assert.Equal(100000L, player.GameObjectFileId);
        Assert.Equal(100004L, player.TransformFileId);
        Assert.Equal("Player", player.Name);
        Assert.Equal(2, player.Line);
        Assert.False(player.IsPrefabInstance);

        Assert.Collection(
            player.Components,
            transform =>
            {
                Assert.Equal(100004L, transform.ComponentFileId);
                Assert.Equal(4, transform.ClassId);
                Assert.Equal("Transform", transform.TypeName);
                Assert.Equal(19, transform.Line);
                Assert.Null(transform.SerializedFields);
            },
            mono =>
            {
                Assert.Equal(100014L, mono.ComponentFileId);
                Assert.Equal(114, mono.ClassId);
                Assert.Equal("PlayerController", mono.TypeName);
                Assert.Equal("c5c9d31933d241c4b9047a5783f7f380", mono.ScriptGuid);
                Assert.Equal("Assets/Scripts/PlayerController.cs", mono.ScriptPath);
                Assert.Equal(36, mono.Line);
                Assert.NotNull(mono.SerializedFields);
                Assert.Equal(
                    "{fileID: 11400000, guid: a1b2c3d4e5f60718293a4b5c6d7e8f90, type: 2}",
                    mono.SerializedFields!["config"]);
                Assert.True(mono.SerializedFields.ContainsKey("onDamaged"));
            });

        AssetHierarchyNodeDto weaponSlot = Assert.Single(player.Children);
        Assert.Equal(200000L, weaponSlot.GameObjectFileId);
        Assert.Equal(200004L, weaponSlot.TransformFileId);
        Assert.Equal("WeaponSlot", weaponSlot.Name);
        Assert.Equal(64, weaponSlot.Line);
        Assert.False(weaponSlot.IsPrefabInstance);
        Assert.Empty(weaponSlot.Children);

        AssetComponentSummaryDto weaponTransform = Assert.Single(weaponSlot.Components);
        Assert.Equal(200004L, weaponTransform.ComponentFileId);
        Assert.Equal("Transform", weaponTransform.TypeName);
    }

    [Fact]
    public void BuildHierarchy_PlayerPrefab_WithoutValuesOrResolver_MarksMissingScriptAndOmitsFields()
    {
        string prefabPath = GetDummyAssetPath("Assets/Prefabs/Player.prefab");

        AssetHierarchyNodeDto[] roots = HierarchyBuilder.BuildHierarchy(
            prefabPath,
            maxDepth: 16,
            includeSerializedValues: false,
            assetResolver: null);

        AssetHierarchyNodeDto player = Assert.Single(roots);
        AssetComponentSummaryDto mono = Assert.Single(player.Components, c => c.ClassId == 114);
        Assert.Equal("Missing Script", mono.TypeName);
        Assert.Equal("c5c9d31933d241c4b9047a5783f7f380", mono.ScriptGuid);
        Assert.Null(mono.ScriptPath);
        Assert.Null(mono.SerializedFields);
    }

    [Fact]
    public void BuildHierarchy_MainScene_BuildsGameManagerAndPrefabInstanceInSceneRootsOrder()
    {
        string scenePath = GetDummyAssetPath("Assets/Scenes/MainScene.unity");
        AssetGuid playerPrefabGuid = AssetGuid.TryParseHex("d1e2f3a4b5c64758697a8b9c0d1e2f30", out var g)
            ? g
            : throw new InvalidOperationException();

        AssetHierarchyNodeDto[] roots = HierarchyBuilder.BuildHierarchy(
            scenePath,
            maxDepth: 16,
            includeSerializedValues: false,
            assetResolver: guid => guid == playerPrefabGuid
                ? ("Assets/Prefabs/Player.prefab", null)
                : (null, null));

        Assert.Collection(
            roots,
            gameManager =>
            {
                Assert.Equal(100000L, gameManager.GameObjectFileId);
                Assert.Equal(100004L, gameManager.TransformFileId);
                Assert.Equal("GameManager", gameManager.Name);
                Assert.Equal(2, gameManager.Line);
                Assert.False(gameManager.IsPrefabInstance);
                Assert.Null(gameManager.SourcePrefabGuid);
                Assert.Null(gameManager.SourcePrefabPath);
                Assert.Empty(gameManager.Children);
            },
            prefabInstance =>
            {
                Assert.Equal(0L, prefabInstance.GameObjectFileId);
                Assert.Equal(300000L, prefabInstance.TransformFileId);
                Assert.Equal("Player", prefabInstance.Name);
                Assert.Equal(34, prefabInstance.Line);
                Assert.True(prefabInstance.IsPrefabInstance);
                Assert.Equal("d1e2f3a4b5c64758697a8b9c0d1e2f30", prefabInstance.SourcePrefabGuid);
                Assert.Equal("Assets/Prefabs/Player.prefab", prefabInstance.SourcePrefabPath);
                Assert.Empty(prefabInstance.Components);
                Assert.Empty(prefabInstance.Children);
            });
    }

    [Fact]
    public void BuildHierarchy_RespectsMaxDepth()
    {
        string prefabPath = GetDummyAssetPath("Assets/Prefabs/Player.prefab");

        AssetHierarchyNodeDto[] depthZero = HierarchyBuilder.BuildHierarchy(prefabPath, maxDepth: 0, includeSerializedValues: false);
        AssetHierarchyNodeDto root = Assert.Single(depthZero);
        Assert.Equal("Player", root.Name);
        Assert.Empty(root.Children);

        AssetHierarchyNodeDto[] negativeDepth = HierarchyBuilder.BuildHierarchy(prefabPath, maxDepth: -1, includeSerializedValues: false);
        Assert.Empty(negativeDepth);
    }

    [Fact]
    public void BuildHierarchyFromBytes_HandlesRectTransformOtherComponentsRootOrderAndNestedPrefabInstance()
    {
        string yaml = string.Join("\n",
        [
            "%YAML 1.1",
            "%TAG !u! tag:unity3d.com,2011:",
            "--- !u!104 &1",
            "RenderSettings:",
            "  m_Fog: 0",
            "--- !u!1 &10",
            "GameObject:",
            "  m_Component:",
            "  - component: {fileID: 11}",
            "  - component: {fileID: 12}",
            "  - component: {fileID: 13}",
            "  m_Name: SecondByDecl_FirstByOrder",
            "--- !u!4 &11",
            "Transform:",
            "  m_GameObject: {fileID: 10}",
            "  m_Children:",
            "  - {fileID: 31}",
            "  m_Father: {fileID: 0}",
            "  m_RootOrder: 1",
            "--- !u!20 &12",
            "Camera:",
            "  m_GameObject: {fileID: 10}",
            "--- !u!65 &13",
            "BoxCollider:",
            "  m_GameObject: {fileID: 10}",
            "--- !u!1 &20",
            "GameObject:",
            "  m_Component:",
            "  - component: {fileID: 21}",
            "  - component: {fileID: 22}",
            "  m_Name: FirstByOrder_UIRoot",
            "--- !u!224 &21",
            "RectTransform:",
            "  m_GameObject: {fileID: 20}",
            "  m_Children: []",
            "  m_Father: {fileID: 0}",
            "  m_RootOrder: 0",
            "--- !u!114 &22",
            "MonoBehaviour:",
            "  m_GameObject: {fileID: 20}",
            "  m_Script: {fileID: 0}",
            "  m_Name: ",
            "  label: \"HUD\"",
            "--- !u!1 &30",
            "GameObject:",
            "  m_Component:",
            "  - component: {fileID: 31}",
            "  - component: {fileID: 32}",
            "  m_Name: ChildMesh",
            "--- !u!4 &31",
            "Transform:",
            "  m_GameObject: {fileID: 30}",
            "  m_Children:",
            "  - {fileID: 40}",
            "  m_Father: {fileID: 11}",
            "  m_RootOrder: 0",
            "--- !u!23 &32",
            "MeshRenderer:",
            "  m_GameObject: {fileID: 30}",
            "--- !u!1001 &40",
            "PrefabInstance:",
            "  m_Modification:",
            "    m_TransformParent: {fileID: 31}",
            "    m_Modifications: []",
            "  m_SourcePrefab: {fileID: 100100000, guid: 11111111222222223333333344444444, type: 3}",
        ]);

        AssetGuid nestedPrefabGuid = AssetGuid.TryParseHex("11111111222222223333333344444444", out var g)
            ? g
            : throw new InvalidOperationException();

        AssetHierarchyNodeDto[] roots = HierarchyBuilder.BuildHierarchyFromBytes(
            Encoding.UTF8.GetBytes(yaml),
            maxDepth: 16,
            includeSerializedValues: true,
            assetResolver: guid => guid == nestedPrefabGuid ? ("Assets/Prefabs/Turret.prefab", null) : (null, null));

        Assert.Collection(
            roots,
            first =>
            {
                Assert.Equal("FirstByOrder_UIRoot", first.Name);
                Assert.Collection(
                    first.Components,
                    rect =>
                    {
                        Assert.Equal(224, rect.ClassId);
                        Assert.Equal("RectTransform", rect.TypeName);
                    },
                    missing =>
                    {
                        Assert.Equal(114, missing.ClassId);
                        Assert.Equal("Missing Script", missing.TypeName);
                        Assert.Null(missing.ScriptGuid);
                        Assert.NotNull(missing.SerializedFields);
                        Assert.Equal("HUD", missing.SerializedFields!["label"]);
                    });
            },
            second =>
            {
                Assert.Equal("SecondByDecl_FirstByOrder", second.Name);
                Assert.Collection(
                    second.Components,
                    tr => Assert.Equal("Transform", tr.TypeName),
                    cam =>
                    {
                        Assert.Equal(20, cam.ClassId);
                        Assert.Equal("Camera", cam.TypeName);
                    },
                    col =>
                    {
                        Assert.Equal(65, col.ClassId);
                        Assert.Equal("BoxCollider", col.TypeName);
                    });

                AssetHierarchyNodeDto child = Assert.Single(second.Children);
                Assert.Equal("ChildMesh", child.Name);
                Assert.Contains(child.Components, c => c.ClassId == 23 && c.TypeName == "MeshRenderer");

                AssetHierarchyNodeDto nestedPi = Assert.Single(child.Children);
                Assert.True(nestedPi.IsPrefabInstance);
                Assert.Equal("Turret", nestedPi.Name);
                Assert.Equal("Assets/Prefabs/Turret.prefab", nestedPi.SourcePrefabPath);
            });
    }

    [Fact]
    public void BuildHierarchyFromBytes_SceneRootsOverridesRootOrderAndGuardsAgainstCycles()
    {
        string yaml = string.Join("\n",
        [
            "%YAML 1.1",
            "%TAG !u! tag:unity3d.com,2011:",
            "--- !u!1 &10",
            "GameObject:",
            "  m_Name: Alpha",
            "--- !u!4 &11",
            "Transform:",
            "  m_GameObject: {fileID: 10}",
            "  m_Children:",
            "  - {fileID: 11}",
            "  m_Father: {fileID: 0}",
            "  m_RootOrder: 0",
            "--- !u!1 &20",
            "GameObject:",
            "  m_Name: Beta",
            "--- !u!4 &21",
            "Transform:",
            "  m_GameObject: {fileID: 20}",
            "  m_Children: []",
            "  m_Father: {fileID: 0}",
            "  m_RootOrder: 1",
            "--- !u!1660057539 &9223372036854775807",
            "SceneRoots:",
            "  m_Roots:",
            "  - {fileID: 21}",
            "  - {fileID: 11}",
        ]);

        AssetHierarchyNodeDto[] roots = HierarchyBuilder.BuildHierarchyFromBytes(
            Encoding.UTF8.GetBytes(yaml),
            maxDepth: 16,
            includeSerializedValues: false);

        Assert.Collection(
            roots,
            first => Assert.Equal("Beta", first.Name),
            second =>
            {
                Assert.Equal("Alpha", second.Name);
                Assert.Empty(second.Children);
            });
    }

    [Fact]
    public void BuildHierarchy_InvalidOrMissingFile_ReturnsEmpty()
    {
        Assert.Empty(HierarchyBuilder.BuildHierarchy(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.prefab"),
            maxDepth: 16,
            includeSerializedValues: false));

        Assert.Empty(HierarchyBuilder.BuildHierarchyFromBytes(
            [0x01, 0x02, 0x03],
            maxDepth: 16,
            includeSerializedValues: false));
    }

    private static string GetDummyAssetPath(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "DummyProject");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(candidate, relativePath.Replace('/', Path.DirectorySeparatorChar));
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DummyProject from test base directory.");
    }
}
