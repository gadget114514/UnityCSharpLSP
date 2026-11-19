using Microsoft.VisualStudio.LanguageServer.Protocol;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.Protocol.Handlers;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class SceneGameObjectParityTests
{
    private const string PrefabPath = "Assets/Prefabs/Player.prefab";
    private const string ScenePath = "Assets/Scenes/MainScene.unity";

    [Fact]
    public async Task Completion_AfterGetComponentLessThan_IncludesRectTransformFromScene()
    {
        const string source = """
            using UnityEngine;

            namespace Game.Runtime
            {
                public sealed class PlayerController : MonoBehaviour
                {
                    public void Probe()
                    {
                        GetComponent<Re
                    }
                }
            }
            """;

        var (handler, ws, _) = CreateHandler(source, includeRectTransformInPrefab: false);
        using (ws)
        {
            var items = await CompleteAsync(handler, ScriptPath(), PositionAfter(source, "GetComponent<Re"));

            Assert.NotNull(items);
            var rect = Assert.Single(items!, item => item.Label == "RectTransform");
            Assert.Equal(CompletionItemKind.Class, rect.Kind);
            Assert.Equal($"from {ScenePath}", rect.Detail);
            Assert.True(items!.Length <= 200);
        }
    }

    [Fact]
    public async Task Completion_AfterGetComponentLessThan_DeduplicatesTypesFromPrefabAndScene()
    {
        const string source = """
            using UnityEngine;

            namespace Game.Runtime
            {
                public sealed class PlayerController : MonoBehaviour
                {
                    public void Probe()
                    {
                        GetComponent<Re
                    }
                }
            }
            """;

        var (handler, ws, _) = CreateHandler(source, includeRectTransformInPrefab: true);
        using (ws)
        {
            var items = await CompleteAsync(handler, ScriptPath(), PositionAfter(source, "GetComponent<Re"));

            Assert.NotNull(items);
            var rect = Assert.Single(items!, item => item.Label == "RectTransform");
            Assert.Equal($"from {PrefabPath}", rect.Detail);
        }
    }

    [Fact]
    public async Task Completion_InMonoBehaviourScope_MergesSceneGameObjectNamesWithOrigin()
    {
        const string source = """
            using UnityEngine;

            namespace Game.Runtime
            {
                public sealed class PlayerController : MonoBehaviour
                {
                    public void Probe()
                    {
                        var slot = Wea
                    }
                }
            }
            """;

        var (handler, ws, _) = CreateHandler(source, includeRectTransformInPrefab: false);
        using (ws)
        {
            var items = await CompleteAsync(handler, ScriptPath(), PositionAfter(source, "Wea"));

            Assert.NotNull(items);
            Assert.Contains(items!, item =>
                item.Label == "GameManager" &&
                item.Kind == CompletionItemKind.Variable &&
                item.Detail == $"GameObject in {ScenePath}");
            var player = Assert.Single(items!, item => item.Label == "Player");
            Assert.Equal($"GameObject in {PrefabPath}", player.Detail);
        }
    }

    private static (CompletionHandler Handler, UnityWorkspaceManager Ws, FakeAssetService Asset) CreateHandler(
        string source,
        bool includeRectTransformInPrefab)
    {
        var asset = new FakeAssetService();
        asset.Assets.Add(new AssetEntryDto
        {
            Guid = "d1e2f3a4b5c64758697a8b9c0d1e2f30",
            RelativePath = PrefabPath,
            FullPath = Path.Combine(FindDummyProjectRoot(), PrefabPath.Replace('/', Path.DirectorySeparatorChar)),
            Kind = AssetKind.Prefab,
        });
        asset.Assets.Add(new AssetEntryDto
        {
            Guid = "e1f2a3b4c5d64758697a8b9c0d1e2f40",
            RelativePath = ScenePath,
            FullPath = Path.Combine(FindDummyProjectRoot(), ScenePath.Replace('/', Path.DirectorySeparatorChar)),
            Kind = AssetKind.Scene,
        });
        asset.Hierarchies[PrefabPath] = new[]
        {
            new AssetHierarchyNodeDto
            {
                Name = "Player",
                Components = CreateComponents(includeRectTransformInPrefab),
                Children = new[]
                {
                    new AssetHierarchyNodeDto
                    {
                        Name = "WeaponSlot",
                        Components = new[] { new AssetComponentSummaryDto { ClassId = 4, TypeName = "Transform" } },
                    },
                },
            },
        };
        asset.Hierarchies[ScenePath] = new[]
        {
            new AssetHierarchyNodeDto
            {
                Name = "GameManager",
                Components = new[]
                {
                    new AssetComponentSummaryDto { ClassId = 4, TypeName = "Transform" },
                    new AssetComponentSummaryDto { ClassId = 224, TypeName = "RectTransform" },
                },
                Children = new[]
                {
                    new AssetHierarchyNodeDto { Name = "Player" },
                },
            },
        };
        asset.References.Add(new AssetReferenceLocationDto
        {
            AssetKind = AssetKind.Prefab,
            RelativePath = PrefabPath,
            FullPath = Path.Combine(FindDummyProjectRoot(), PrefabPath.Replace('/', Path.DirectorySeparatorChar)),
        });

        var ws = UnityWorkspaceManager.Create(FindDummyProjectRoot(), assetService: asset);
        ws.OpenOrUpdateDocument(ScriptPath(), source);
        var handler = new CompletionHandler(_ => ws, new ClientCapabilityStore());
        return (handler, ws, asset);
    }

    private static AssetComponentSummaryDto[] CreateComponents(bool includeRectTransform)
    {
        var components = new List<AssetComponentSummaryDto>
        {
            new() { ClassId = 4, TypeName = "Transform" },
            new()
            {
                ClassId = 114,
                TypeName = "PlayerController",
                ScriptPath = "Assets/Scripts/PlayerController.cs",
                EditorClassIdentifier = "Game.Runtime::Game.Runtime.PlayerController",
            },
        };
        if (includeRectTransform)
        {
            components.Add(new AssetComponentSummaryDto { ClassId = 224, TypeName = "RectTransform" });
        }

        return components.ToArray();
    }

    private static string ScriptPath() => Path.Combine(
        FindDummyProjectRoot(), "Assets", "Scripts", "PlayerController.cs");

    private static Position PositionAfter(string source, string needle)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int index = lines[i].IndexOf(needle, StringComparison.Ordinal);
            if (index >= 0)
            {
                return new Position(i, index + needle.Length);
            }
        }

        throw new InvalidOperationException($"Could not find '{needle}' in source.");
    }

    private static async Task<CompletionItem[]?> CompleteAsync(
        CompletionHandler handler,
        string filePath,
        Position position)
    {
        return await handler.Completion(
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath) },
                Position = position,
            },
            CancellationToken.None).ConfigureAwait(false);
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

        public Dictionary<string, AssetHierarchyNodeDto[]> Hierarchies { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<AssetReferenceLocationDto> References { get; } = new();

        public Task<AssetEntryDto[]> GetAssetsAsync(AssetKind? kind, CancellationToken ct = default) =>
            Task.FromResult(Assets.Where(asset => kind is null || asset.Kind == kind).ToArray());

        public Task<AssetHierarchyNodeDto[]> GetHierarchyAsync(
            string assetPath, int maxDepth, bool includeSerializedValues, CancellationToken ct = default) =>
            Task.FromResult(Hierarchies.TryGetValue(assetPath, out var hierarchy)
                ? hierarchy
                : Array.Empty<AssetHierarchyNodeDto>());

        public Task<AssetReferenceResultDto> FindReferencesAsync(
            AssetReferenceQueryDto query, CancellationToken ct = default) =>
            Task.FromResult(new AssetReferenceResultDto { Locations = References.ToArray() });

        public Task<BuildSceneDto[]> GetBuildScenesAsync(CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<BuildSceneDto>());

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

        public Task<AssetWorkspaceEditDto> ComputeRenameEditsAsync(AssetRenameQueryDto query, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetDocumentLinksDto> GetDocumentLinksAsync(string assetPath, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetHoverDto?> GetHoverAsync(string assetPath, int line, int character, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AssetLocationDto?> GetDefinitionAsync(string assetPath, int line, int character, CancellationToken ct = default) =>
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
