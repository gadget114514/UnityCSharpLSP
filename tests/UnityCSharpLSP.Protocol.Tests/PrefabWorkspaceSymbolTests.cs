using Nerdbank.Streams;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.Protocol.Handlers;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspSymbolKind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class PrefabWorkspaceSymbolTests
{
    private const string PrefabPath = "Assets/Prefabs/Player.prefab";
    private const string ControllerPrefabPath = "Assets/Prefabs/PlayerController.prefab";
    private const string ControllerScriptPath = "Assets/Scripts/PlayerController.cs";

    [Fact]
    public async Task WorkspaceSymbol_PlayerQuery_ReturnsPrefabAsFileSymbol()
    {
        var asset = new FakeAssetService();
        asset.Assets.Add(CreateAsset(PrefabPath, AssetKind.Prefab));
        using var ws = CreateWorkspace(asset);
        var handler = CreateHandler(ws);

        var symbols = await SearchAsync(handler, "Player");

        var prefab = Assert.Single(symbols, s => s.Name == "Player");
        Assert.Equal(LspSymbolKind.File, prefab.Kind);
        Assert.Equal(
            Path.Combine(FindDummyProjectRoot(), "Assets", "Prefabs", "Player.prefab"),
            prefab.Location.Uri.LocalPath,
            ignoreCase: true);
        Assert.Contains("Prefab", prefab.Detail);
        Assert.Contains(PrefabPath, prefab.Detail);
    }

    [Fact]
    public async Task WorkspaceSymbol_PlayerControllerQuery_ReturnsCSharpSymbolBeforePrefab()
    {
        var asset = new FakeAssetService();
        asset.Assets.Add(CreateAsset(PrefabPath, AssetKind.Prefab));
        asset.Assets.Add(CreateAsset(ControllerPrefabPath, AssetKind.Prefab));
        asset.Assets.Add(CreateAsset(ControllerScriptPath, AssetKind.CSharpScript));
        using var ws = CreateWorkspace(asset);
        var handler = CreateHandler(ws);

        var symbols = await SearchAsync(handler, "PlayerController");

        var csharpIndex = Array.FindIndex(symbols, s =>
            s.Kind == LspSymbolKind.Class &&
            string.Equals(s.Name, "PlayerController", StringComparison.Ordinal));
        var prefabIndex = Array.FindIndex(symbols, s =>
            s.Kind == LspSymbolKind.File &&
            s.Detail == $"Prefab — {ControllerPrefabPath}");
        Assert.True(csharpIndex >= 0, "Expected the C# PlayerController symbol.");
        Assert.True(prefabIndex >= 0, "Expected the PlayerController prefab symbol.");
        Assert.True(csharpIndex < prefabIndex, "C# symbols must come before assets.");
        Assert.Equal(
            Path.Combine(FindDummyProjectRoot(), "Assets", "Scripts", "PlayerController.cs"),
            symbols[csharpIndex].Location.Uri.LocalPath,
            ignoreCase: true);
    }

    [Fact]
    public async Task WorkspaceSymbol_LimitsMergedResultsTo200()
    {
        var asset = new FakeAssetService();
        for (var i = 0; i < 250; i++)
        {
            asset.Assets.Add(CreateAsset($"Assets/Prefabs/CapAsset{i:D3}.prefab", AssetKind.Prefab));
        }

        using var ws = CreateWorkspace(asset);
        var handler = CreateHandler(ws);

        var symbols = await SearchAsync(handler, "CapAsset");

        Assert.Equal(200, symbols.Length);
        Assert.All(symbols, s => Assert.Equal(LspSymbolKind.File, s.Kind));
        Assert.Equal("CapAsset000", symbols[0].Name);
        Assert.Equal("CapAsset199", symbols[^1].Name);
    }

    [Fact]
    public async Task WorkspaceSymbol_MainSceneQuery_ReturnsSceneWithSceneDetail()
    {
        var asset = new FakeAssetService();
        asset.Assets.Add(CreateAsset(PrefabPath, AssetKind.Prefab));
        asset.Assets.Add(CreateAsset("Assets/Scenes/MainScene.unity", AssetKind.Scene));
        using var ws = CreateWorkspace(asset);
        var handler = CreateHandler(ws);

        var symbols = await SearchAsync(handler, "MainScene");

        var scene = Assert.Single(symbols, s => s.Name == "MainScene");
        Assert.Equal(LspSymbolKind.File, scene.Kind);
        Assert.Equal("Scene — Assets/Scenes/MainScene.unity", scene.Detail);
    }

    [Fact]
    public async Task WorkspaceSymbol_EmptyQuery_DoesNotReturnAllAssets()
    {
        var asset = new FakeAssetService();
        asset.Assets.Add(CreateAsset(PrefabPath, AssetKind.Prefab));
        asset.Assets.Add(CreateAsset("Assets/Scenes/MainScene.unity", AssetKind.Scene));
        using var ws = CreateWorkspace(asset);
        var handler = CreateHandler(ws);

        var symbols = await SearchAsync(handler, string.Empty);

        Assert.DoesNotContain(symbols, s => s.Kind == LspSymbolKind.File);
    }

    private static AssetEntryDto CreateAsset(string relativePath, AssetKind kind) => new()
    {
        Guid = Guid.NewGuid().ToString("N"),
        RelativePath = relativePath,
        FullPath = Path.Combine(
            FindDummyProjectRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar)),
        Kind = kind,
    };

    private static UnityWorkspaceManager CreateWorkspace(FakeAssetService asset) =>
        UnityWorkspaceManager.Create(FindDummyProjectRoot(), assetService: asset);

    private static WorkspaceSymbolHandler CreateHandler(UnityWorkspaceManager ws)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, serverStream, new JsonMessageFormatter()));
        return new WorkspaceSymbolHandler(() => new[] { ws }, rpc);
    }

    private static async Task<WorkspaceSymbol[]> SearchAsync(WorkspaceSymbolHandler handler, string query)
    {
        var symbols = await handler.WorkspaceSymbol(
            new WorkspaceSymbolRequestParams { Query = query },
            CancellationToken.None);
        Assert.NotNull(symbols);
        return symbols!;
    }

    private static string FindDummyProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var dummy = Path.Combine(dir, "DummyProject");
            if (Directory.Exists(dummy))
            {
                return dummy;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not find DummyProject folder.");
    }

    private sealed class FakeAssetService : IAssetDataService
    {
        public List<AssetEntryDto> Assets { get; } = new();

        public Task<AssetEntryDto[]> GetAssetsAsync(AssetKind? kind, CancellationToken ct = default) =>
            Task.FromResult(Assets.Where(asset => kind is null || asset.Kind == kind).ToArray());

        public Task<DaemonHandshakeResponse> HandshakeAsync(DaemonHandshakeRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DaemonStatusResponse> GetStatusAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetEntryDto?> GetAssetByGuidAsync(string guidHex, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetEntryDto?> GetAssetByPathAsync(string filePath, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<ScriptReferenceCountDto[]> GetScriptReferenceCountsAsync(string[] scriptGuids, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetReferenceResultDto> FindReferencesAsync(AssetReferenceQueryDto query, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetWorkspaceEditDto> ComputeRenameEditsAsync(AssetRenameQueryDto query, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetDocumentLinksDto> GetDocumentLinksAsync(string assetPath, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetHoverDto?> GetHoverAsync(string assetPath, int line, int character, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetLocationDto?> GetDefinitionAsync(string assetPath, int line, int character, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetHierarchyNodeDto[]> GetHierarchyAsync(
            string assetPath, int maxDepth, bool includeSerializedValues, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<BuildSceneDto[]> GetBuildScenesAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetDiagnosticDto[]> GetAssetDiagnosticsAsync(string assetPath, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> IsMethodReferencedByUnityEventAsync(string scriptGuid, string methodName, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DaemonStatusResponse> RescanAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task ShutdownAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
