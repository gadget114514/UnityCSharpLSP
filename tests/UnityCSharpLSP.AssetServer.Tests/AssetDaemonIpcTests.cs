using System.Diagnostics;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Daemon;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class AssetDaemonIpcTests
{
    private const string PlayerControllerGuid = "c5c9d31933d241c4b9047a5783f7f380";
    private const string PlayerPrefabGuid = "d1e2f3a4b5c64758697a8b9c0d1e2f30";
    private const string MainSceneGuid = "e1f2a3b4c5d64758697a8b9c0d1e2f40";

    [Fact]
    public void AssetPipeNaming_ProducesDeterministicNormalizedNames()
    {
        string pathA = Path.Combine(Path.GetTempPath(), "UnityProjA", "SubFolder");
        string pathB = pathA + Path.DirectorySeparatorChar;

        string hashA = AssetPipeNaming.ComputeProjectHash(pathA);
        string hashB = AssetPipeNaming.ComputeProjectHash(pathB);

        Assert.Equal(16, hashA.Length);
        Assert.Equal(hashA, hashA.ToLowerInvariant());
        Assert.Equal(hashA, hashB);

        if (OperatingSystem.IsWindows())
        {
            string hashUpper = AssetPipeNaming.ComputeProjectHash(pathA.ToUpperInvariant());
            Assert.Equal(hashA, hashUpper);
        }

        string otherHash = AssetPipeNaming.ComputeProjectHash(Path.Combine(Path.GetTempPath(), "UnityProjB"));
        Assert.NotEqual(hashA, otherHash);

        Assert.Equal($"unitycsharplsp-assetd-{hashA}", AssetPipeNaming.GetPipeName(pathA));
        Assert.Equal("custom-test-pipe", AssetPipeNaming.GetPipeName(pathA, "custom-test-pipe"));
        Assert.Equal($"Global\\UnityCSharpLSP.AssetDaemon.{hashA}", AssetPipeNaming.GetMutexName(pathA));
    }

    [Fact]
    public async Task NamedPipe_EndToEndRpcCalls_AndShutdown_Succeed()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");
        string projectRoot = GetDummyProjectRoot();
        string pipeName = CreateUniquePipeName();

        using var hostCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var host = new AssetDaemonHost(
            projectRoot,
            idleTimeout: TimeSpan.FromMinutes(5),
            pipeName: pipeName,
            databasePath: dbPath,
            enableFileWatcher: false);

        Task hostTask = host.RunAsync(hostCts.Token);

        using AssetDaemonClient client = await AssetDaemonClient.ConnectAsync(pipeName, connectTimeoutMs: 5000, hostCts.Token);
        Assert.True(client.IsConnected);
        Assert.False(client.IsInProcessFallback);

        DaemonHandshakeResponse handshake = await client.HandshakeAsync(new DaemonHandshakeRequest
        {
            ClientVersion = "1.0.0",
            ClientPid = Environment.ProcessId,
            ProjectRoot = projectRoot,
        }, hostCts.Token);

        Assert.Equal(Environment.ProcessId, handshake.DaemonPid);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)), handshake.ProjectRoot);
        Assert.True(handshake.State is "Indexing" or "Ready");

        DaemonStatusResponse status = await WaitForReadyStatusAsync(client, hostCts.Token);
        Assert.Equal("Ready", status.State);
        Assert.Equal(10, status.TotalAssets);
        Assert.Equal(3, status.TotalScripts);
        Assert.Equal(1, status.TotalPrefabs);
        Assert.Equal(1, status.TotalScenes);
        Assert.Equal(dbPath, status.DatabasePath);

        AssetEntryDto? byGuid = await client.GetAssetByGuidAsync(PlayerControllerGuid, hostCts.Token);
        Assert.NotNull(byGuid);
        Assert.Equal("Assets/Scripts/PlayerController.cs", byGuid.RelativePath);

        AssetEntryDto? byPath = await client.GetAssetByPathAsync("Assets/Prefabs/Player.prefab", hostCts.Token);
        Assert.NotNull(byPath);
        Assert.Equal(PlayerPrefabGuid, byPath.Guid);

        ScriptReferenceCountDto[] refCounts = await client.GetScriptReferenceCountsAsync([PlayerControllerGuid], hostCts.Token);
        ScriptReferenceCountDto count = Assert.Single(refCounts);
        Assert.Equal(1, count.DirectPrefabCount);
        Assert.Equal(0, count.TransitivePrefabCount);
        Assert.Equal(1, count.SceneCount);
        Assert.Equal(2, count.TotalAssetCount);

        AssetReferenceResultDto classRefs = await client.FindReferencesAsync(new AssetReferenceQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Class,
            IncludeTransitiveScenes = true,
            MaxResults = 500,
        }, hostCts.Token);
        Assert.Equal(2, classRefs.Locations.Length);

        AssetReferenceResultDto fieldRefs = await client.FindReferencesAsync(new AssetReferenceQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Field,
            MemberName = "config",
            MaxResults = 500,
        }, hostCts.Token);
        Assert.Equal(2, fieldRefs.Locations.Length);

        AssetReferenceResultDto methodRefs = await client.FindReferencesAsync(new AssetReferenceQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Method,
            MemberName = "ApplyDamage",
            MaxResults = 500,
        }, hostCts.Token);
        Assert.Single(methodRefs.Locations);

        AssetWorkspaceEditDto renameEdits = await client.ComputeRenameEditsAsync(new AssetRenameQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Field,
            OldName = "config",
            NewName = "gameConfig",
        }, hostCts.Token);
        Assert.Equal(2, renameEdits.FileEdits.Length);

        AssetDocumentLinksDto links = await client.GetDocumentLinksAsync("Assets/Prefabs/Player.prefab", hostCts.Token);
        Assert.NotEmpty(links.Links);

        AssetHoverDto? hover = await client.GetHoverAsync("Assets/Prefabs/Player.prefab", line: 45, character: 40, hostCts.Token);
        Assert.NotNull(hover);

        AssetLocationDto? def = await client.GetDefinitionAsync("Assets/Prefabs/Player.prefab", line: 45, character: 40, hostCts.Token);
        Assert.NotNull(def);

        AssetHierarchyNodeDto[] hierarchy = await client.GetHierarchyAsync("Assets/Prefabs/Player.prefab", maxDepth: 8, includeSerializedValues: false, hostCts.Token);
        Assert.Single(hierarchy);

        BuildSceneDto[] scenes = await client.GetBuildScenesAsync(hostCts.Token);
        BuildSceneDto mainScene = Assert.Single(scenes);
        Assert.Equal(MainSceneGuid, mainScene.Guid);

        AssetDiagnosticDto[] diagnostics = await client.GetAssetDiagnosticsAsync("Assets/Prefabs/Player.prefab", hostCts.Token);
        Assert.Empty(diagnostics);

        bool eventUsed = await client.IsMethodReferencedByUnityEventAsync(PlayerControllerGuid, "ApplyDamage", hostCts.Token);
        Assert.True(eventUsed);

        DaemonStatusResponse rescanStatus = await client.RescanAsync(hostCts.Token);
        Assert.Equal("Ready", rescanStatus.State);
        Assert.Equal(10, rescanStatus.TotalAssets);

        await client.ShutdownAsync(hostCts.Token);
        await hostTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SingleInstanceMutex_PreventsDuplicateDaemonForSameProjectRoot()
    {
        using var tempDir = new TempDirectoryScope();
        string projectRoot = Path.Combine(tempDir.Path, "UnityProj");
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");
        string expectedPipe = AssetPipeNaming.GetPipeName(projectRoot);

        using var hostCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var primaryHost = new AssetDaemonHost(
            projectRoot,
            idleTimeout: TimeSpan.FromMinutes(5),
            pipeName: null,
            databasePath: dbPath,
            enableFileWatcher: false,
            useSingleInstanceMutex: true);

        Task primaryTask = primaryHost.RunAsync(hostCts.Token);

        using AssetDaemonClient client = await AssetDaemonClient.ConnectAsync(expectedPipe, connectTimeoutMs: 5000, hostCts.Token);
        await WaitForReadyStatusAsync(client, hostCts.Token);

        using var duplicateHost = new AssetDaemonHost(
            projectRoot,
            idleTimeout: TimeSpan.FromMinutes(5),
            pipeName: null,
            databasePath: dbPath,
            enableFileWatcher: false,
            useSingleInstanceMutex: true);

        // Duplicate host for the same project root should exit immediately because the primary holds the Mutex.
        await duplicateHost.RunAsync(hostCts.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(primaryTask.IsCompleted);

        await client.ShutdownAsync(hostCts.Token);
        await primaryTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NamedPipe_MultipleConcurrentClients_AreTrackedAndServeConcurrently()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");
        string projectRoot = GetDummyProjectRoot();
        string pipeName = CreateUniquePipeName();

        using var hostCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var host = new AssetDaemonHost(
            projectRoot,
            idleTimeout: TimeSpan.FromMinutes(5),
            pipeName: pipeName,
            databasePath: dbPath,
            enableFileWatcher: false);

        Task hostTask = host.RunAsync(hostCts.Token);

        using AssetDaemonClient client1 = await AssetDaemonClient.ConnectAsync(pipeName, connectTimeoutMs: 5000, hostCts.Token);
        using AssetDaemonClient client2 = await AssetDaemonClient.ConnectAsync(pipeName, connectTimeoutMs: 5000, hostCts.Token);

        Assert.True(await WaitUntilAsync(() => Task.FromResult(host.ActiveConnectionCount == 2), TimeSpan.FromSeconds(3)));

        await WaitForReadyStatusAsync(client1, hostCts.Token);

        Task<ScriptReferenceCountDto[]> t1 = client1.GetScriptReferenceCountsAsync([PlayerControllerGuid], hostCts.Token);
        Task<AssetReferenceResultDto> t2 = client2.FindReferencesAsync(new AssetReferenceQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Class,
        }, hostCts.Token);

        await Task.WhenAll(t1, t2);
        Assert.Equal(2, Assert.Single(await t1).TotalAssetCount);
        Assert.Equal(2, (await t2).Locations.Length);

        client1.Dispose();
        Assert.True(await WaitUntilAsync(() => Task.FromResult(host.ActiveConnectionCount == 1), TimeSpan.FromSeconds(3)));

        DaemonStatusResponse status2 = await client2.GetStatusAsync(hostCts.Token);
        Assert.Equal(10, status2.TotalAssets);

        await client2.ShutdownAsync(hostCts.Token);
        await hostTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NamedPipe_IdleTimeout_ShutsDownOnlyAfterConnectionsDropToZero()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");
        string projectRoot = GetDummyProjectRoot();
        string pipeName = CreateUniquePipeName();

        using var hostCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var host = new AssetDaemonHost(
            projectRoot,
            idleTimeout: TimeSpan.FromMilliseconds(250),
            pipeName: pipeName,
            databasePath: dbPath,
            enableFileWatcher: false);

        Task hostTask = host.RunAsync(hostCts.Token);

        AssetDaemonClient client = await AssetDaemonClient.ConnectAsync(pipeName, connectTimeoutMs: 5000, hostCts.Token);
        try
        {
            // Keep the connection open longer than idleTimeout to verify the idle timer stays disarmed.
            await Task.Delay(400, hostCts.Token);
            Assert.False(hostTask.IsCompleted);

            DaemonStatusResponse status = await client.GetStatusAsync(hostCts.Token);
            Assert.True(status.TotalAssets is 0 or 10);
        }
        finally
        {
            client.Dispose();
        }

        // Once active connections reach 0, idle timeout should cleanly terminate the host.
        await hostTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateOrFallbackInProcess_WorksReliablyInTestHost()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");
        string projectRoot = GetDummyProjectRoot();
        string unusedPipeName = CreateUniquePipeName();

        using AssetDaemonClient client = AssetDaemonClient.CreateOrFallbackInProcess(
            projectRoot,
            pipeName: unusedPipeName,
            databasePath: dbPath,
            enableFileWatcher: false);

        DaemonHandshakeResponse handshake = await client.HandshakeAsync(new DaemonHandshakeRequest
        {
            ClientVersion = "1.0.0",
            ClientPid = Environment.ProcessId,
            ProjectRoot = projectRoot,
        });

        Assert.True(client.IsInProcessFallback);
        Assert.False(client.IsConnected);
        Assert.Equal("Ready", handshake.State);
        Assert.Equal(Environment.ProcessId, handshake.DaemonPid);

        DaemonStatusResponse status = await client.GetStatusAsync();
        Assert.Equal(10, status.TotalAssets);

        ScriptReferenceCountDto[] refCounts = await client.GetScriptReferenceCountsAsync([PlayerControllerGuid]);
        ScriptReferenceCountDto count = Assert.Single(refCounts);
        Assert.Equal(2, count.TotalAssetCount);

        AssetReferenceResultDto refs = await client.FindReferencesAsync(new AssetReferenceQueryDto
        {
            ScriptGuid = PlayerControllerGuid,
            MemberKind = AssetMemberKind.Class,
        });
        Assert.Equal(2, refs.Locations.Length);

        await client.ShutdownAsync();
    }

    [Fact]
    public async Task CreateOrFallbackInProcess_ConnectsToRunningDaemonOverPipeWhenAvailable()
    {
        using var tempDir = new TempDirectoryScope();
        string dbPath = Path.Combine(tempDir.Path, "asset-db.sqlite");
        string projectRoot = GetDummyProjectRoot();
        string pipeName = CreateUniquePipeName();

        using var hostCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var host = new AssetDaemonHost(
            projectRoot,
            idleTimeout: TimeSpan.FromMinutes(5),
            pipeName: pipeName,
            databasePath: dbPath,
            enableFileWatcher: false);

        Task hostTask = host.RunAsync(hostCts.Token);

        // Wait briefly until the pipe server starts listening.
        using AssetDaemonClient probe = await AssetDaemonClient.ConnectAsync(pipeName, connectTimeoutMs: 5000, hostCts.Token);
        probe.Dispose();

        using AssetDaemonClient client = AssetDaemonClient.CreateOrFallbackInProcess(
            projectRoot,
            pipeName: pipeName,
            databasePath: dbPath,
            enableFileWatcher: false);

        DaemonStatusResponse status = await WaitForReadyStatusAsync(client, hostCts.Token);
        Assert.True(client.IsConnected);
        Assert.False(client.IsInProcessFallback);
        Assert.Equal(10, status.TotalAssets);

        await client.ShutdownAsync(hostCts.Token);
        await hostTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static string CreateUniquePipeName()
        => $"unitycsharplsp-test-{Guid.NewGuid():N}";

    private static async Task<DaemonStatusResponse> WaitForReadyStatusAsync(
        IAssetDataService client,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            DaemonStatusResponse status = await client.GetStatusAsync(ct);
            if (string.Equals(status.State, "Ready", StringComparison.Ordinal))
            {
                return status;
            }

            await Task.Delay(25, ct);
        }

        return await client.GetStatusAsync(ct);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return await condition();
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"UnityCSharpLSP_DaemonTests_{Guid.NewGuid():N}");
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
