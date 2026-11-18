using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Handlers;
using UnityCSharpLSP.Semantics.Analyzers;
using Xunit;
using LspDiagnostic = Microsoft.VisualStudio.LanguageServer.Protocol.Diagnostic;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class AssetRenameCodeActionAndSuppressorTests
{
    [Fact]
    public async Task Rename_OnPlayerControllerConfig_IncludesCSharpPrefabAndSceneEdits()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var mainScenePath = Path.Combine(dummyPath, "Assets", "Scenes", "MainScene.unity");

            var pos = FindPosition(playerControllerPath, "[SerializeField] private GameConfig config", "config =");
            var workspaceEdit = await client.InvokeWithParameterObjectAsync<WorkspaceEdit?>(
                Methods.TextDocumentRenameName,
                new RenameParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerControllerPath) },
                    Position = pos,
                    NewName = "gameConfig",
                });

            Assert.NotNull(workspaceEdit);
            Assert.NotNull(workspaceEdit!.Changes);

            var csharpEntry = Assert.Single(
                workspaceEdit.Changes!,
                kv => PathsEqual(new Uri(kv.Key).LocalPath, playerControllerPath));
            var prefabEntry = Assert.Single(
                workspaceEdit.Changes!,
                kv => PathsEqual(new Uri(kv.Key).LocalPath, playerPrefabPath));
            var sceneEntry = Assert.Single(
                workspaceEdit.Changes!,
                kv => PathsEqual(new Uri(kv.Key).LocalPath, mainScenePath));

            var updatedCs = ApplyTextEdits(await File.ReadAllTextAsync(playerControllerPath), csharpEntry.Value);
            Assert.Contains("[SerializeField] private GameConfig gameConfig = null!;", updatedCs);
            Assert.Contains("return gameConfig.MaxHealth;", updatedCs);

            var updatedPrefab = ApplyTextEdits(await File.ReadAllTextAsync(playerPrefabPath), prefabEntry.Value);
            Assert.Contains("  gameConfig: {fileID: 11400000, guid: a1b2c3d4e5f60718293a4b5c6d7e8f90, type: 2}", updatedPrefab);

            var updatedScene = ApplyTextEdits(await File.ReadAllTextAsync(mainScenePath), sceneEntry.Value);
            Assert.Contains("      propertyPath: gameConfig", updatedScene);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task Rename_OnPlayerControllerUnityEventMethod_IncludesPrefabEdit()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerPrefabPath = Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab");
            var playerControllerUri = new Uri(playerControllerPath);

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

            await OpenDocumentAsync(client, playerControllerUri, modifiedSource);

            var pos = FindPositionInText(modifiedSource, "public void ApplyDamage", "ApplyDamage");
            var workspaceEdit = await client.InvokeWithParameterObjectAsync<WorkspaceEdit?>(
                Methods.TextDocumentRenameName,
                new RenameParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = playerControllerUri },
                    Position = pos,
                    NewName = "TakeDamage",
                });

            Assert.NotNull(workspaceEdit);
            Assert.NotNull(workspaceEdit!.Changes);

            var csharpEntry = Assert.Single(
                workspaceEdit.Changes!,
                kv => PathsEqual(new Uri(kv.Key).LocalPath, playerControllerPath));
            var prefabEntry = Assert.Single(
                workspaceEdit.Changes!,
                kv => PathsEqual(new Uri(kv.Key).LocalPath, playerPrefabPath));

            var updatedCs = ApplyTextEdits(modifiedSource, csharpEntry.Value);
            Assert.Contains("public void TakeDamage(int amount)", updatedCs);

            var updatedPrefab = ApplyTextEdits(await File.ReadAllTextAsync(playerPrefabPath), prefabEntry.Value);
            Assert.Contains("m_MethodName: TakeDamage", updatedPrefab);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task CodeAction_OnPlayerControllerConfig_OffersFormerlySerializedAsAction()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerControllerUri = new Uri(playerControllerPath);
            var pos = FindPosition(playerControllerPath, "[SerializeField] private GameConfig config", "config =");

            var actions = await client.InvokeWithParameterObjectAsync<CodeAction[]?>(
                Methods.TextDocumentCodeActionName,
                new CodeActionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = playerControllerUri },
                    Range = new LspRange { Start = pos, End = pos },
                    Context = new CodeActionContext { Diagnostics = Array.Empty<LspDiagnostic>() },
                });

            Assert.NotNull(actions);
            var action = Assert.Single(
                actions!,
                a => a.Title == "Add [FormerlySerializedAs(\"config\")]");
            Assert.Equal(CodeActionKind.Refactor, action.Kind);
            Assert.NotNull(action.Edit?.Changes);

            var fileEdits = Assert.Single(
                action.Edit!.Changes!,
                kv => PathsEqual(new Uri(kv.Key).LocalPath, playerControllerPath));

            var original = await File.ReadAllTextAsync(playerControllerPath);
            var updated = ApplyTextEdits(original, fileEdits.Value);
            Assert.Contains("[UnityEngine.Serialization.FormerlySerializedAs(\"config\")]", updated);
            Assert.Contains("[SerializeField] private GameConfig config = null!;", updated);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task CodeAction_WhenAlreadyMarkedOrNonSerializable_DoesNotOfferFormerlySerializedAs()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerControllerUri = new Uri(playerControllerPath);

            const string modifiedSource = """
                using UnityEngine;
                using UnityEngine.Serialization;
                using Game.Data;

                namespace Game.Runtime
                {
                    public sealed class PlayerController : MonoBehaviour
                    {
                        [FormerlySerializedAs("oldConfig")]
                        [SerializeField] private GameConfig config = null!;

                        private int nonSerializedField;
                    }

                    public sealed class NonUnityClass
                    {
                        public int normalField;
                    }
                }
                """;

            await OpenDocumentAsync(client, playerControllerUri, modifiedSource);

            foreach (var (needle, token) in new[]
            {
                ("private GameConfig config", "config ="),
                ("private int nonSerializedField", "nonSerializedField"),
                ("public int normalField", "normalField"),
            })
            {
                var pos = FindPositionInText(modifiedSource, needle, token);
                var actions = await client.InvokeWithParameterObjectAsync<CodeAction[]?>(
                    Methods.TextDocumentCodeActionName,
                    new CodeActionParams
                    {
                        TextDocument = new TextDocumentIdentifier { Uri = playerControllerUri },
                        Range = new LspRange { Start = pos, End = pos },
                        Context = new CodeActionContext { Diagnostics = Array.Empty<LspDiagnostic>() },
                    });

                Assert.NotNull(actions);
                Assert.DoesNotContain(
                    actions!,
                    a => a.Title.StartsWith("Add [FormerlySerializedAs(", StringComparison.Ordinal));
            }
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task DiagnosticsSuppression_IDE0051_SuppressesOnlyUnityEventReferencedMethods()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            var playerControllerUri = new Uri(playerControllerPath);

            const string modifiedSource = """
                using UnityEngine;
                using Game.Data;

                namespace Game.Runtime
                {
                    public sealed class PlayerController : MonoBehaviour
                    {
                        [SerializeField] private GameConfig config = null!;

                        private void ApplyDamage(int amount)
                        {
                        }

                        private void UnusedHelper()
                        {
                        }
                    }

                    public sealed class Enemy
                    {
                        private void ApplyDamage(int amount)
                        {
                        }
                    }
                }
                """;

            var ws = server.WorkspaceManager!;
            ws.OpenOrUpdateDocument(playerControllerPath, modifiedSource);

            var doc = ws.GetDocument(playerControllerPath);
            Assert.NotNull(doc);

            var tree = await doc!.GetSyntaxTreeAsync();
            var semanticModel = await doc.GetSemanticModelAsync();
            Assert.NotNull(tree);
            Assert.NotNull(semanticModel);

            var root = await tree!.GetRootAsync();
            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
            Assert.Equal(3, methods.Length);

            var descriptor = new DiagnosticDescriptor(
                id: SerializeFieldSuppressor.IDE0051,
                title: "Remove unused private members",
                messageFormat: "Private member '{0}' is unused",
                category: "Style",
                defaultSeverity: Microsoft.CodeAnalysis.DiagnosticSeverity.Info,
                isEnabledByDefault: true);

            var syntheticDiagnostics = methods
                .Select(m => Microsoft.CodeAnalysis.Diagnostic.Create(
                    descriptor,
                    m.Identifier.GetLocation(),
                    m.Identifier.ValueText))
                .ToArray();

            var filtered = await DiagnosticsHandler.FilterDiagnosticsAsync(
                ws,
                semanticModel,
                syntheticDiagnostics);

            // PlayerController.ApplyDamage is referenced by UnityEvent in Player.prefab and must be suppressed.
            // PlayerController.UnusedHelper and Enemy.ApplyDamage are NOT referenced by UnityEvent and must remain.
            Assert.Equal(2, filtered.Count);
            Assert.DoesNotContain(filtered, d =>
                d.Location.SourceSpan == methods[0].Identifier.Span);
            Assert.Contains(filtered, d =>
                d.Location.SourceSpan == methods[1].Identifier.Span);
            Assert.Contains(filtered, d =>
                d.Location.SourceSpan == methods[2].Identifier.Span);
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    private static async Task OpenDocumentAsync(JsonRpc client, Uri uri, string text)
    {
        await client.NotifyWithParameterObjectAsync(
            Methods.TextDocumentDidOpenName,
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = uri,
                    LanguageId = "csharp",
                    Version = 1,
                    Text = text,
                },
            });
    }

    private static string ApplyTextEdits(string text, IEnumerable<TextEdit> edits)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        int ToOffset(Position position)
        {
            var lineStart = lineStarts[position.Line];
            return lineStart + position.Character;
        }

        var result = text;
        foreach (var edit in edits.OrderByDescending(e => ToOffset(e.Range.Start)))
        {
            var start = ToOffset(edit.Range.Start);
            var end = ToOffset(edit.Range.End);
            result = string.Concat(result.AsSpan(0, start), edit.NewText, result.AsSpan(end));
        }

        return result;
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
