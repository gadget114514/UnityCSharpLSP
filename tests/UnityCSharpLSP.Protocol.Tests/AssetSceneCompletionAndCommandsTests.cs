using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.Protocol.Handlers;
using Xunit;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class AssetSceneCompletionAndCommandsTests
{
    private const string PlayerControllerGuid = "c5c9d31933d241c4b9047a5783f7f380";

    [Fact]
    public async Task Completion_InsideSceneManagerLoadScene_ReturnsEnabledBuildScenes()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var scriptPath = Path.Combine(dummyPath, "Assets", "Scripts", $"SceneLoader_{Guid.NewGuid():N}.cs");
            var scriptUri = new Uri(scriptPath);
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");

            const string source = """
                using UnityEngine.SceneManagement;

                namespace Game.Runtime
                {
                    public sealed class SceneLoader
                    {
                        public void Load()
                        {
                            SceneManager.LoadScene("");
                            SceneManager.LoadSceneAsync("Main");
                            SceneManager.LoadScene("MainScene");
                            OtherHelper.Log("");
                        }
                    }

                    public static class OtherHelper
                    {
                        public static void Log(string msg) { }
                    }
                }
                """;

            await client.NotifyWithParameterObjectAsync(
                Methods.TextDocumentDidOpenName,
                new DidOpenTextDocumentParams
                {
                    TextDocument = new TextDocumentItem
                    {
                        Uri = scriptUri,
                        LanguageId = "csharp",
                        Version = 1,
                        Text = source,
                    },
                });

            // 1. Inside empty quotes of SceneManager.LoadScene("")
            var emptyPos = FindPositionAfter(source, "SceneManager.LoadScene(\"", "\"");
            var emptyItems = await client.InvokeWithParameterObjectAsync<CompletionItem[]?>(
                Methods.TextDocumentCompletionName,
                new CompletionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = scriptUri },
                    Position = emptyPos,
                });

            Assert.NotNull(emptyItems);
            Assert.Contains(emptyItems!, item =>
                item.Label == "MainScene" &&
                item.Kind == CompletionItemKind.File);
            Assert.Contains(emptyItems!, item =>
                item.Label == "Assets/Scenes/MainScene.unity" &&
                item.Kind == CompletionItemKind.File &&
                item.Documentation?.Value is string doc &&
                PathsEqual(doc, mainScenePath));

            // 2. Inside partial string of SceneManager.LoadSceneAsync("Main")
            var asyncPos = FindPositionAfter(source, "SceneManager.LoadSceneAsync(\"Main", "\"Main");
            var asyncItems = await client.InvokeWithParameterObjectAsync<CompletionItem[]?>(
                Methods.TextDocumentCompletionName,
                new CompletionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = scriptUri },
                    Position = asyncPos,
                });

            Assert.NotNull(asyncItems);
            Assert.Contains(asyncItems!, item => item.Label == "MainScene" && item.Kind == CompletionItemKind.File);

            // 3. Outside closing quote of SceneManager.LoadScene("MainScene") -> should NOT return scene completions
            var afterQuotePos = FindPositionAfter(source, "SceneManager.LoadScene(\"MainScene\")", "\"MainScene\"");
            var outsideItems = await client.InvokeWithParameterObjectAsync<CompletionItem[]?>(
                Methods.TextDocumentCompletionName,
                new CompletionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = scriptUri },
                    Position = afterQuotePos,
                });

            Assert.True(outsideItems is null || !outsideItems.Any(i => i.Label == "Assets/Scenes/MainScene.unity"));

            // 4. Inside unrelated method call string literal -> should NOT return scene completions
            var unrelatedPos = FindPositionAfter(source, "OtherHelper.Log(\"", "\"");
            var unrelatedItems = await client.InvokeWithParameterObjectAsync<CompletionItem[]?>(
                Methods.TextDocumentCompletionName,
                new CompletionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = scriptUri },
                    Position = unrelatedPos,
                });

            Assert.True(unrelatedItems is null || !unrelatedItems.Any(i => i.Label == "Assets/Scenes/MainScene.unity"));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task DefinitionAndDocumentLink_OnSceneManagerLoadScene_NavigatesToUnityScene()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var scriptPath = Path.Combine(dummyPath, "Assets", "Scripts", $"SceneNav_{Guid.NewGuid():N}.cs");
            var scriptUri = new Uri(scriptPath);
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");

            const string source = """
                using UnityEngine.SceneManagement;

                namespace Game.Runtime
                {
                    public sealed class SceneNavigator
                    {
                        public void Go()
                        {
                            SceneManager.LoadScene("MainScene");
                            SceneManager.LoadSceneAsync("Assets/Scenes/MainScene.unity");
                        }
                    }
                }
                """;

            await client.NotifyWithParameterObjectAsync(
                Methods.TextDocumentDidOpenName,
                new DidOpenTextDocumentParams
                {
                    TextDocument = new TextDocumentItem
                    {
                        Uri = scriptUri,
                        LanguageId = "csharp",
                        Version = 1,
                        Text = source,
                    },
                });

            // 1. Definition on "MainScene"
            var loadScenePos = FindPosition(source, "SceneManager.LoadScene(\"MainScene\")", "MainScene");
            var defLocations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentDefinitionName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = scriptUri },
                    Position = loadScenePos,
                });

            Assert.NotNull(defLocations);
            var defLoc = Assert.Single(defLocations!);
            Assert.True(PathsEqual(defLoc.Uri.LocalPath, mainScenePath));
            Assert.Equal(0, defLoc.Range.Start.Line);
            Assert.Equal(0, defLoc.Range.Start.Character);

            // 2. Definition on "Assets/Scenes/MainScene.unity" in LoadSceneAsync
            var loadAsyncPos = FindPosition(source, "SceneManager.LoadSceneAsync(\"Assets/Scenes/MainScene.unity\")", "Assets/Scenes");
            var asyncDefLocations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentDefinitionName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = scriptUri },
                    Position = loadAsyncPos,
                });

            Assert.NotNull(asyncDefLocations);
            var asyncDefLoc = Assert.Single(asyncDefLocations!);
            Assert.True(PathsEqual(asyncDefLoc.Uri.LocalPath, mainScenePath));

            // 3. DocumentLink returns links for both LoadScene and LoadSceneAsync
            var links = await client.InvokeWithParameterObjectAsync<DocumentLink[]?>(
                Methods.TextDocumentDocumentLinkName,
                new DocumentLinkParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = scriptUri },
                });

            Assert.NotNull(links);
            Assert.Equal(2, links!.Count(l => l.Target is not null && PathsEqual(l.Target.LocalPath, mainScenePath)));
            Assert.Contains(links!, l => l.Range.Start.Line == loadScenePos.Line);
            Assert.Contains(links!, l => l.Range.Start.Line == loadAsyncPos.Line);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task ExecuteCommand_GetSceneOrPrefabHierarchy_ReturnsHierarchyNodes()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");

            var prefabHierarchy = await client.InvokeWithParameterObjectAsync<AssetHierarchyNodeDto[]?>(
                Methods.WorkspaceExecuteCommandName,
                new
                {
                    command = ExecuteCommandHandler.GetSceneOrPrefabHierarchyCommand,
                    arguments = new object[]
                    {
                        new
                        {
                            path = playerPrefabPath,
                            maxDepth = 16,
                            includeSerializedValues = true,
                        },
                    },
                });

            Assert.NotNull(prefabHierarchy);
            var root = Assert.Single(prefabHierarchy!);
            Assert.Equal("Player", root.Name);
            Assert.Contains(root.Children, child => child.Name == "WeaponSlot");
            var controller = Assert.Single(root.Components, c => c.TypeName == "PlayerController");
            Assert.NotNull(controller.SerializedFields);
            Assert.True(controller.SerializedFields!.ContainsKey("config"));

            // Also works with project-relative path
            var sceneHierarchy = await client.InvokeWithParameterObjectAsync<AssetHierarchyNodeDto[]?>(
                Methods.WorkspaceExecuteCommandName,
                new
                {
                    command = ExecuteCommandHandler.GetSceneOrPrefabHierarchyCommand,
                    arguments = new object[]
                    {
                        new
                        {
                            path = "Assets/Scenes/MainScene.unity",
                            maxDepth = 16,
                            includeSerializedValues = false,
                        },
                    },
                });

            Assert.NotNull(sceneHierarchy);
            Assert.Contains(sceneHierarchy!, n => n.Name == "GameManager");
            Assert.Contains(sceneHierarchy!, n => n.IsPrefabInstance && n.Name == "Player");
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task ExecuteCommand_FindSymbolUsagesInAssets_ReturnsLocationsByGuidAndSymbolName()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");

            // 1. Query by scriptGuid + memberKind = "Field" + memberName = "config"
            var fieldUsages = await client.InvokeWithParameterObjectAsync<AssetReferenceResultDto?>(
                Methods.WorkspaceExecuteCommandName,
                new
                {
                    command = ExecuteCommandHandler.FindSymbolUsagesInAssetsCommand,
                    arguments = new object[]
                    {
                        new
                        {
                            scriptGuid = PlayerControllerGuid,
                            memberKind = "Field",
                            memberName = "config",
                            maxResults = 500,
                        },
                    },
                });

            Assert.NotNull(fieldUsages);
            Assert.Contains(fieldUsages!.Locations, loc => PathsEqual(loc.FullPath, playerPrefabPath));
            Assert.Contains(fieldUsages.Locations, loc => PathsEqual(loc.FullPath, mainScenePath) && loc.IsOverride);

            // 2. Query by symbolName = "PlayerController" (default memberKind = Class)
            var classUsages = await client.InvokeWithParameterObjectAsync<AssetReferenceResultDto?>(
                Methods.WorkspaceExecuteCommandName,
                new
                {
                    command = ExecuteCommandHandler.FindSymbolUsagesInAssetsCommand,
                    arguments = new object[]
                    {
                        new
                        {
                            symbolName = "PlayerController",
                        },
                    },
                });

            Assert.NotNull(classUsages);
            Assert.Contains(classUsages!.Locations, loc => PathsEqual(loc.FullPath, playerPrefabPath));
            Assert.Contains(classUsages.Locations, loc => PathsEqual(loc.FullPath, mainScenePath));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task ExecuteCommand_AssetServerStatus_ReturnsStatusSummary()
    {
        var (server, client, _) = await CreateInitializedSessionAsync();
        try
        {
            var status = await client.InvokeWithParameterObjectAsync<DaemonStatusResponse?>(
                Methods.WorkspaceExecuteCommandName,
                new
                {
                    command = ExecuteCommandHandler.AssetServerStatusCommand,
                });

            Assert.NotNull(status);
            Assert.Equal("Ready", status!.State);
            Assert.True(status.TotalAssets > 0);
            Assert.True(status.TotalScripts > 0);
            Assert.True(status.TotalPrefabs >= 1);
            Assert.True(status.TotalScenes >= 1);
            Assert.False(string.IsNullOrWhiteSpace(status.DatabasePath));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    private static Position FindPosition(string source, string lineNeedle, string positionNeedle)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(lineNeedle, StringComparison.Ordinal))
            {
                continue;
            }

            var character = lines[i].IndexOf(positionNeedle, StringComparison.Ordinal);
            if (character >= 0)
            {
                return new Position(i, character);
            }
        }

        throw new InvalidOperationException($"Could not find '{lineNeedle}' / '{positionNeedle}'.");
    }

    private static Position FindPositionAfter(string source, string lineNeedle, string afterNeedle)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(lineNeedle, StringComparison.Ordinal))
            {
                continue;
            }

            var idx = lines[i].IndexOf(afterNeedle, StringComparison.Ordinal);
            if (idx >= 0)
            {
                return new Position(i, idx + afterNeedle.Length);
            }
        }

        throw new InvalidOperationException($"Could not find '{lineNeedle}' / '{afterNeedle}'.");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<(LspJsonRpcSession Server, JsonRpc Client, string DummyPath)> CreateInitializedSessionAsync()
    {
        var rootPath = GetDummyProjectPath();
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var server = new LspJsonRpcSession(serverStream, serverStream);
        server.Start();

        var client = new JsonRpc(new HeaderDelimitedMessageHandler(
            clientStream,
            clientStream,
            new JsonMessageFormatter()));
        client.StartListening();

        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new
            {
                rootUri = new Uri(rootPath),
                capabilities = new JObject(),
            });

        return (server, client, rootPath);
    }

    private static async Task ShutdownAsync(LspJsonRpcSession server, JsonRpc client)
    {
        try
        {
            await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
            await client.NotifyAsync(Methods.ExitName);
            await server.WaitForExitAsync();
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    private static string GetDummyProjectPath()
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
}
