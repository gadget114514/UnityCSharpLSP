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
