using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class AssetReferencesAndCodeLensTests
{
    [Fact]
    public async Task References_OnPlayerController_ReturnsPrefabSceneAndCSharpReferences()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");
            var consumerPath = Path.Combine(dummyPath, "Assets", "Scripts", $"PlayerConsumer_{Guid.NewGuid():N}.cs");

            // Add an in-memory C# file referencing PlayerController so C# usages and asset usages are returned together.
            server.WorkspaceManager!.OpenOrUpdateDocument(
                consumerPath,
                """
                namespace Game.Runtime
                {
                    public static class PlayerConsumer
                    {
                        public static int ReadHealth(PlayerController controller) => controller.GetConfiguredHealth();
                    }
                }
                """);

            var pos = FindPosition(playerControllerPath, "public sealed class PlayerController", "PlayerController");
            var locations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentReferencesName,
                new ReferenceParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerControllerPath) },
                    Position = pos,
                    Context = new ReferenceContext { IncludeDeclaration = true },
                });

            Assert.NotNull(locations);
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, playerControllerPath) &&
                loc.Range.Start.Line == pos.Line);
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, consumerPath));
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, playerPrefabPath) &&
                loc.Range.Start.Line == 45);
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, mainScenePath) &&
                loc.Range.Start.Line == 54);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task References_OnPlayerControllerConfig_ReturnsPrefabAndMainSceneOverride()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");

            var pos = FindPosition(playerControllerPath, "[SerializeField] private GameConfig config", "config =");
            var locations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentReferencesName,
                new ReferenceParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerControllerPath) },
                    Position = pos,
                    Context = new ReferenceContext { IncludeDeclaration = true },
                });

            Assert.NotNull(locations);
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, playerControllerPath) &&
                loc.Range.Start.Line == 11);
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, playerPrefabPath) &&
                loc.Range.Start.Line == 48);
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, mainScenePath) &&
                loc.Range.Start.Line == 43);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task References_OnPlayerControllerApplyDamage_FindsUnityEventCallInPrefab()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var playerControllerUri = new Uri(playerControllerPath);

            // Open PlayerController.cs in-memory with ApplyDamage added to PlayerController (without modifying disk).
            const string modifiedSource = """
                using UnityEngine;
                using Game.Data;

                namespace Game.Runtime
                {
                    public sealed class PlayerController : MonoBehaviour
                    {
                        [SerializeField] private GameConfig config = null!;

                        public int GetConfiguredHealth()
                        {
                            return config.MaxHealth;
                        }

                        public void ApplyDamage(int amount)
                        {
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
                        Uri = playerControllerUri,
                        LanguageId = "csharp",
                        Version = 1,
                        Text = modifiedSource,
                    },
                });

            var pos = FindPositionInText(modifiedSource, "public void ApplyDamage", "ApplyDamage");
            var locations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentReferencesName,
                new ReferenceParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = playerControllerUri },
                    Position = pos,
                    Context = new ReferenceContext { IncludeDeclaration = true },
                });

            Assert.NotNull(locations);
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, playerControllerPath) &&
                loc.Range.Start.Line == pos.Line);
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, playerPrefabPath) &&
                loc.Range.Start.Line == 54);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task References_OnNonUnityTypeMethodApplyDamage_DoesNotReturnPrefabUnityEvent()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");

            var pos = FindPosition(playerControllerPath, "public void ApplyDamage", "ApplyDamage");
            var locations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentReferencesName,
                new ReferenceParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerControllerPath) },
                    Position = pos,
                    Context = new ReferenceContext { IncludeDeclaration = false },
                });

            Assert.NotNull(locations);
            Assert.DoesNotContain(locations, loc => PathsEqual(loc.Uri.LocalPath, playerPrefabPath));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task CodeLens_OnPlayerController_IncludesPrefabAndSceneReferenceCounts()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");

            var lenses = await client.InvokeWithParameterObjectAsync<CodeLens[]?>(
                Methods.TextDocumentCodeLensName,
                new CodeLensParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerControllerPath) },
                });

            Assert.NotNull(lenses);
            var playerPos = FindPosition(playerControllerPath, "public sealed class PlayerController", "PlayerController");
            var playerLens = Assert.Single(lenses, lens => lens.Range.Start.Line == playerPos.Line);
            Assert.NotNull(playerLens.Command);
            Assert.Equal("2 references (1 prefabs, 1 scenes)", playerLens.Command!.Title);
            Assert.NotNull(playerLens.Command.Arguments);
            Assert.Equal(3, playerLens.Command.Arguments!.Length);

            var locations = JToken.FromObject(playerLens.Command.Arguments[2]).ToObject<LspLocation[]>();
            Assert.NotNull(locations);
            Assert.Equal(2, locations!.Length);
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, playerPrefabPath) &&
                loc.Range.Start.Line == 45);
            Assert.Contains(locations, loc =>
                PathsEqual(loc.Uri.LocalPath, mainScenePath) &&
                loc.Range.Start.Line == 54);

            // Non-Unity type in the same file should not include prefab/scene counts.
            var enemyPos = FindPosition(playerControllerPath, "public sealed class Enemy", "Enemy");
            var enemyLens = Assert.Single(lenses, lens => lens.Range.Start.Line == enemyPos.Line);
            Assert.NotNull(enemyLens.Command);
            Assert.DoesNotContain("prefabs", enemyLens.Command!.Title, StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        return string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static Position FindPosition(string filePath, string lineNeedle, string token)
    {
        return FindPositionInText(File.ReadAllText(filePath), lineNeedle, token);
    }

    private static Position FindPositionInText(string text, string lineNeedle, string token)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(lineNeedle, StringComparison.Ordinal))
            {
                continue;
            }

            var ch = lines[i].IndexOf(token, StringComparison.Ordinal);
            if (ch >= 0)
            {
                return new Position(i, ch);
            }
        }

        throw new InvalidOperationException($"Could not find '{lineNeedle}' / '{token}'.");
    }

    [Fact]
    public async Task References_WithIncludeAndExcludePatterns_FiltersCSharpAndAssetLocations()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");

            var pos = FindPosition(playerControllerPath, "public sealed class PlayerController", "PlayerController");

            var prefabOnly = JObject.FromObject(new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerControllerPath) },
                Position = pos,
                Context = new ReferenceContext { IncludeDeclaration = true },
            });
            prefabOnly["includePatterns"] = new JArray("*.prefab");

            var prefabLocations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentReferencesName,
                prefabOnly);

            Assert.NotNull(prefabLocations);
            Assert.All(prefabLocations, loc => Assert.True(loc.Uri.LocalPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(prefabLocations, loc => PathsEqual(loc.Uri.LocalPath, playerPrefabPath));

            var noScene = JObject.FromObject(new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerControllerPath) },
                Position = pos,
                Context = new ReferenceContext { IncludeDeclaration = true },
            });
            noScene["excludePatterns"] = new JArray("*.unity");

            var filteredLocations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentReferencesName,
                noScene);

            Assert.NotNull(filteredLocations);
            Assert.DoesNotContain(filteredLocations, loc => PathsEqual(loc.Uri.LocalPath, mainScenePath));
            Assert.Contains(filteredLocations, loc => PathsEqual(loc.Uri.LocalPath, playerPrefabPath));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    private static async Task<(LspJsonRpcSession Server, JsonRpc Client, string DummyPath)> CreateInitializedSessionAsync()
    {
        var dummyPath = GetDummyProjectPath();
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
            new InitializeParams
            {
                RootUri = new Uri(dummyPath),
                Capabilities = new ClientCapabilities(),
            });

        return (server, client, dummyPath);
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
