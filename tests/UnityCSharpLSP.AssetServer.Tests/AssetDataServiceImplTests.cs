using System.Text;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Daemon;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class AssetDataServiceImplTests
{
    private const string PlayerControllerGuid = "c5c9d31933d241c4b9047a5783f7f380";
    private const string PlayerPrefabGuid = "d1e2f3a4b5c64758697a8b9c0d1e2f30";
    private const string MainSceneGuid = "e1f2a3b4c5d64758697a8b9c0d1e2f40";

    [Fact]
    public async Task LifecycleAndMetadataMethods_DummyProject_ReturnExpectedResults()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");
        string projectRoot = GetDummyProjectRoot();

        using var service = new AssetDataServiceImpl(projectRoot, dbPath, enableFileWatcher: false);

        DaemonHandshakeResponse initialHandshake = await service.HandshakeAsync(new DaemonHandshakeRequest
        {
            ClientVersion = "1.0.0",
            ClientPid = 1234,
            ProjectRoot = projectRoot,
        });
        Assert.Equal("Indexing", initialHandshake.State);
        Assert.Equal(Environment.ProcessId, initialHandshake.DaemonPid);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)), initialHandshake.ProjectRoot);

        await service.InitializeAndSyncAsync();

        DaemonHandshakeResponse readyHandshake = await service.HandshakeAsync(new DaemonHandshakeRequest());
        Assert.Equal("Ready", readyHandshake.State);

        DaemonStatusResponse status = await service.GetStatusAsync();
        Assert.Equal("Ready", status.State);
        Assert.Equal(10, status.TotalAssets);
        Assert.Equal(3, status.TotalScripts);
        Assert.Equal(1, status.TotalPrefabs);
        Assert.Equal(1, status.TotalScenes);
        Assert.Equal(dbPath, status.DatabasePath);

        AssetEntryDto? byGuid = await service.GetAssetByGuidAsync(PlayerControllerGuid);
        Assert.NotNull(byGuid);
        Assert.Equal("Assets/Scripts/PlayerController.cs", byGuid.RelativePath);
        Assert.Equal(AssetKind.CSharpScript, byGuid.Kind);

        Assert.Null(await service.GetAssetByGuidAsync("invalid-guid"));

        AssetEntryDto? byPath = await service.GetAssetByPathAsync("Assets/Prefabs/Player.prefab");
        Assert.NotNull(byPath);
        Assert.Equal(PlayerPrefabGuid, byPath.Guid);
        Assert.Equal(AssetKind.Prefab, byPath.Kind);

        ScriptReferenceCountDto[] refCounts = await service.GetScriptReferenceCountsAsync([PlayerControllerGuid]);
        ScriptReferenceCountDto playerCount = Assert.Single(refCounts);
        Assert.Equal(1, playerCount.DirectPrefabCount);
        Assert.Equal(0, playerCount.TransitivePrefabCount);
        Assert.Equal(1, playerCount.SceneCount);
        Assert.Equal(2, playerCount.TotalAssetCount);

        BuildSceneDto[] buildScenes = await service.GetBuildScenesAsync();
        BuildSceneDto mainScene = Assert.Single(buildScenes);
        Assert.Equal(0, mainScene.BuildIndex);
        Assert.True(mainScene.Enabled);
        Assert.Equal("MainScene", mainScene.SceneName);
        Assert.Equal("Assets/Scenes/MainScene.unity", mainScene.RelativePath);
        Assert.Equal(MainSceneGuid, mainScene.Guid);

        DaemonStatusResponse rescanStatus = await service.RescanAsync();
        Assert.Equal("Ready", rescanStatus.State);
        Assert.Equal(10, rescanStatus.TotalAssets);

        bool shutdownRaised = false;
        service.ShutdownRequested += (_, _) => shutdownRaised = true;
        await service.ShutdownAsync();
        Assert.True(shutdownRaised);
    }

    [Fact]
    public async Task FindReferencesAsync_And_IsMethodReferencedByUnityEventAsync_DummyProject_FindClassFieldAndMethod()
    {
        using var tempDir = new TempDirectoryScope();
        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(GetDummyProjectRoot(), tempDir.Path);

        AssetReferenceResultDto classRefs = await service.FindReferencesAsync(new AssetReferenceQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Class,
            IncludeTransitiveScenes = true,
            MaxResults = 500,
        });
        Assert.False(classRefs.Truncated);
        Assert.Collection(
            classRefs.Locations,
            prefabLoc =>
            {
                Assert.Equal("Assets/Prefabs/Player.prefab", prefabLoc.RelativePath);
                Assert.Equal(AssetKind.Prefab, prefabLoc.AssetKind);
                Assert.Equal(45, prefabLoc.Line);
                Assert.Equal("Player", prefabLoc.GameObjectName);
            },
            sceneLoc =>
            {
                Assert.Equal("Assets/Scenes/MainScene.unity", sceneLoc.RelativePath);
                Assert.Equal(AssetKind.Scene, sceneLoc.AssetKind);
                Assert.Equal(54, sceneLoc.Line);
            });

        AssetReferenceResultDto fieldRefs = await service.FindReferencesAsync(new AssetReferenceQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Field,
            MemberName = "config",
            MaxResults = 500,
        });
        Assert.False(fieldRefs.Truncated);
        Assert.Collection(
            fieldRefs.Locations,
            defLoc =>
            {
                Assert.Equal("Assets/Prefabs/Player.prefab", defLoc.RelativePath);
                Assert.Equal(48, defLoc.Line);
                Assert.Equal("config", defLoc.PropertyPath);
                Assert.False(defLoc.IsOverride);
            },
            overrideLoc =>
            {
                Assert.Equal("Assets/Scenes/MainScene.unity", overrideLoc.RelativePath);
                Assert.Equal(43, overrideLoc.Line);
                Assert.Equal("config", overrideLoc.PropertyPath);
                Assert.True(overrideLoc.IsOverride);
            });

        AssetReferenceResultDto methodRefs = await service.FindReferencesAsync(new AssetReferenceQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Method,
            MemberName = "ApplyDamage",
            MaxResults = 500,
        });
        Assert.False(methodRefs.Truncated);
        AssetReferenceLocationDto methodLoc = Assert.Single(methodRefs.Locations);
        Assert.Equal("Assets/Prefabs/Player.prefab", methodLoc.RelativePath);
        Assert.Equal(54, methodLoc.Line);
        Assert.Equal("ApplyDamage", methodLoc.PropertyPath);
        Assert.False(methodLoc.IsOverride);

        Assert.True(await service.IsMethodReferencedByUnityEventAsync(PlayerControllerGuid, "ApplyDamage"));
        Assert.False(await service.IsMethodReferencedByUnityEventAsync(PlayerControllerGuid, "NonExistentMethod"));
    }

    [Fact]
    public async Task ComputeRenameEditsAsync_DummyProject_ComputesExactLineAndCharacterSpans()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = GetDummyProjectRoot();
        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(projectRoot, tempDir.Path);

        string playerPrefabPath = Path.GetFullPath(Path.Combine(projectRoot, "Assets", "Prefabs", "Player.prefab"));
        string mainScenePath = Path.GetFullPath(Path.Combine(projectRoot, "Assets", "Scenes", "MainScene.unity"));

        // Rename field: config -> gameConfig
        AssetWorkspaceEditDto fieldRename = await service.ComputeRenameEditsAsync(new AssetRenameQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Field,
            OldName = "config",
            NewName = "gameConfig",
        });

        Assert.Equal(2, fieldRename.FileEdits.Length);

        AssetFileEditDto prefabEdit = Assert.Single(fieldRename.FileEdits, f => string.Equals(f.FullPath, playerPrefabPath, StringComparison.OrdinalIgnoreCase));
        AssetTextEditDto prefabTextEdit = Assert.Single(prefabEdit.Edits);
        Assert.Equal(48, prefabTextEdit.Line);
        Assert.Equal(2, prefabTextEdit.StartCharacter);
        Assert.Equal(8, prefabTextEdit.EndCharacter);
        Assert.Equal("gameConfig", prefabTextEdit.NewText);
        Assert.Equal("config", ReadSpanAt(playerPrefabPath, prefabTextEdit));

        AssetFileEditDto sceneEdit = Assert.Single(fieldRename.FileEdits, f => string.Equals(f.FullPath, mainScenePath, StringComparison.OrdinalIgnoreCase));
        AssetTextEditDto sceneTextEdit = Assert.Single(sceneEdit.Edits);
        Assert.Equal(43, sceneTextEdit.Line);
        Assert.Equal(20, sceneTextEdit.StartCharacter);
        Assert.Equal(26, sceneTextEdit.EndCharacter);
        Assert.Equal("gameConfig", sceneTextEdit.NewText);
        Assert.Equal("config", ReadSpanAt(mainScenePath, sceneTextEdit));

        // Rename method: ApplyDamage -> TakeDamage
        AssetWorkspaceEditDto methodRename = await service.ComputeRenameEditsAsync(new AssetRenameQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Method,
            OldName = "ApplyDamage",
            NewName = "TakeDamage",
        });

        AssetFileEditDto methodFileEdit = Assert.Single(methodRename.FileEdits);
        Assert.Equal(playerPrefabPath, methodFileEdit.FullPath, ignoreCase: OperatingSystem.IsWindows());
        AssetTextEditDto methodTextEdit = Assert.Single(methodFileEdit.Edits);
        Assert.Equal(54, methodTextEdit.Line);
        Assert.Equal(22, methodTextEdit.StartCharacter);
        Assert.Equal(33, methodTextEdit.EndCharacter);
        Assert.Equal("TakeDamage", methodTextEdit.NewText);
        Assert.Equal("ApplyDamage", ReadSpanAt(playerPrefabPath, methodTextEdit));
    }

    [Fact]
    public async Task ComputeRenameEditsAsync_NestedAndArrayPropertyPaths_ReplacesLeadingSegmentOnly()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "Proj");
        const string scriptGuid = "11111111111111111111111111111111";
        const string prefabGuid = "22222222222222222222222222222222";
        const string sceneGuid = "33333333333333333333333333333333";

        WriteAssetWithMeta(projectRoot, "Assets/Scripts/Hero.cs", scriptGuid, "public class Hero { public int[] stats; }");
        WriteAssetWithMeta(
            projectRoot,
            "Assets/Prefabs/Hero.prefab",
            prefabGuid,
            string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                $"  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}",
                "  stats: []",
            ]));
        string scenePath = WriteAssetWithMeta(
            projectRoot,
            "Assets/Scenes/TestScene.unity",
            sceneGuid,
            string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1001 &300000",
                "PrefabInstance:",
                "  m_Modification:",
                "    m_Modifications:",
                $"    - target: {{fileID: 100014, guid: {prefabGuid}, type: 3}}",
                "      propertyPath: stats.Array.size",
                "      value: 2",
                $"    - target: {{fileID: 100014, guid: {prefabGuid}, type: 3}}",
                "      propertyPath: stats[0]",
                "      value: 10",
                $"  m_SourcePrefab: {{fileID: 100100000, guid: {prefabGuid}, type: 3}}",
            ]));

        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(projectRoot, Path.Combine(tempDir.Path, "db.sqlite"));
        AssetWorkspaceEditDto edits = await service.ComputeRenameEditsAsync(new AssetRenameQueryDto
        {
            ScriptGuid = scriptGuid,
            MemberKind = AssetMemberKind.Field,
            OldName = "stats",
            NewName = "heroStats",
        });

        AssetFileEditDto sceneFileEdit = Assert.Single(edits.FileEdits, f => string.Equals(f.FullPath, scenePath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, sceneFileEdit.Edits.Length);
        Assert.All(sceneFileEdit.Edits, edit =>
        {
            Assert.Equal("stats", ReadSpanAt(scenePath, edit));
            Assert.Equal("heroStats", edit.NewText);
        });
        Assert.Equal(7, sceneFileEdit.Edits[0].Line);
        Assert.Equal(10, sceneFileEdit.Edits[1].Line);
    }

    [Fact]
    public async Task NavigationMethods_DummyProject_DocumentLinksHoverDefinitionAndHierarchy()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = GetDummyProjectRoot();
        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(projectRoot, tempDir.Path);

        string playerPrefabPath = Path.GetFullPath(Path.Combine(projectRoot, "Assets", "Prefabs", "Player.prefab"));
        string playerScriptPath = Path.GetFullPath(Path.Combine(projectRoot, "Assets", "Scripts", "PlayerController.cs"));
        string mainScenePath = Path.GetFullPath(Path.Combine(projectRoot, "Assets", "Scenes", "MainScene.unity"));

        // DocumentLinks on Player.prefab
        AssetDocumentLinksDto links = await service.GetDocumentLinksAsync("Assets/Prefabs/Player.prefab");
        AssetDocumentLinkItemDto scriptLink = Assert.Single(links.Links);
        Assert.Equal(45, scriptLink.Line);
        Assert.Equal(37, scriptLink.StartCharacter);
        Assert.Equal(69, scriptLink.EndCharacter);
        Assert.Equal(PlayerControllerGuid, scriptLink.TargetGuid);
        Assert.Equal(playerScriptPath, scriptLink.TargetFullPath, ignoreCase: OperatingSystem.IsWindows());

        // Hover on m_Script GUID token (line 45, char 40)
        AssetHoverDto? guidHover = await service.GetHoverAsync(playerPrefabPath, 45, 40);
        Assert.NotNull(guidHover);
        Assert.Contains("PlayerController.cs", guidHover.Markdown);
        Assert.Contains(PlayerControllerGuid, guidHover.Markdown);
        Assert.Equal(playerScriptPath, guidHover.TargetFullPath, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(PlayerControllerGuid, guidHover.ScriptGuid);

        // Hover on serialized field key "config:" (line 48, char 4)
        AssetHoverDto? fieldHover = await service.GetHoverAsync(playerPrefabPath, 48, 4);
        Assert.NotNull(fieldHover);
        Assert.Equal(PlayerControllerGuid, fieldHover.ScriptGuid);
        Assert.Equal("config", fieldHover.FieldName);
        Assert.Equal(playerScriptPath, fieldHover.TargetFullPath, ignoreCase: OperatingSystem.IsWindows());
        Assert.Contains("config", fieldHover.Markdown);

        // Hover on PrefabModification "propertyPath: config" in MainScene.unity (line 43, char 22)
        AssetHoverDto? modHover = await service.GetHoverAsync(mainScenePath, 43, 22);
        Assert.NotNull(modHover);
        Assert.Equal(PlayerControllerGuid, modHover.ScriptGuid);
        Assert.Equal("config", modHover.FieldName);
        Assert.Equal(playerScriptPath, modHover.TargetFullPath, ignoreCase: OperatingSystem.IsWindows());

        // Definition on m_Script GUID token -> PlayerController.cs (0, 0)
        AssetLocationDto? guidDef = await service.GetDefinitionAsync(playerPrefabPath, 45, 40);
        Assert.NotNull(guidDef);
        Assert.Equal(playerScriptPath, guidDef.FullPath, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(0, guidDef.Line);
        Assert.Equal(0, guidDef.Character);

        // Definition on serialized field key "config:" -> PlayerController.cs field declaration
        AssetLocationDto? fieldDef = await service.GetDefinitionAsync(playerPrefabPath, 48, 4);
        Assert.NotNull(fieldDef);
        Assert.Equal(playerScriptPath, fieldDef.FullPath, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(7, fieldDef.Line);
        Assert.Equal(44, fieldDef.Character);

        // Definition on PrefabModification "propertyPath: config" -> PlayerController.cs field declaration
        AssetLocationDto? modDef = await service.GetDefinitionAsync(mainScenePath, 43, 22);
        Assert.NotNull(modDef);
        Assert.Equal(playerScriptPath, modDef.FullPath, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(7, modDef.Line);
        Assert.Equal(44, modDef.Character);

        // Hierarchy on Player.prefab
        AssetHierarchyNodeDto[] prefabHierarchy = await service.GetHierarchyAsync(playerPrefabPath, maxDepth: 8, includeSerializedValues: true);
        AssetHierarchyNodeDto root = Assert.Single(prefabHierarchy);
        Assert.Equal("Player", root.Name);
        Assert.Contains(root.Components, c => c.TypeName == "PlayerController" && c.ScriptGuid == PlayerControllerGuid);
        AssetHierarchyNodeDto child = Assert.Single(root.Children);
        Assert.Equal("WeaponSlot", child.Name);

        // Hierarchy on MainScene.unity
        AssetHierarchyNodeDto[] sceneHierarchy = await service.GetHierarchyAsync(mainScenePath, maxDepth: 8, includeSerializedValues: false);
        Assert.Equal(2, sceneHierarchy.Length);
        Assert.Equal("GameManager", sceneHierarchy[0].Name);
        Assert.True(sceneHierarchy[1].IsPrefabInstance);
        Assert.Equal("Player", sceneHierarchy[1].Name);
        Assert.Equal("Assets/Prefabs/Player.prefab", sceneHierarchy[1].SourcePrefabPath);
    }

    [Fact]
    public async Task GetAssetDiagnosticsAsync_UnknownScriptGuid_ReportsUnity0001()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "DiagProj");
        const string unknownGuid = "deadbeefdeadbeefdeadbeefdeadbeef";
        const string validScriptGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string brokenPrefabGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        WriteAssetWithMeta(projectRoot, "Assets/Scripts/ValidScript.cs", validScriptGuid, "public class ValidScript {}");
        string brokenPrefabPath = WriteAssetWithMeta(
            projectRoot,
            "Assets/Prefabs/Broken.prefab",
            brokenPrefabGuid,
            string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1 &100000",
                "GameObject:",
                "  m_Name: BrokenObject",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                "  m_GameObject: {fileID: 100000}",
                $"  m_Script: {{fileID: 11500000, guid: {unknownGuid}, type: 3}}",
            ]));

        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(projectRoot, Path.Combine(tempDir.Path, "db.sqlite"));

        AssetDiagnosticDto[] diags = await service.GetAssetDiagnosticsAsync(brokenPrefabPath);
        AssetDiagnosticDto diag = Assert.Single(diags);
        Assert.Equal("UNITY0001", diag.Code);
        Assert.Equal(8, diag.Line);
        Assert.Equal(37, diag.StartCharacter);
        Assert.Equal(69, diag.EndCharacter);
        Assert.Contains(unknownGuid, diag.Message);
        Assert.Contains("BrokenObject", diag.Message);

        // Also works on an unindexed temporary prefab written directly on disk
        string unindexedPath = Path.Combine(projectRoot, "Assets", "Prefabs", "TempUnindexed.prefab");
        File.WriteAllText(
            unindexedPath,
            string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1 &100000",
                "GameObject:",
                "  m_Name: TempGo",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                "  m_GameObject: {fileID: 100000}",
                $"  m_Script: {{fileID: 11500000, guid: {unknownGuid}, type: 3}}",
            ]),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        AssetDiagnosticDto[] unindexedDiags = await service.GetAssetDiagnosticsAsync(unindexedPath);
        AssetDiagnosticDto unindexedDiag = Assert.Single(unindexedDiags);
        Assert.Equal("UNITY0001", unindexedDiag.Code);
        Assert.Equal(8, unindexedDiag.Line);
    }

    [Fact]
    public async Task GetAssetsAsync_FiltersByKindAndReturnsGuidsForCompletion()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = GetDummyProjectRoot();
        using var service = new AssetDataServiceImpl(projectRoot, Path.Combine(tempDir.Path, "db.sqlite"), enableFileWatcher: false);
        await service.InitializeAndSyncAsync();

        AssetEntryDto[] scripts = await service.GetAssetsAsync(AssetKind.CSharpScript);
        Assert.NotEmpty(scripts);
        Assert.All(scripts, a => Assert.Equal(AssetKind.CSharpScript, a.Kind));
        Assert.Contains(scripts, a => a.Guid == PlayerControllerGuid && a.RelativePath == "Assets/Scripts/PlayerController.cs");

        AssetEntryDto[] prefabs = await service.GetAssetsAsync(AssetKind.Prefab);
        AssetEntryDto prefab = Assert.Single(prefabs);
        Assert.Equal(PlayerPrefabGuid, prefab.Guid);

        AssetEntryDto[] all = await service.GetAssetsAsync(null);
        Assert.True(all.Length > scripts.Length + prefabs.Length);
    }

    [Fact]
    public async Task GetAssetDiagnosticsAsync_UnresolvedGuids_ReportsUnity0002WithoutDuplicatingUnity0001()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "DanglingDiagProj");
        const string validScriptGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string deletedPrefabGuid = "cccccccccccccccccccccccccccccccc";
        const string deletedAssetGuid = "dddddddddddddddddddddddddddddddd";

        WriteAssetWithMeta(projectRoot, "Assets/Scripts/ValidScript.cs", validScriptGuid, "public class ValidScript {}");
        string scenePath = WriteAssetWithMeta(
            projectRoot,
            "Assets/Scenes/Broken.unity",
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1001 &100100",
                "PrefabInstance:",
                $"  m_SourcePrefab: {{fileID: 100100000, guid: {deletedPrefabGuid}, type: 3}}",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                $"  m_Script: {{fileID: 11500000, guid: {validScriptGuid}, type: 3}}",
                $"  icon: {{fileID: 2800000, guid: {deletedAssetGuid}, type: 3}}",
            ]));

        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(projectRoot, Path.Combine(tempDir.Path, "db.sqlite"));

        AssetDiagnosticDto[] diags = await service.GetAssetDiagnosticsAsync(scenePath);
        Assert.Equal(2, diags.Length);
        Assert.All(diags, d => Assert.Equal("UNITY0002", d.Code));

        AssetDiagnosticDto prefabDiag = Assert.Single(diags, d => d.Message.Contains("Missing prefab reference"));
        Assert.Equal(4, prefabDiag.Line);
        Assert.Contains(deletedPrefabGuid, prefabDiag.Message);
        Assert.Contains("m_SourcePrefab", prefabDiag.Message);

        AssetDiagnosticDto assetDiag = Assert.Single(diags, d => d.Message.Contains("Dangling asset reference"));
        Assert.Equal(8, assetDiag.Line);
        Assert.Contains(deletedAssetGuid, assetDiag.Message);
        Assert.Contains("icon", assetDiag.Message);
    }

    [Fact]
    public async Task GetAssetDiagnosticsAsync_PrefabOverride_ReportsUnity0003OnPropertyPath()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = GetDummyProjectRoot();
        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(projectRoot, tempDir.Path);
        string scenePath = Path.Combine(projectRoot, "Assets", "Scenes", "MainScene.unity");

        AssetDiagnosticDto[] diags = await service.GetAssetDiagnosticsAsync(scenePath);
        AssetDiagnosticDto diag = Assert.Single(diags, d => d.Code == "UNITY0003");
        Assert.Equal(43, diag.Line);
        Assert.Equal(20, diag.StartCharacter);
        Assert.Equal(26, diag.EndCharacter);
        Assert.Contains("Player.prefab", diag.Message);
        Assert.Contains("in MainScene.unity", diag.Message);
        Assert.Contains("overrides", diag.Message);

        string[] lines = File.ReadAllLines(scenePath);
        Assert.Equal("config", lines[diag.Line].Substring(diag.StartCharacter, diag.EndCharacter - diag.StartCharacter));
    }

    [Fact]
    public async Task GetAssetDiagnosticsAsync_UnindexedPrefabOverride_ReportsUnity0003FromScan()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "OverrideDiagProj");
        const string basePrefabGuid = "bcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbc";
        WriteAssetWithMeta(projectRoot, "Assets/Prefabs/Base.prefab", basePrefabGuid, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");

        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(projectRoot, Path.Combine(tempDir.Path, "db.sqlite"));

        string variantPath = Path.Combine(projectRoot, "Assets", "Prefabs", "Variant.prefab");
        string variantText = string.Join("\n",
        [
            "%YAML 1.1",
            "%TAG !u! tag:unity3d.com,2011:",
            "--- !u!1001 &300000",
            "PrefabInstance:",
            "  m_Modification:",
            "    m_Modifications:",
            $"    - target: {{fileID: 100014, guid: {basePrefabGuid}, type: 3}}",
            "      propertyPath: config",
            "      value: 42",
        ]);
        File.WriteAllText(variantPath, variantText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        AssetDiagnosticDto[] diags = await service.GetAssetDiagnosticsAsync(variantPath);
        AssetDiagnosticDto diag = Assert.Single(diags, d => d.Code == "UNITY0003");
        Assert.Equal(7, diag.Line);
        Assert.Contains("Base.prefab", diag.Message);
    }

    [Fact]
    public async Task AssetFileWatcher_DebouncesFileEventsAndUpdatesStoreAndGraph()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "WatchProj");
        string dbPath = Path.Combine(tempDir.Path, "db.sqlite");

        const string scriptGuid = "12121212121212121212121212121212";
        const string prefabGuid = "34343434343434343434343434343434";

        WriteAssetWithMeta(projectRoot, "Assets/Scripts/Enemy.cs", scriptGuid, "public class Enemy {}");

        using var service = new AssetDataServiceImpl(
            projectRoot,
            dbPath,
            enableFileWatcher: true,
            watcherDebounceWindow: TimeSpan.FromMilliseconds(150));

        await service.InitializeAndSyncAsync();

        ScriptReferenceCountDto initial = Assert.Single(await service.GetScriptReferenceCountsAsync([scriptGuid]));
        Assert.Equal(0, initial.TotalAssetCount);

        // Ignored directory write should NOT be indexed
        WriteAssetWithMeta(
            projectRoot,
            "Library/Ignored/ShouldNotIndex.prefab",
            "99999999999999999999999999999999",
            string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                $"  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}",
            ]));

        string prefabPath = WriteAssetWithMeta(
            projectRoot,
            "Assets/Prefabs/Enemy.prefab",
            prefabGuid,
            string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1 &100000",
                "GameObject:",
                "  m_Name: Enemy",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                "  m_GameObject: {fileID: 100000}",
                $"  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}",
            ]));

        // Wait up to 5 seconds for debounced FileSystemWatcher update
        bool updated = await WaitUntilAsync(async () =>
        {
            ScriptReferenceCountDto[] counts = await service.GetScriptReferenceCountsAsync([scriptGuid]);
            return counts.Length == 1 && counts[0].DirectPrefabCount == 1;
        }, TimeSpan.FromSeconds(5));

        Assert.True(updated, "Expected AssetFileWatcher to update graph after prefab creation.");
        Assert.NotNull(await service.GetAssetByPathAsync("Assets/Prefabs/Enemy.prefab"));
        Assert.Null(await service.GetAssetByPathAsync("Library/Ignored/ShouldNotIndex.prefab"));

        // Delete prefab and verify debounced removal
        File.Delete(prefabPath);
        File.Delete(prefabPath + ".meta");

        bool removed = await WaitUntilAsync(async () =>
        {
            ScriptReferenceCountDto[] counts = await service.GetScriptReferenceCountsAsync([scriptGuid]);
            return counts.Length == 1 && counts[0].DirectPrefabCount == 0;
        }, TimeSpan.FromSeconds(5));

        Assert.True(removed, "Expected AssetFileWatcher to remove deleted prefab from graph.");
        Assert.Null(await service.GetAssetByPathAsync("Assets/Prefabs/Enemy.prefab"));
    }

    [Fact]
    public async Task GetHierarchyAsync_RepeatedRequests_ReuseCachedResult()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = GetDummyProjectRoot();
        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(projectRoot, tempDir.Path);

        Assert.Equal(0, service.HierarchyCache.Count);

        AssetHierarchyNodeDto[] first = await service.GetHierarchyAsync("Assets/Prefabs/Player.prefab", 8, true);
        Assert.Equal(0, service.HierarchyCache.Hits);
        Assert.Equal(1, service.HierarchyCache.Misses);
        Assert.Equal(1, service.HierarchyCache.Count);

        string fullPath = Path.GetFullPath(Path.Combine(projectRoot, "Assets", "Prefabs", "Player.prefab"));
        AssetHierarchyNodeDto[] second = await service.GetHierarchyAsync(fullPath, 8, true);
        Assert.Equal(1, service.HierarchyCache.Hits);
        Assert.Equal(1, service.HierarchyCache.Misses);
        Assert.Equal(first.Length, second.Length);
        Assert.Equal("Player", Assert.Single(second).Name);

        await service.GetHierarchyAsync(fullPath, 4, true);
        Assert.Equal(2, service.HierarchyCache.Misses);
        Assert.Equal(2, service.HierarchyCache.Count);

        await service.RescanAsync();
        Assert.Equal(0, service.HierarchyCache.Count);
        await service.GetHierarchyAsync(fullPath, 8, true);
        Assert.Equal(3, service.HierarchyCache.Misses);
    }

    [Fact]
    public async Task GetHierarchyAsync_Invalidation_RebuildsFromUpdatedFile()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "CacheProj");
        string prefabPath = WriteAssetWithMeta(
            projectRoot,
            "Assets/Prefabs/Cached.prefab",
            "55555555555555555555555555555555",
            HierarchyPrefabYaml("Before"));

        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(
            projectRoot,
            Path.Combine(tempDir.Path, "db.sqlite"));

        Assert.Equal("Before", Assert.Single(await service.GetHierarchyAsync(prefabPath, 8, false)).Name);
        Assert.Equal(1, service.HierarchyCache.Count);

        File.WriteAllText(prefabPath, HierarchyPrefabYaml("After"), new UTF8Encoding(false));
        Assert.Equal("Before", Assert.Single(await service.GetHierarchyAsync(prefabPath, 8, false)).Name);

        Assert.Equal(1, service.HierarchyCache.Invalidate(prefabPath));
        Assert.Equal("After", Assert.Single(await service.GetHierarchyAsync(prefabPath, 8, false)).Name);
    }

    [Fact]
    public async Task GetHierarchyAsync_FileWatcherUpdate_InvalidatesCachedHierarchy()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "WatchCacheProj");
        string prefabPath = WriteAssetWithMeta(
            projectRoot,
            "Assets/Prefabs/Watched.prefab",
            "66666666666666666666666666666666",
            HierarchyPrefabYaml("Original"));

        using var service = new AssetDataServiceImpl(
            projectRoot,
            Path.Combine(tempDir.Path, "db.sqlite"),
            enableFileWatcher: true,
            watcherDebounceWindow: TimeSpan.FromMilliseconds(100));
        await service.InitializeAndSyncAsync();

        Assert.Equal("Original", Assert.Single(await service.GetHierarchyAsync(prefabPath, 8, false)).Name);
        Assert.Equal(1, service.HierarchyCache.Count);

        File.WriteAllText(prefabPath, HierarchyPrefabYaml("Renamed"), new UTF8Encoding(false));

        bool invalidated = await WaitUntilAsync(
            () => Task.FromResult(service.HierarchyCache.Count == 0),
            TimeSpan.FromSeconds(5));
        Assert.True(invalidated, "Expected the file watcher to invalidate the cached hierarchy.");
        Assert.Equal("Renamed", Assert.Single(await service.GetHierarchyAsync(prefabPath, 8, false)).Name);
    }

    [Fact]
    public async Task GetAssetDiagnosticsAsync_PackageCacheGuid_SuppressesUnity0002()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "PackageCacheDiagProj");
        const string packageGuid = "77777777777777777777777777777777";
        const string missingGuid = "88888888888888888888888888888888";
        const string validScriptGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        WriteAssetWithMeta(projectRoot, "Assets/Scripts/ValidScript.cs", validScriptGuid, "public class ValidScript {}");
        string scenePath = WriteAssetWithMeta(
            projectRoot,
            "Assets/Scenes/WithPackage.unity",
            "99999999999999999999999999999999",
            string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                $"  m_Script: {{fileID: 11500000, guid: {validScriptGuid}, type: 3}}",
                $"  packageRef: {{fileID: 11400000, guid: {packageGuid}, type: 2}}",
                $"  missingRef: {{fileID: 11400000, guid: {missingGuid}, type: 2}}",
            ]));

        string packageMetaPath = Path.Combine(
            projectRoot,
            "Library",
            "PackageCache",
            "com.example.pkg@1.0.0",
            "Runtime",
            "PkgAsset.asset.meta");
        Directory.CreateDirectory(Path.GetDirectoryName(packageMetaPath)!);
        File.WriteAllText(
            packageMetaPath,
            $"fileFormatVersion: 2\nguid: {packageGuid}\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(
            projectRoot,
            Path.Combine(tempDir.Path, "db.sqlite"));

        AssetDiagnosticDto[] diags = await service.GetAssetDiagnosticsAsync(scenePath);
        AssetDiagnosticDto diag = Assert.Single(diags);
        Assert.Equal("UNITY0002", diag.Code);
        Assert.Contains(missingGuid, diag.Message);
        Assert.DoesNotContain(packageGuid, diag.Message);
        Assert.True(service.PackageCacheGuidIndex.Contains(packageGuid));
        Assert.False(service.PackageCacheGuidIndex.Contains(missingGuid));
    }

    private static string HierarchyPrefabYaml(string name) => string.Join("\n",
    [
        "%YAML 1.1",
        "%TAG !u! tag:unity3d.com,2011:",
        "--- !u!1 &100000",
        "GameObject:",
        "  m_Component:",
        "  - component: {fileID: 100004}",
        $"  m_Name: {name}",
        "--- !u!4 &100004",
        "Transform:",
        "  m_GameObject: {fileID: 100000}",
        "  m_Father: {fileID: 0}",
        "  m_RootOrder: 0",
    ]);

    private static async Task<AssetDataServiceImpl> CreateInitializedServiceAsync(string projectRoot, string dbDir)
    {
        string dbPath = Path.Combine(dbDir, "asset-db.sqlite");
        var service = new AssetDataServiceImpl(projectRoot, dbPath, enableFileWatcher: false);
        await service.InitializeAndSyncAsync();
        return service;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }
            await Task.Delay(50);
        }

        return await condition();
    }

    private static string ReadSpanAt(string filePath, AssetTextEditDto edit)
    {
        string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
        return lines[edit.Line].Substring(edit.StartCharacter, edit.EndCharacter - edit.StartCharacter);
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

    [Fact]
    public async Task GuidReferenceMethods_DummyProject_ForwardAndReverseLookup()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = GetDummyProjectRoot();
        using AssetDataServiceImpl service = await CreateInitializedServiceAsync(projectRoot, tempDir.Path);

        AssetReferencedGuidResultDto forward = await service.GetReferencedGuidsAsync(
            new AssetReferencedGuidQueryDto { AssetPath = "Assets/Prefabs/Player.prefab" });

        Assert.Equal(2, forward.References.Length);
        AssetReferencedGuidItemDto scriptRef = Assert.Single(forward.References, item => item.Guid == PlayerControllerGuid);
        Assert.Equal(45, scriptRef.Line);
        Assert.Equal(1, scriptRef.OccurrenceCount);
        Assert.NotNull(scriptRef.ResolvedPath);
        Assert.Equal(AssetKind.CSharpScript, scriptRef.ResolvedKind);
        AssetReferencedGuidItemDto configRef = Assert.Single(forward.References, item => item.Guid == "a1b2c3d4e5f60718293a4b5c6d7e8f90");
        Assert.Equal(48, configRef.Line);
        Assert.Null(configRef.ResolvedPath);
        Assert.Null(configRef.ResolvedKind);
        Assert.False(configRef.InPackageCache);

        AssetGuidReferenceResultDto reverse = await service.GetGuidReferencesAsync(
            new AssetGuidReferenceQueryDto { Guid = "a1b2c3d4e5f60718293a4b5c6d7e8f90" });

        Assert.Equal(2, reverse.References.Length);
        Assert.Contains(reverse.References, item =>
            item.RelativePath == "Assets/Prefabs/Player.prefab" && item.Line == 48 && item.AssetKind == AssetKind.Prefab);
        Assert.Contains(reverse.References, item =>
            item.RelativePath == "Assets/Scenes/MainScene.unity" && item.Line == 45 && item.AssetKind == AssetKind.Scene);

        AssetGuidReferenceResultDto invalid = await service.GetGuidReferencesAsync(
            new AssetGuidReferenceQueryDto { Guid = "not-a-guid" });
        Assert.Empty(invalid.References);

        AssetReferencedGuidResultDto missing = await service.GetReferencedGuidsAsync(
            new AssetReferencedGuidQueryDto { AssetPath = "Assets/DoesNotExist.prefab" });
        Assert.Empty(missing.References);
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"UnityCSharpLSP_ServiceTests_{Guid.NewGuid():N}");
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
