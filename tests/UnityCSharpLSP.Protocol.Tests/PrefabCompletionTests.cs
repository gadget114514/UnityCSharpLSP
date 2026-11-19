using Microsoft.VisualStudio.LanguageServer.Protocol;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Parsing;
using UnityCSharpLSP.Protocol.Handlers;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class PrefabCompletionTests
{
    private const string PrefabPath = "Assets/Prefabs/Player.prefab";
    private const string PlayerControllerGuid = "c5c9d31933d241c4b9047a5783f7f380";

    [Fact]
    public async Task Completion_AfterGetComponentLessThan_ReturnsResolvableComponentTypesOnly()
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

        var (handler, ws, _) = CreateHandler(source);
        using (ws)
        {
            var items = await CompleteAsync(handler, ScriptPath(), PositionAfter(source, "GetComponent<Re"));

            Assert.NotNull(items);
            Assert.Contains(items!, item =>
                item.Label == "RectTransform" &&
                item.Kind == CompletionItemKind.Class &&
                item.Detail == $"from {PrefabPath}");
            Assert.Contains(items!, item =>
                item.Label == "PlayerController" &&
                item.Kind == CompletionItemKind.Class &&
                item.Detail == $"from {PrefabPath}");

            Assert.DoesNotContain(items!, item => item.Label == "MysteryComponent");
            Assert.DoesNotContain(items!, item => item.Label == "Component_987654");
            Assert.True(items!.Length <= 200);
        }
    }

    [Fact]
    public void UnityComponentTypeMap_UnknownAndUnresolvableComponents_ProduceNoTypeName()
    {
        Assert.True(UnityComponentTypeMap.TryGetTypeName(
            new AssetComponentSummaryDto { ClassId = 4, TypeName = "Transform" }, out var transform));
        Assert.Equal("Transform", transform);

        Assert.True(UnityComponentTypeMap.TryGetTypeName(
            new AssetComponentSummaryDto { ClassId = 224, TypeName = "RectTransform" }, out var rectTransform));
        Assert.Equal("RectTransform", rectTransform);

        Assert.True(UnityComponentTypeMap.TryGetTypeName(
            new AssetComponentSummaryDto
            {
                ClassId = 114,
                TypeName = "PlayerController",
                EditorClassIdentifier = "Game.Runtime::Game.Runtime.PlayerController",
            }, out var monoBehaviour));
        Assert.Equal("PlayerController", monoBehaviour);

        Assert.False(UnityComponentTypeMap.TryGetTypeName(
            new AssetComponentSummaryDto
            {
                ClassId = 987654,
                TypeName = "Component_987654",
                EditorClassIdentifier = "Ghost::MysteryComponent",
            }, out _));
        Assert.False(UnityComponentTypeMap.TryGetTypeName(
            new AssetComponentSummaryDto { ClassId = 114, TypeName = "Missing Script" }, out _));
        Assert.False(UnityComponentTypeMap.TryGetTypeName(null, out _));
    }

    [Fact]
    public async Task Completion_InMonoBehaviourScope_MergesPrefabGameObjectNamesWithoutOverridingRoslynSymbols()
    {
        const string source = """
            using UnityEngine;

            namespace Game.Runtime
            {
                public sealed class PlayerController : MonoBehaviour
                {
                    public void Probe()
                    {
                        var Player = 1;
                        var slot = Wea
                    }
                }
            }
            """;

        var (handler, ws, _) = CreateHandler(source);
        using (ws)
        {
            var items = await CompleteAsync(handler, ScriptPath(), PositionAfter(source, "Wea"));

            Assert.NotNull(items);
            Assert.Contains(items!, item =>
                item.Label == "WeaponSlot" &&
                item.Kind == CompletionItemKind.Variable &&
                item.Detail == $"GameObject in {PrefabPath}");
            Assert.DoesNotContain(items!, item =>
                item.Label == "Player" && item.Detail == $"GameObject in {PrefabPath}");
        }
    }

    [Fact]
    public async Task Completion_CSharpMemberAccess_StillReturnsRoslynMembers()
    {
        const string source = """
            namespace Game.Runtime
            {
                public sealed class Probe
                {
                    public void Run()
                    {
                        var config = new Game.Data.GameConfig();
                        config.
                    }
                }
            }
            """;

        var (handler, ws, _) = CreateHandler(source);
        using (ws)
        {
            var items = await CompleteAsync(handler, ScriptPath(), PositionAfter(source, "config."));

            Assert.NotNull(items);
            Assert.Contains(items!, item => item.Label == "MaxHealth");
            Assert.Contains(items!, item => item.Label == "MoveSpeed");
        }
    }

    [Fact]
    public async Task Completion_InsideSceneManagerLoadScene_StillReturnsBuildScenes()
    {
        const string source = """
            using UnityEngine.SceneManagement;

            namespace Game.Runtime
            {
                public sealed class SceneLoader
                {
                    public void Load()
                    {
                        SceneManager.LoadScene("
                    }
                }
            }
            """;

        var (handler, ws, asset) = CreateHandler(source);
        asset.BuildScenes.Add(new BuildSceneDto
        {
            BuildIndex = 0,
            Enabled = true,
            SceneName = "MainScene",
            RelativePath = "Assets/Scenes/MainScene.unity",
            FullPath = Path.Combine(FindDummyProjectRoot(), "Assets", "Scenes", "MainScene.unity"),
        });

        using (ws)
        {
            var items = await CompleteAsync(handler, ScriptPath(), PositionAfter(source, "SceneManager.LoadScene(\""));

            Assert.NotNull(items);
            Assert.Contains(items!, item => item.Label == "MainScene" && item.Kind == CompletionItemKind.File);
        }
    }

    [Fact]
    public async Task Completion_OnPrefabScriptGuid_StillReturnsGuidInsertions()
    {
        var (handler, ws, _) = CreateHandler("class Probe { }");
        using (ws)
        {
            string prefabPath = Path.Combine(FindDummyProjectRoot(), PrefabPath.Replace('/', Path.DirectorySeparatorChar));
            string[] lines = await File.ReadAllLinesAsync(prefabPath);
            int lineIndex = Array.FindIndex(lines, line => line.Contains("m_Script:", StringComparison.Ordinal));
            Assert.True(lineIndex >= 0);

            int character = lines[lineIndex].IndexOf("guid:", StringComparison.Ordinal) + "guid:".Length;
            while (character < lines[lineIndex].Length && lines[lineIndex][character] == ' ')
            {
                character++;
            }

            var items = await CompleteAsync(handler, prefabPath, new Position(lineIndex, character + 4));

            Assert.NotNull(items);
            var item = Assert.Single(items!, i => i.Label == "PlayerController");
            Assert.Equal(PlayerControllerGuid, item.InsertText);
            Assert.Equal("Assets/Scripts/PlayerController.cs", item.Detail);
        }
    }

    private static (CompletionHandler Handler, UnityWorkspaceManager Ws, FakeAssetService Asset) CreateHandler(
        string source)
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
            Guid = PlayerControllerGuid,
            RelativePath = "Assets/Scripts/PlayerController.cs",
            FullPath = ScriptPath(),
            Kind = AssetKind.CSharpScript,
        });
        asset.Hierarchy.Add(CreateHierarchy());
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

    private static AssetHierarchyNodeDto CreateHierarchy()
    {
        var transform = new AssetComponentSummaryDto { ClassId = 4, TypeName = "Transform" };
        return new AssetHierarchyNodeDto
        {
            Name = "Player",
            Components = new[]
            {
                transform,
                new AssetComponentSummaryDto { ClassId = 224, TypeName = "RectTransform" },
                new AssetComponentSummaryDto
                {
                    ClassId = 114,
                    TypeName = "PlayerController",
                    ScriptPath = "Assets/Scripts/PlayerController.cs",
                    EditorClassIdentifier = "Game.Runtime::Game.Runtime.PlayerController",
                },
                new AssetComponentSummaryDto
                {
                    ClassId = 987654,
                    TypeName = "Component_987654",
                    EditorClassIdentifier = "Ghost::MysteryComponent",
                },
            },
            Children = new[]
            {
                new AssetHierarchyNodeDto
                {
                    Name = "WeaponSlot",
                    Components = new[] { transform },
                },
            },
        };
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

        public List<AssetHierarchyNodeDto> Hierarchy { get; } = new();

        public List<AssetReferenceLocationDto> References { get; } = new();

        public List<BuildSceneDto> BuildScenes { get; } = new();

        public Task<AssetEntryDto[]> GetAssetsAsync(AssetKind? kind, CancellationToken ct = default) =>
            Task.FromResult(Assets.Where(asset => kind is null || asset.Kind == kind).ToArray());

        public Task<AssetHierarchyNodeDto[]> GetHierarchyAsync(
            string assetPath, int maxDepth, bool includeSerializedValues, CancellationToken ct = default) =>
            Task.FromResult(Hierarchy.ToArray());

        public Task<AssetReferenceResultDto> FindReferencesAsync(
            AssetReferenceQueryDto query, CancellationToken ct = default) =>
            Task.FromResult(new AssetReferenceResultDto { Locations = References.ToArray() });

        public Task<BuildSceneDto[]> GetBuildScenesAsync(CancellationToken ct = default) =>
            Task.FromResult(BuildScenes.ToArray());

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
