using System.Collections.Concurrent;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol;
using UnityCSharpLSP.Protocol.Handlers;
using UnityCSharpLSP.Protocol.Models;
using Xunit;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class WorkspaceFeatureTests
{
    [Fact]
    public async Task Initialize_AdvertisesWorkspaceCapabilities()
    {
        var (server, client) = await CreateInitializedSessionAsync(new object());
        try
        {
            var result = await client.InvokeWithParameterObjectAsync<JObject>(
                Methods.InitializeName,
                new InitializeParams { Capabilities = new ClientCapabilities() });
            var capabilities = result["capabilities"]!;

            var commands = capabilities["executeCommandProvider"]?["commands"]?.ToObject<string[]>();
            Assert.NotNull(commands);
            Assert.Contains(ExecuteCommandHandler.ReloadWorkspaceCommand, commands!);
            Assert.Contains(ExecuteCommandHandler.ShowWorkspaceInfoCommand, commands!);
            Assert.Contains(ExecuteCommandHandler.GetSceneOrPrefabHierarchyCommand, commands!);
            Assert.Contains(ExecuteCommandHandler.FindSymbolUsagesInAssetsCommand, commands!);
            Assert.Contains(ExecuteCommandHandler.AssetServerStatusCommand, commands!);

            Assert.True((bool?)capabilities["textDocumentSync"]?["willSave"]);
            Assert.True((bool?)capabilities["textDocumentSync"]?["willSaveWaitUntil"]);
            Assert.Equal("utf-16", (string?)capabilities["positionEncoding"]);
            Assert.True((bool?)capabilities["workspaceSymbolProvider"]?["resolveProvider"]);
            Assert.True((bool?)capabilities["diagnosticProvider"]?["workspaceDiagnostics"]);
            Assert.True((bool?)capabilities["semanticTokensProvider"]?["full"]);
            Assert.True((bool?)capabilities["semanticTokensProvider"]?["range"]);
            Assert.NotNull(capabilities["semanticTokensProvider"]?["legend"]?["tokenTypes"]);
            Assert.Null(capabilities["semanticTokensProvider"]?["Full"]);
            Assert.NotNull(capabilities["workspace"]?["fileOperations"]?["didRename"]);
            Assert.NotNull(capabilities["workspace"]?["fileOperations"]?["didCreate"]);
            Assert.NotNull(capabilities["workspace"]?["fileOperations"]?["didDelete"]);
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task ExecuteCommand_ReloadWorkspace_RebuildsManager()
    {
        var (server, client) = await CreateInitializedSessionAsync(new object());
        try
        {
            var before = server.WorkspaceManager;
            Assert.NotNull(before);

            await client.InvokeWithParameterObjectAsync<object?>(
                Methods.WorkspaceExecuteCommandName,
                new { command = ExecuteCommandHandler.ReloadWorkspaceCommand });

            var after = server.WorkspaceManager;
            Assert.NotNull(after);
            Assert.NotSame(before, after);
            Assert.True(after!.CurrentSolution.ProjectIds.Count > 0);
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task ExecuteCommand_ShowWorkspaceInfo_ReturnsSummaryAndNotifies()
    {
        var messages = new ConcurrentQueue<JObject>();
        var (server, client) = await CreateInitializedSessionAsync(new MessageCollector(messages));
        try
        {
            var result = await client.InvokeWithParameterObjectAsync<JObject>(
                Methods.WorkspaceExecuteCommandName,
                new { command = ExecuteCommandHandler.ShowWorkspaceInfoCommand });

            Assert.NotNull(result);
            var summary = (string?)result["summary"];
            Assert.False(string.IsNullOrEmpty(summary));
            Assert.Contains("projects=", summary, StringComparison.Ordinal);
            Assert.Contains("documents=", summary, StringComparison.Ordinal);

            Assert.True(await WaitForAsync(() => !messages.IsEmpty));
            Assert.Contains("UnityCSharpLSP", messages.First()["message"]?.Value<string>());
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task DidChangeConfiguration_StoresSettingsAndControlsDiagnostics()
    {
        var diagnostics = new ConcurrentQueue<JObject>();
        var (server, client) = await CreateInitializedSessionAsync(new DiagnosticCollector(diagnostics));
        var dummyPath = GetDummyProjectPath();
        var uri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "Data", "ConfigProbe.cs"));
        try
        {
            await client.NotifyWithParameterObjectAsync(
                Methods.WorkspaceDidChangeConfigurationName,
                new { settings = new { enableDiagnostics = false, predefinedSymbols = new[] { "CUSTOM_SYMBOL" } } });

            Assert.True(await WaitForAsync(() => !server.Settings.Current.EnableDiagnostics));
            Assert.Contains("CUSTOM_SYMBOL", server.Settings.Current.PredefinedSymbols);

            await client.NotifyWithParameterObjectAsync(
                Methods.TextDocumentDidOpenName,
                new DidOpenTextDocumentParams
                {
                    TextDocument = new TextDocumentItem
                    {
                        Uri = uri,
                        LanguageId = "csharp",
                        Version = 1,
                        Text = "namespace Probe { public class Broken { invalid } }",
                    },
                });

            await Task.Delay(600);
            Assert.Empty(diagnostics);

            await client.NotifyWithParameterObjectAsync(
                Methods.WorkspaceDidChangeConfigurationName,
                new { settings = new { enableDiagnostics = true } });
            Assert.True(await WaitForAsync(() => server.Settings.Current.EnableDiagnostics));

            await client.NotifyWithParameterObjectAsync(
                Methods.TextDocumentDidChangeName,
                new DidChangeTextDocumentParams
                {
                    TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = 2 },
                    ContentChanges = new[]
                    {
                        new TextDocumentContentChangeEvent
                        {
                            Text = "namespace Probe { public class Broken { also_invalid } }",
                        },
                    },
                });

            Assert.True(await WaitForAsync(() => !diagnostics.IsEmpty, 5000));
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task WorkspaceSymbolResolve_FillsDetailAndContainerFromData()
    {
        var (server, client) = await CreateInitializedSessionAsync(new object());
        try
        {
            var symbols = await client.InvokeWithParameterObjectAsync<JObject[]?>(
                Methods.WorkspaceSymbolName,
                new WorkspaceSymbolParams { Query = "GameConfig" });
            Assert.NotNull(symbols);
            var symbol = Assert.Single(symbols!, s => (string?)s["name"] == "GameConfig");
            Assert.NotNull(symbol["data"]);

            var resolved = await client.InvokeWithParameterObjectAsync<JObject>(
                CustomLspMethods.WorkspaceSymbolResolveName,
                symbol);

            Assert.NotNull(resolved);
            Assert.False(string.IsNullOrEmpty((string?)resolved["detail"]));
            Assert.False(string.IsNullOrEmpty((string?)resolved["containerName"]));
            Assert.NotNull(resolved["kind"]);
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task WorkspaceSymbol_ReturnsLsp17ShapeWithData()
    {
        var (server, client) = await CreateInitializedSessionAsync(new object());
        try
        {
            var symbols = await client.InvokeWithParameterObjectAsync<JObject[]?>(
                Methods.WorkspaceSymbolName,
                new WorkspaceSymbolParams { Query = "GameConfig" });

            Assert.NotNull(symbols);
            var symbol = Assert.Single(symbols!, s => (string?)s["name"] == "GameConfig");
            Assert.NotNull(symbol["kind"]);
            Assert.NotNull(symbol["location"]);
            Assert.NotNull(symbol["data"]);
            Assert.NotNull(symbol["data"]?["workspaces"]);
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task WorkspaceDiagnostic_ReturnsFullThenUnchanged()
    {
        var (server, client) = await CreateInitializedSessionAsync(new object());
        try
        {
            var report = await client.InvokeWithParameterObjectAsync<JObject>(
                DiagnosticMethods.WorkspaceName,
                new { identifier = "unitycsharp" });

            var items = report["items"]?.ToObject<JObject[]>() ?? Array.Empty<JObject>();
            Assert.NotEmpty(items);
            Assert.All(items, item =>
            {
                Assert.Equal("full", (string?)item["kind"]);
                Assert.False(string.IsNullOrEmpty((string?)item["resultId"]));
                Assert.False(string.IsNullOrEmpty((string?)item["uri"]));
                Assert.NotNull(item["diagnostics"]);
            });

            var previousResultIds = items
                .Select(item => new { uri = (string?)item["uri"], value = (string?)item["resultId"] })
                .ToArray();

            var unchangedReport = await client.InvokeWithParameterObjectAsync<JObject>(
                DiagnosticMethods.WorkspaceName,
                new { identifier = "unitycsharp", previousResultIds = previousResultIds });

            var unchangedItems = unchangedReport["items"]?.ToObject<JObject[]>() ?? Array.Empty<JObject>();
            var knownUris = items.Select(item => (string?)item["uri"]).ToHashSet();
            var matching = unchangedItems
                .Where(item => knownUris.Contains((string?)item["uri"]))
                .ToArray();

            Assert.NotEmpty(matching);
            Assert.All(matching, item => Assert.Equal("unchanged", (string?)item["kind"]));
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task FileOperations_DidCreateRenameDelete_UpdateWorkspace()
    {
        var (server, client) = await CreateInitializedSessionAsync(new object());
        var dummyPath = GetDummyProjectPath();
        var dir = Path.Combine(dummyPath, "Assets", "Scripts", "Data");
        var name = "FileOps" + Guid.NewGuid().ToString("N");
        var created = Path.Combine(dir, name + ".cs");
        var renamed = Path.Combine(dir, name + "Renamed.cs");
        try
        {
            File.WriteAllText(created, $"namespace Game.Data {{ public class {name} {{}} }}");
            await client.NotifyWithParameterObjectAsync(
                CustomLspMethods.WorkspaceDidCreateFilesName,
                new { files = new[] { new { uri = created } } });

            Assert.True(await WaitForAsync(() => server.WorkspaceManager?.GetDocument(created) is not null));

            File.Move(created, renamed);
            await client.NotifyWithParameterObjectAsync(
                CustomLspMethods.WorkspaceDidRenameFilesName,
                new { files = new[] { new { oldUri = created, newUri = renamed } } });

            Assert.True(await WaitForAsync(() => server.WorkspaceManager?.GetDocument(created) is null));
            Assert.True(await WaitForAsync(() => server.WorkspaceManager?.GetDocument(renamed) is not null));

            File.Delete(renamed);
            await client.NotifyWithParameterObjectAsync(
                CustomLspMethods.WorkspaceDidDeleteFilesName,
                new { files = new[] { new { uri = renamed } } });

            Assert.True(await WaitForAsync(() => server.WorkspaceManager?.GetDocument(renamed) is null));
        }
        finally
        {
            await ShutdownAsync(client);
            TryDelete(created);
            TryDelete(renamed);
        }
    }

    [Fact]
    public async Task WillRenameFiles_ReturnsSymbolRenameEdits()
    {
        var (_, client) = await CreateInitializedSessionAsync(new object());
        try
        {
            var root = GetDummyProjectPath();
            var oldPath = Path.Combine(root, "Assets", "Scripts", "PlayerController.cs");
            var newPath = Path.Combine(root, "Assets", "Scripts", "PlayerController2.cs");

            var edit = await client.InvokeWithParameterObjectAsync<JObject>(
                CustomLspMethods.WorkspaceWillRenameFilesName,
                new { files = new[] { new { oldUri = oldPath, newUri = newPath } } });

            var uri = new Uri(Path.GetFullPath(oldPath)).AbsoluteUri;
            var textEdits = edit["changes"]?[uri]?.ToObject<JObject[]>() ?? Array.Empty<JObject>();
            Assert.NotEmpty(textEdits);
            Assert.Contains("2", string.Concat(textEdits.Select(e => (string?)e["newText"])));
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task WillCreateFiles_InsertsClassSkeleton()
    {
        var (_, client) = await CreateInitializedSessionAsync(new object());
        try
        {
            var path = Path.Combine(GetDummyProjectPath(), "Assets", "test", "CreatedByWill.cs");
            var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;

            var edit = await client.InvokeWithParameterObjectAsync<JObject>(
                CustomLspMethods.WorkspaceWillCreateFilesName,
                new { files = new[] { new { uri = uri } } });

            var textEdits = edit["changes"]?[uri]?.ToObject<JObject[]>() ?? Array.Empty<JObject>();
            var inserted = string.Concat(textEdits.Select(e => (string?)e["newText"]));
            Assert.Contains("namespace ", inserted);
            Assert.Contains("public class CreatedByWill", inserted);
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task WillDeleteFiles_ReturnsDeleteFileOperation()
    {
        var (_, client) = await CreateInitializedSessionAsync(new object());
        try
        {
            var path = Path.Combine(GetDummyProjectPath(), "Assets", "test", "LspTestProgram.cs");
            var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;

            var edit = await client.InvokeWithParameterObjectAsync<JObject>(
                CustomLspMethods.WorkspaceWillDeleteFilesName,
                new { files = new[] { new { uri = uri } } });

            var changes = edit["documentChanges"]?.ToObject<JObject[]>() ?? Array.Empty<JObject>();
            var operation = Assert.Single(changes);
            Assert.Equal("delete", (string?)operation["kind"]);
            Assert.Equal(uri, (string?)operation["uri"]);
            Assert.True((bool?)operation["options"]?["ignoreIfNotExists"]);
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task WillSave_AndWillSaveWaitUntil_AreHandled()
    {
        var (server, client) = await CreateInitializedSessionAsync(new object());
        try
        {
            var dummyPath = GetDummyProjectPath();
            var uri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs"));
            var @params = new { textDocument = new { uri = uri.OriginalString }, reason = 1 };

            await client.NotifyWithParameterObjectAsync(Methods.TextDocumentWillSaveName, @params);
            var edits = await client.InvokeWithParameterObjectAsync<TextEdit[]>(
                Methods.TextDocumentWillSaveWaitUntilName,
                @params);
            Assert.NotNull(edits);
            Assert.Empty(edits);
        }
        finally
        {
            await ShutdownAsync(client);
        }
    }

    [Fact]
    public async Task MultiRoot_RoutesDocumentsToOwningWorkspace()
    {
        var rootA = CreateMinimalProject("RootA");
        var rootB = CreateMinimalProject("RootB");
        var alphaFile = Path.Combine(rootA, "Assets", "Scripts", "Alpha.cs");
        var betaFile = Path.Combine(rootB, "Assets", "Scripts", "Beta.cs");
        var (server, client) = await CreateInitializedSessionAsync(new object(), rootA, rootB);
        try
        {
            if (server.Router is null || server.Router.Count < 2)
            {
                return;
            }

            var alphaUri = new Uri(alphaFile);
            var betaUri = new Uri(betaFile);

            Assert.Same(server.Router.GetManagerForFile(alphaFile), server.Router.AllManagers[0]);
            Assert.Same(server.Router.GetManagerForFile(betaFile), server.Router.AllManagers[1]);

            var alphaHover = await client.InvokeWithParameterObjectAsync<JObject>(
                Methods.TextDocumentHoverName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = alphaUri },
                    Position = FindPosition(alphaFile, "public class Alpha", "Alpha"),
                });
            Assert.Contains("AlphaNs.Alpha", alphaHover["contents"]?["value"]?.Value<string>());

            var betaHover = await client.InvokeWithParameterObjectAsync<JObject>(
                Methods.TextDocumentHoverName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = betaUri },
                    Position = FindPosition(betaFile, "public class Beta", "Beta"),
                });
            Assert.Contains("BetaNs.Beta", betaHover["contents"]?["value"]?.Value<string>());
        }
        finally
        {
            await ShutdownAsync(client);
            TryDeleteDirectory(rootA);
            TryDeleteDirectory(rootB);
        }
    }

    private static async Task<(LspJsonRpcSession Server, JsonRpc Client)> CreateInitializedSessionAsync(
        object clientCallbackTarget,
        params string[] roots)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var server = new LspJsonRpcSession(serverStream, serverStream);
        server.Start();

        var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, new JsonMessageFormatter()));
        client.AddLocalRpcTarget(clientCallbackTarget);
        client.StartListening();

        object initParams = roots.Length == 0
            ? new { rootUri = new Uri(GetDummyProjectPath()), capabilities = new ClientCapabilities() }
            : new
            {
                workspaceFolders = roots.Select(r => new { uri = new Uri(r).OriginalString, name = Path.GetFileName(r) }).ToArray(),
                capabilities = new ClientCapabilities(),
            };

        await client.InvokeWithParameterObjectAsync<InitializeResult>(Methods.InitializeName, initParams);
        return (server, client);
    }

    private static async Task ShutdownAsync(JsonRpc client)
    {
        try
        {
            await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
            await client.NotifyAsync(Methods.ExitName);
        }
        catch
        {
            // Session may already be gone.
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return predicate();
    }

    private static Position FindPosition(string path, string lineNeedle, string token)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var needleIndex = lines[i].IndexOf(lineNeedle, StringComparison.Ordinal);
            if (needleIndex < 0) continue;
            var character = lines[i].IndexOf(token, needleIndex, StringComparison.Ordinal);
            if (character >= 0) return new Position(i, character);
        }

        throw new InvalidOperationException($"Could not find '{lineNeedle}' in {path}.");
    }

    private static string CreateMinimalProject(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "UnityCSharpLSP.MultiRoot", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Scripts"));
        File.WriteAllText(
            Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 2022.3.20f1");
        File.WriteAllText(
            Path.Combine(root, "Assets", "Scripts", "Alpha.cs"),
            "namespace AlphaNs { public class Alpha { public int Value => 1; } }");
        File.WriteAllText(
            Path.Combine(root, "Assets", "Scripts", "Beta.cs"),
            "namespace BetaNs { public class Beta { public int Value => 2; } }");
        return root;
    }

    private static string GetDummyProjectPath()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var dummy = Path.Combine(dir, "DummyProject");
            if (Directory.Exists(dummy)) return dummy;
            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not find DummyProject folder.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed class MessageCollector
    {
        private readonly ConcurrentQueue<JObject> _messages;
        public MessageCollector(ConcurrentQueue<JObject> messages) => _messages = messages;

        [JsonRpcMethod(Methods.WindowShowMessageName, UseSingleObjectParameterDeserialization = true)]
        public void OnMessage(JObject message) => _messages.Enqueue(message);
    }

    private sealed class DiagnosticCollector
    {
        private readonly ConcurrentQueue<JObject> _diagnostics;
        public DiagnosticCollector(ConcurrentQueue<JObject> diagnostics) => _diagnostics = diagnostics;

        [JsonRpcMethod(Methods.TextDocumentPublishDiagnosticsName, UseSingleObjectParameterDeserialization = true)]
        public void OnDiagnostics(JObject diagnostics) => _diagnostics.Enqueue(diagnostics);
    }
}
