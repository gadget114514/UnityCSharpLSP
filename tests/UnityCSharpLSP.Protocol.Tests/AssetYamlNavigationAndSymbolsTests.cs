using System.Collections.Concurrent;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using Xunit;
using LspDiagnostic = Microsoft.VisualStudio.LanguageServer.Protocol.Diagnostic;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;
using LspSymbolKind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class AssetYamlNavigationAndSymbolsTests
{
    private const string PlayerControllerGuid = "c5c9d31933d241c4b9047a5783f7f380";
    private const string PlayerPrefabGuid = "d1e2f3a4b5c64758697a8b9c0d1e2f30";

    [Fact]
    public async Task DocumentLink_OnPrefabAndScene_ReturnsAssetLinks()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");

            var prefabLinks = await client.InvokeWithParameterObjectAsync<DocumentLink[]?>(
                Methods.TextDocumentDocumentLinkName,
                new DocumentLinkParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerPrefabPath) },
                });

            Assert.NotNull(prefabLinks);
            var scriptLink = Assert.Single(prefabLinks!);
            Assert.Equal(45, scriptLink.Range.Start.Line);
            Assert.Equal(37, scriptLink.Range.Start.Character);
            Assert.Equal(69, scriptLink.Range.End.Character);
            Assert.NotNull(scriptLink.Target);
            Assert.True(PathsEqual(scriptLink.Target!.LocalPath, playerControllerPath));

            var sceneLinks = await client.InvokeWithParameterObjectAsync<DocumentLink[]?>(
                Methods.TextDocumentDocumentLinkName,
                new DocumentLinkParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(mainScenePath) },
                });

            Assert.NotNull(sceneLinks);
            Assert.Contains(sceneLinks!, link =>
                link.Target is not null &&
                PathsEqual(link.Target.LocalPath, playerPrefabPath));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task Definition_OnScriptGuidAndSerializedFields_ResolvesToCSharpDeclarations()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");

            // 1. Definition on m_Script GUID in Player.prefab -> PlayerController.cs (0, 0)
            var guidLocations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentDefinitionName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerPrefabPath) },
                    Position = new Position(45, 40),
                });

            Assert.NotNull(guidLocations);
            var guidLoc = Assert.Single(guidLocations!);
            Assert.True(PathsEqual(guidLoc.Uri.LocalPath, playerControllerPath));
            Assert.Equal(0, guidLoc.Range.Start.Line);
            Assert.Equal(0, guidLoc.Range.Start.Character);

            // 2. Definition on "config:" in Player.prefab -> config field declaration in PlayerController.cs
            var expectedFieldPos = FindPosition(playerControllerPath, "[SerializeField] private GameConfig config", "config =");
            var prefabFieldLocations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentDefinitionName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerPrefabPath) },
                    Position = new Position(48, 4),
                });

            Assert.NotNull(prefabFieldLocations);
            var prefabFieldLoc = Assert.Single(prefabFieldLocations!);
            Assert.True(PathsEqual(prefabFieldLoc.Uri.LocalPath, playerControllerPath));
            Assert.Equal(expectedFieldPos.Line, prefabFieldLoc.Range.Start.Line);
            Assert.Equal(expectedFieldPos.Character, prefabFieldLoc.Range.Start.Character);

            // 3. Definition on "propertyPath: config" in MainScene.unity -> config field declaration in PlayerController.cs
            var sceneFieldLocations = await client.InvokeWithParameterObjectAsync<LspLocation[]?>(
                Methods.TextDocumentDefinitionName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(mainScenePath) },
                    Position = new Position(43, 22),
                });

            Assert.NotNull(sceneFieldLocations);
            var sceneFieldLoc = Assert.Single(sceneFieldLocations!);
            Assert.True(PathsEqual(sceneFieldLoc.Uri.LocalPath, playerControllerPath));
            Assert.Equal(expectedFieldPos.Line, sceneFieldLoc.Range.Start.Line);
            Assert.Equal(expectedFieldPos.Character, sceneFieldLoc.Range.Start.Character);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task Hover_OnPrefabAndScene_EnrichesWithCSharpSignatureXmlDocAndTooltip()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerControllerUri = new Uri(playerControllerPath);

            const string enrichedSource = """
                using UnityEngine;
                using Game.Data;

                namespace Game.Runtime
                {
                    /// <summary>
                    /// Controls player movement and health configuration.
                    /// </summary>
                    public sealed class PlayerController : MonoBehaviour
                    {
                        /// <summary>
                        /// Active game configuration asset for the player.
                        /// </summary>
                        [Tooltip("Assign the player's GameConfig ScriptableObject.")]
                        [SerializeField] private GameConfig config = null!;

                        public int GetConfiguredHealth()
                        {
                            return config.MaxHealth;
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
                        Text = enrichedSource,
                    },
                });

            // Hover on m_Script GUID in Player.prefab
            var scriptHover = await client.InvokeWithParameterObjectAsync<Hover?>(
                Methods.TextDocumentHoverName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerPrefabPath) },
                    Position = new Position(45, 40),
                });

            Assert.NotNull(scriptHover);
            var scriptMarkdown = GetHoverMarkdown(scriptHover!);
            Assert.Contains("```csharp", scriptMarkdown);
            Assert.Contains("PlayerController", scriptMarkdown);
            Assert.Contains("Controls player movement and health configuration.", scriptMarkdown);
            Assert.Contains(PlayerControllerGuid, scriptMarkdown);

            // Hover on "config:" in Player.prefab
            var prefabFieldHover = await client.InvokeWithParameterObjectAsync<Hover?>(
                Methods.TextDocumentHoverName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerPrefabPath) },
                    Position = new Position(48, 4),
                });

            Assert.NotNull(prefabFieldHover);
            var fieldMarkdown = GetHoverMarkdown(prefabFieldHover!);
            Assert.Contains("```csharp", fieldMarkdown);
            Assert.Contains("GameConfig config", fieldMarkdown);
            Assert.Contains("Active game configuration asset for the player.", fieldMarkdown);
            Assert.Contains("Assign the player's GameConfig ScriptableObject.", fieldMarkdown);
            Assert.Contains(PlayerControllerGuid, fieldMarkdown);

            // Hover on "propertyPath: config" in MainScene.unity
            var sceneFieldHover = await client.InvokeWithParameterObjectAsync<Hover?>(
                Methods.TextDocumentHoverName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(mainScenePath) },
                    Position = new Position(43, 22),
                });

            Assert.NotNull(sceneFieldHover);
            var sceneMarkdown = GetHoverMarkdown(sceneFieldHover!);
            Assert.Contains("GameConfig config", sceneMarkdown);
            Assert.Contains("Assign the player's GameConfig ScriptableObject.", sceneMarkdown);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task Hover_OnSceneGameObjectAndComponent_IncludesSourcePath()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");

            var gameObjectHover = await client.InvokeWithParameterObjectAsync<Hover?>(
                Methods.TextDocumentHoverName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(mainScenePath) },
                    Position = new Position(12, 12),
                });

            Assert.NotNull(gameObjectHover);
            var gameObjectMarkdown = GetHoverMarkdown(gameObjectHover!);
            Assert.Contains("GameObject: GameManager", gameObjectMarkdown);
            Assert.Contains("Assets/Scenes/MainScene.unity", gameObjectMarkdown);

            var componentHover = await client.InvokeWithParameterObjectAsync<Hover?>(
                Methods.TextDocumentHoverName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(mainScenePath) },
                    Position = new Position(19, 2),
                });

            Assert.NotNull(componentHover);
            var componentMarkdown = GetHoverMarkdown(componentHover!);
            Assert.Contains("Component: Transform", componentMarkdown);
            Assert.Contains("Assets/Scenes/MainScene.unity", componentMarkdown);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task DocumentSymbol_OnPrefabAndScene_ReturnsHierarchyTree()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");

            var prefabSymbols = await client.InvokeWithParameterObjectAsync<DocumentSymbol[]?>(
                Methods.TextDocumentDocumentSymbolName,
                new DocumentSymbolParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerPrefabPath) },
                });

            Assert.NotNull(prefabSymbols);
            var root = Assert.Single(prefabSymbols!);
            Assert.Equal("Player", root.Name);
            Assert.Equal(LspSymbolKind.Object, root.Kind);
            Assert.NotNull(root.Children);

            Assert.Contains(root.Children!, c => c.Name == "Transform" && c.Kind == LspSymbolKind.Class);
            Assert.Contains(root.Children!, c =>
                c.Name == "PlayerController" &&
                c.Kind == LspSymbolKind.Class &&
                c.Detail == "Assets/Scripts/PlayerController.cs");

            var weaponSlot = Assert.Single(root.Children!, c => c.Name == "WeaponSlot");
            Assert.Equal(LspSymbolKind.Object, weaponSlot.Kind);

            var sceneSymbols = await client.InvokeWithParameterObjectAsync<DocumentSymbol[]?>(
                Methods.TextDocumentDocumentSymbolName,
                new DocumentSymbolParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(mainScenePath) },
                });

            Assert.NotNull(sceneSymbols);
            Assert.Equal(2, sceneSymbols!.Length);
            Assert.Equal("GameManager", sceneSymbols[0].Name);
            Assert.Equal(LspSymbolKind.Object, sceneSymbols[0].Kind);
            Assert.Equal("Player", sceneSymbols[1].Name);
            Assert.Equal(LspSymbolKind.Object, sceneSymbols[1].Kind);
            Assert.Equal("Assets/Prefabs/Player.prefab", sceneSymbols[1].Detail);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task Diagnostics_OnPrefabWithMissingScript_ReportsUnity0001WarningViaPullAndPush()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "UnityCSharpLSP.AssetDiagTests", Guid.NewGuid().ToString("N"));
        try
        {
            var scriptsDir = Path.Combine(tempRoot, "Assets", "Scripts");
            var prefabsDir = Path.Combine(tempRoot, "Assets", "Prefabs");
            Directory.CreateDirectory(scriptsDir);
            Directory.CreateDirectory(prefabsDir);

            await File.WriteAllTextAsync(
                Path.Combine(scriptsDir, "ValidBehaviour.cs"),
                "public class ValidBehaviour {}");
            await File.WriteAllTextAsync(
                Path.Combine(scriptsDir, "ValidBehaviour.cs.meta"),
                "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\n");

            const string missingGuid = "deadbeefdeadbeefdeadbeefdeadbeef";
            var brokenPrefabPath = Path.Combine(prefabsDir, "Broken.prefab");
            var brokenPrefabText = string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1 &100000",
                "GameObject:",
                "  m_Name: BrokenObject",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                "  m_GameObject: {fileID: 100000}",
                $"  m_Script: {{fileID: 11500000, guid: {missingGuid}, type: 3}}",
            ]);
            await File.WriteAllTextAsync(brokenPrefabPath, brokenPrefabText);
            await File.WriteAllTextAsync(
                brokenPrefabPath + ".meta",
                $"fileFormatVersion: 2\nguid: {PlayerPrefabGuid}\n");

            var collector = new DiagnosticsCollector();
            var (server, client, _) = await CreateInitializedSessionAsync(
                rootOverride: tempRoot,
                clientCallbackTarget: collector);
            try
            {
                var brokenUri = new Uri(brokenPrefabPath);

                // 1. Pull diagnostics (textDocument/diagnostic)
                var report = await client.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(
                    DiagnosticMethods.Name,
                    new DocumentDiagnosticParams
                    {
                        TextDocument = new TextDocumentIdentifier { Uri = brokenUri },
                    });

                Assert.Equal("full", report.Kind);
                Assert.False(string.IsNullOrEmpty(report.ResultId));
                Assert.NotNull(report.Diagnostics);
                var diag = Assert.Single(report.Diagnostics!);
                Assert.Equal("UNITY0001", diag.Code?.Value);
                Assert.Equal(DiagnosticSeverity.Warning, diag.Severity);
                Assert.Equal("UnityCSharpLSP", diag.Source);
                Assert.Equal(8, diag.Range.Start.Line);
                Assert.Contains("BrokenObject", diag.Message);
                Assert.Contains(missingGuid, diag.Message);

                // Repeat pull with previousResultId -> unchanged
                var unchanged = await client.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(
                    DiagnosticMethods.Name,
                    new DocumentDiagnosticParams
                    {
                        TextDocument = new TextDocumentIdentifier { Uri = brokenUri },
                        PreviousResultId = report.ResultId,
                    });
                Assert.Equal("unchanged", unchanged.Kind);
                Assert.Equal(report.ResultId, unchanged.ResultId);

                // 2. Push diagnostics via didOpen on .prefab should publish UNITY0001 and NOT add to Roslyn C# solution
                await client.NotifyWithParameterObjectAsync(
                    Methods.TextDocumentDidOpenName,
                    new DidOpenTextDocumentParams
                    {
                        TextDocument = new TextDocumentItem
                        {
                            Uri = brokenUri,
                            LanguageId = "yaml",
                            Version = 1,
                            Text = brokenPrefabText,
                        },
                    });

                var published = await collector.WaitForAsync(brokenUri);
                var pushedDiag = Assert.Single(published.Diagnostics);
                Assert.Equal("UNITY0001", pushedDiag.Code?.Value);
                Assert.Equal(DiagnosticSeverity.Warning, pushedDiag.Severity);
                Assert.Null(server.WorkspaceManager!.GetDocument(brokenPrefabPath));
            }
            finally
            {
                await ShutdownAsync(server, client);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // Ignore transient SQLite file handles during temp cleanup
                }
            }
        }
    }

    [Fact]
    public async Task Diagnostics_PrefabOverrideImpact_IsOptInForPullAndPush()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "UnityCSharpLSP.PrefabOverrideDiagTests", Guid.NewGuid().ToString("N"));
        try
        {
            var prefabsDir = Path.Combine(tempRoot, "Assets", "Prefabs");
            Directory.CreateDirectory(prefabsDir);

            const string baseGuid = "bcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbc";
            var basePrefabPath = Path.Combine(prefabsDir, "Base.prefab");
            await File.WriteAllTextAsync(basePrefabPath, string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                "  config: 1",
            ]));
            await File.WriteAllTextAsync(basePrefabPath + ".meta", $"fileFormatVersion: 2\nguid: {baseGuid}\n");

            var variantPath = Path.Combine(prefabsDir, "Variant.prefab");
            var variantText = string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1001 &300000",
                "PrefabInstance:",
                "  m_Modification:",
                "    m_Modifications:",
                $"    - target: {{fileID: 100014, guid: {baseGuid}, type: 3}}",
                "      propertyPath: config",
                "      value: 42",
            ]);
            await File.WriteAllTextAsync(variantPath, variantText);
            await File.WriteAllTextAsync(variantPath + ".meta", "fileFormatVersion: 2\nguid: cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd\n");

            var collector = new DiagnosticsCollector();
            var (server, client, _) = await CreateInitializedSessionAsync(rootOverride: tempRoot, clientCallbackTarget: collector);
            try
            {
                var variantUri = new Uri(variantPath);
                var pullParams = new DocumentDiagnosticParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = variantUri },
                };

                // Default (opt-out): neither pull nor push reports UNITY0003.
                var before = await client.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(DiagnosticMethods.Name, pullParams);
                Assert.NotNull(before.Diagnostics);
                Assert.DoesNotContain(before.Diagnostics!, d => string.Equals(d.Code?.Value as string, "UNITY0003", StringComparison.Ordinal));

                await client.NotifyWithParameterObjectAsync(
                    Methods.TextDocumentDidOpenName,
                    new DidOpenTextDocumentParams
                    {
                        TextDocument = new TextDocumentItem
                        {
                            Uri = variantUri,
                            LanguageId = "yaml",
                            Version = 1,
                            Text = variantText,
                        },
                    });
                var beforePush = await collector.WaitForAsync(variantUri);
                Assert.DoesNotContain(beforePush.Diagnostics, d => string.Equals(d.Code?.Value as string, "UNITY0003", StringComparison.Ordinal));

                // Opt in (camelCase): pull reports UNITY0003 at the propertyPath line.
                await client.NotifyWithParameterObjectAsync(
                    Methods.WorkspaceDidChangeConfigurationName,
                    new { settings = new { reportPrefabOverrideImpact = true } });
                var after = await client.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(DiagnosticMethods.Name, pullParams);
                var diag = Assert.Single(after.Diagnostics!, d => string.Equals(d.Code?.Value as string, "UNITY0003", StringComparison.Ordinal));
                Assert.Equal(7, diag.Range.Start.Line);
                Assert.Contains("Base.prefab", diag.Message);

                // Push after opt-in (didSave re-publishes for YAML assets).
                await client.NotifyWithParameterObjectAsync(
                    Methods.TextDocumentDidSaveName,
                    new DidSaveTextDocumentParams { TextDocument = new TextDocumentIdentifier { Uri = variantUri } });
                var afterPush = await collector.WaitForAsync(variantUri);
                Assert.Contains(afterPush.Diagnostics, d => string.Equals(d.Code?.Value as string, "UNITY0003", StringComparison.Ordinal));

                // Opt out via snake_case: pull hides UNITY0003 again.
                await client.NotifyWithParameterObjectAsync(
                    Methods.WorkspaceDidChangeConfigurationName,
                    new { settings = new { report_prefab_override_impact = false } });
                var afterOptOut = await client.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(DiagnosticMethods.Name, pullParams);
                Assert.DoesNotContain(afterOptOut.Diagnostics!, d => string.Equals(d.Code?.Value as string, "UNITY0003", StringComparison.Ordinal));
            }
            finally
            {
                await ShutdownAsync(server, client);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // Ignore transient SQLite file handles during temp cleanup
                }
            }
        }
    }

    [Fact]
    public async Task Diagnostics_ScenePrefabOverride_IsOptInAndNamesScene()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "UnityCSharpLSP.SceneOverrideDiagTests", Guid.NewGuid().ToString("N"));
        try
        {
            var prefabsDir = Path.Combine(tempRoot, "Assets", "Prefabs");
            var scenesDir = Path.Combine(tempRoot, "Assets", "Scenes");
            Directory.CreateDirectory(prefabsDir);
            Directory.CreateDirectory(scenesDir);

            const string baseGuid = "bcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbc";
            var basePrefabPath = Path.Combine(prefabsDir, "Base.prefab");
            await File.WriteAllTextAsync(basePrefabPath, string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                "  config: 1",
            ]));
            await File.WriteAllTextAsync(basePrefabPath + ".meta", $"fileFormatVersion: 2\nguid: {baseGuid}\n");

            var scenePath = Path.Combine(scenesDir, "MainScene.unity");
            await File.WriteAllTextAsync(scenePath, string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1001 &300000",
                "PrefabInstance:",
                "  m_Modification:",
                "    m_Modifications:",
                $"    - target: {{fileID: 100014, guid: {baseGuid}, type: 3}}",
                "      propertyPath: config",
                "      value: 42",
            ]));
            await File.WriteAllTextAsync(scenePath + ".meta", "fileFormatVersion: 2\nguid: e1f2a3b4c5d64758697a8b9c0d1e2f40\n");

            var (server, client, _) = await CreateInitializedSessionAsync(rootOverride: tempRoot);
            try
            {
                var pullParams = new DocumentDiagnosticParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(scenePath) },
                };

                var before = await client.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(DiagnosticMethods.Name, pullParams);
                Assert.DoesNotContain(before.Diagnostics!, d => string.Equals(d.Code?.Value as string, "UNITY0003", StringComparison.Ordinal));

                await client.NotifyWithParameterObjectAsync(
                    Methods.WorkspaceDidChangeConfigurationName,
                    new { settings = new { reportPrefabOverrideImpact = true } });

                var after = await client.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(DiagnosticMethods.Name, pullParams);
                var diag = Assert.Single(after.Diagnostics!, d => string.Equals(d.Code?.Value as string, "UNITY0003", StringComparison.Ordinal));
                Assert.Equal(7, diag.Range.Start.Line);
                Assert.Contains("Base.prefab in MainScene.unity", diag.Message);
            }
            finally
            {
                await ShutdownAsync(server, client);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // Ignore transient SQLite file handles during temp cleanup
                }
            }
        }
    }

    private static string GetHoverMarkdown(Hover hover)
    {
        if (hover.Contents.TryGetThird(out MarkupContent? markup) && markup is not null)
        {
            return markup.Value;
        }

        if (hover.Contents.Value is JObject jObj)
        {
            return jObj["value"]?.Value<string>() ?? string.Empty;
        }

        return hover.Contents.Value?.ToString() ?? string.Empty;
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
        var lines = File.ReadAllText(filePath).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
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

    private static async Task<(LspJsonRpcSession Server, JsonRpc Client, string RootPath)> CreateInitializedSessionAsync(
        string? rootOverride = null,
        object? clientCallbackTarget = null)
    {
        var rootPath = rootOverride ?? GetDummyProjectPath();
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var server = new LspJsonRpcSession(serverStream, serverStream);
        server.Start();

        var client = new JsonRpc(new HeaderDelimitedMessageHandler(
            clientStream,
            clientStream,
            new JsonMessageFormatter()));
        if (clientCallbackTarget is not null)
        {
            client.AddLocalRpcTarget(clientCallbackTarget);
        }

        client.StartListening();

        var capabilities = new JObject
        {
            ["textDocument"] = new JObject
            {
                ["documentSymbol"] = new JObject
                {
                    ["hierarchicalDocumentSymbolSupport"] = true,
                },
                ["hover"] = new JObject
                {
                    ["contentFormat"] = new JArray("markdown", "plaintext"),
                },
            },
        };

        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new
            {
                rootUri = new Uri(rootPath),
                capabilities,
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

    private sealed class DiagnosticsCollector
    {
        private readonly ConcurrentQueue<PublishDiagnosticParams> _received = new();
        private readonly SemaphoreSlim _signal = new(0);

        [JsonRpcMethod(Methods.TextDocumentPublishDiagnosticsName, UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(PublishDiagnosticParams @params)
        {
            _received.Enqueue(@params);
            _signal.Release();
        }

        public async Task<PublishDiagnosticParams> WaitForAsync(Uri uri, int timeoutMs = 10000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                while (_received.TryDequeue(out var item))
                {
                    if (PathsEqual(item.Uri.LocalPath, uri.LocalPath))
                    {
                        return item;
                    }
                }

                var remaining = (int)Math.Max(10, (deadline - DateTime.UtcNow).TotalMilliseconds);
                await _signal.WaitAsync(Math.Min(remaining, 200));
            }

            throw new TimeoutException($"Timed out waiting for diagnostics for {uri}.");
        }
    }
}
