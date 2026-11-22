using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol;
using Xunit;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class LspSessionTests
{
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

            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find DummyProject folder.");
    }

    private static (LspJsonRpcSession Server, JsonRpc Client) CreateSessionPair()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var server = new LspJsonRpcSession(serverStream, serverStream);
        server.Start();

        var clientHandler = new HeaderDelimitedMessageHandler(clientStream, clientStream, new JsonMessageFormatter());
        var client = new JsonRpc(clientHandler);

        return (server, client);
    }

    [Fact]
    public async Task Initialize_ReturnsCapabilities()
    {
        var (server, client) = CreateSessionPair();
        client.StartListening();

        var initParams = new InitializeParams
        {
            Capabilities = new ClientCapabilities(),
        };

        var result = await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            initParams);

        Assert.NotNull(result);
        Assert.NotNull(result.Capabilities);
        Assert.NotNull(result.Capabilities.TextDocumentSync);
        Assert.Equal(TextDocumentSyncKind.Incremental, result.Capabilities.TextDocumentSync?.Change);
        Assert.NotNull(result.Capabilities.TextDocumentSync?.Save);

        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);

        var exitCode = await server.WaitForExitAsync();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Initialize_AdvertisesUnityLspMethodsInExperimental()
    {
        var (server, client) = CreateSessionPair();
        client.StartListening();

        var result = await client.InvokeWithParameterObjectAsync<Newtonsoft.Json.Linq.JObject>(
            Methods.InitializeName,
            new InitializeParams { Capabilities = new ClientCapabilities() });

        // UnityCSharpFastLSP's proxy routes unitylsp/* to this server based on this list.
        var methods = result["capabilities"]?["experimental"]?["unitylsp"]?["methods"]?
            .Select(m => (string?)m)
            .ToArray();
        Assert.NotNull(methods);
        Assert.Contains(UnityCSharpLSP.Protocol.Models.UnityGuidMethods.GuidDefinitionName, methods!);
        Assert.Contains(UnityCSharpLSP.Protocol.Models.UnityGuidMethods.GuidReferencesName, methods!);

        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);
        Assert.Equal(0, await server.WaitForExitAsync());
    }

    private sealed class ClientCallbackTarget
    {
        private readonly TaskCompletionSource<PublishDiagnosticParams> _diagnosticsTcs;

        public ClientCallbackTarget(TaskCompletionSource<PublishDiagnosticParams> diagnosticsTcs)
        {
            _diagnosticsTcs = diagnosticsTcs;
        }

        [JsonRpcMethod(Methods.TextDocumentPublishDiagnosticsName, UseSingleObjectParameterDeserialization = true)]
        public void OnPublishDiagnostics(PublishDiagnosticParams diag)
        {
            _diagnosticsTcs.TrySetResult(diag);
        }
    }

    [Fact]
    public async Task DidOpenAndChange_PublishesDiagnostics()
    {
        var (server, client) = CreateSessionPair();

        var diagnosticsTcs = new TaskCompletionSource<PublishDiagnosticParams>();
        client.AddLocalRpcTarget(new ClientCallbackTarget(diagnosticsTcs));

        client.StartListening();

        var dummyPath = GetDummyProjectPath();
        var initParams = new InitializeParams
        {
            RootUri = new Uri(dummyPath),
            Capabilities = new ClientCapabilities(),
        };

        var initResult = await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            initParams);
        Assert.NotNull(initResult);

        // Open a file with a deliberate syntax error
        var testUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "Data", "GameConfig.cs"));
        var invalidCode = "namespace Game.Data { public class GameConfig { invalid_syntax_error } }";

        await client.NotifyWithParameterObjectAsync(
            Methods.TextDocumentDidOpenName,
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = testUri,
                    LanguageId = "csharp",
                    Version = 1,
                    Text = invalidCode,
                },
            });

        // Wait for diagnostics publication
        var timeoutTask = Task.Delay(5000);
        var completed = await Task.WhenAny(diagnosticsTcs.Task, timeoutTask);
        Assert.Same(diagnosticsTcs.Task, completed);

        var diagParams = await diagnosticsTcs.Task;
        Assert.NotNull(diagParams);
        Assert.Equal(testUri, diagParams.Uri);
        Assert.NotEmpty(diagParams.Diagnostics);
        Assert.Contains(diagParams.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // Clean shutdown
        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);

        var exitCode = await server.WaitForExitAsync();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Hover_ReturnsSignature()
    {
        var (server, client) = CreateSessionPair();
        client.StartListening();

        var dummyPath = GetDummyProjectPath();
        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new InitializeParams
            {
                RootUri = new Uri(dummyPath),
                Capabilities = new ClientCapabilities(),
            });

        var gameConfigUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "Data", "GameConfig.cs"));
        var hover = await client.InvokeWithParameterObjectAsync<Hover?>(
            Methods.TextDocumentHoverName,
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = gameConfigUri },
                Position = new Position(5, 26), // on "GameConfig" in "public sealed class GameConfig"
            });

        Assert.NotNull(hover);
        MarkupContent? markup = null;
        if (hover.Contents.Value is MarkupContent mc)
        {
            markup = mc;
        }
        else if (hover.Contents.Value is Newtonsoft.Json.Linq.JObject jo)
        {
            markup = jo.ToObject<MarkupContent>();
        }

        Assert.NotNull(markup);
        Assert.Contains("Game.Data.GameConfig", markup.Value);

        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);
        await server.WaitForExitAsync();
    }

    [Fact]
    public async Task GotoDefinition_CrossAssembly_NavigatesToDeclaration()
    {
        var (server, client) = CreateSessionPair();
        client.StartListening();

        var dummyPath = GetDummyProjectPath();
        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new InitializeParams
            {
                RootUri = new Uri(dummyPath),
                Capabilities = new ClientCapabilities(),
            });

        var playerControllerUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs"));
        var gameConfigUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "Data", "GameConfig.cs"));

        // Ask for definition of "GameConfig" referenced inside PlayerController.cs:
        // "[SerializeField] private GameConfig config = null!;" -> "GameConfig" starts at character 33
        var locations = await client.InvokeWithParameterObjectAsync<Microsoft.VisualStudio.LanguageServer.Protocol.Location[]?>(
            Methods.TextDocumentDefinitionName,
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = playerControllerUri },
                Position = new Position(7, 35),
            });

        Assert.NotNull(locations);
        Assert.NotEmpty(locations);

        var loc = locations[0];
        Assert.Equal(gameConfigUri.LocalPath, loc.Uri.LocalPath, ignoreCase: true);
        // GameConfig is declared on line 5
        Assert.Equal(5, loc.Range.Start.Line);

        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);
        await server.WaitForExitAsync();
    }

    [Fact]
    public async Task Completion_DotMemberAccess_ReturnsTypeMembers()
    {
        var (server, client) = CreateSessionPair();
        client.StartListening();

        var dummyPath = GetDummyProjectPath();
        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new InitializeParams
            {
                RootUri = new Uri(dummyPath),
                Capabilities = new ClientCapabilities(),
            });

        var playerControllerUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs"));

        // In PlayerController.cs:
        // "            return config.MaxHealth;" -> line 11: "return config." is at character 26
        var items = await client.InvokeWithParameterObjectAsync<CompletionItem[]?>(
            Methods.TextDocumentCompletionName,
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = playerControllerUri },
                Position = new Position(11, 26),
            });

        Assert.NotNull(items);
        Assert.NotEmpty(items);
        Assert.Contains(items, item => item.Label == "MaxHealth");
        Assert.Contains(items, item => item.Label == "MoveSpeed");

        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);
        await server.WaitForExitAsync();
    }

    [Fact]
    public async Task Completion_MonoBehaviour_SuggestsUnityMessages()
    {
        var (server, client) = CreateSessionPair();
        client.StartListening();

        var dummyPath = GetDummyProjectPath();
        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new InitializeParams
            {
                RootUri = new Uri(dummyPath),
                Capabilities = new ClientCapabilities(),
            });

        var playerControllerUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs"));

        // Position inside PlayerController class body (e.g. line 8: empty line inside class)
        var items = await client.InvokeWithParameterObjectAsync<CompletionItem[]?>(
            Methods.TextDocumentCompletionName,
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = playerControllerUri },
                Position = new Position(8, 8),
            });

        Assert.NotNull(items);
        Assert.NotEmpty(items);

        // Verify Unity Magic Methods are suggested with snippet templates
        Assert.Contains(items, item => item.Label == "Update" && item.Detail!.Contains("Unity Message"));
        Assert.Contains(items, item => item.Label == "Start" && item.Detail!.Contains("Unity Message"));
        Assert.Contains(items, item => item.Label == "Awake" && item.Detail!.Contains("Unity Message"));

        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);
        await server.WaitForExitAsync();
    }

    [Fact]
    public async Task FindReferences_CrossAssembly_FindsDeclarationAndUsages()
    {
        var (server, client) = CreateSessionPair();
        client.StartListening();

        var dummyPath = GetDummyProjectPath();
        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new InitializeParams
            {
                RootUri = new Uri(dummyPath),
                Capabilities = new ClientCapabilities(),
            });

        var gameConfigUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "Data", "GameConfig.cs"));
        var playerControllerUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs"));

        // Position on "MaxHealth" property in GameConfig.cs (line 10, char 22: "public int MaxHealth => ...")
        var locations = await client.InvokeWithParameterObjectAsync<Microsoft.VisualStudio.LanguageServer.Protocol.Location[]?>(
            Methods.TextDocumentReferencesName,
            new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = gameConfigUri },
                Position = new Position(10, 22),
                Context = new ReferenceContext { IncludeDeclaration = true },
            });

        Assert.NotNull(locations);
        Assert.True(locations.Length >= 2, $"Expected at least 2 locations (declaration + usage), got {locations.Length}");

        // 1. Must contain declaration in GameConfig.cs
        Assert.Contains(locations, loc =>
            string.Equals(loc.Uri.LocalPath, gameConfigUri.LocalPath, StringComparison.OrdinalIgnoreCase) &&
            loc.Range.Start.Line == 10);

        // 2. Must contain cross-assembly usage in PlayerController.cs
        Assert.Contains(locations, loc =>
            string.Equals(loc.Uri.LocalPath, playerControllerUri.LocalPath, StringComparison.OrdinalIgnoreCase));

        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);
        await server.WaitForExitAsync();
    }

    [Fact]
    public async Task PublishDiagnostics_SuppressesCS0649_ForSerializeField()
    {
        var (server, client) = CreateSessionPair();

        var diagnosticsTcs = new TaskCompletionSource<PublishDiagnosticParams>();
        client.AddLocalRpcTarget(new ClientCallbackTarget(diagnosticsTcs));
        client.StartListening();

        var dummyPath = GetDummyProjectPath();
        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new InitializeParams
            {
                RootUri = new Uri(dummyPath),
                Capabilities = new ClientCapabilities(),
            });

        var testUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "Data", "SuppressTest.cs"));
        // unassignedWithSerialize has [SerializeField], so CS0649 should be suppressed.
        // unassignedWithoutSerialize does NOT have [SerializeField], so CS0649 must be reported.
        var code = @"
using UnityEngine;

namespace Game.Data
{
    public class SuppressTest : MonoBehaviour
    {
        [SerializeField] private int unassignedWithSerialize;
        private int unassignedWithoutSerialize;

        public int GetVal() => unassignedWithoutSerialize;
    }
}
";

        await client.NotifyWithParameterObjectAsync(
            Methods.TextDocumentDidOpenName,
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = testUri,
                    LanguageId = "csharp",
                    Version = 1,
                    Text = code,
                },
            });

        var completed = await Task.WhenAny(diagnosticsTcs.Task, Task.Delay(5000));
        Assert.Same(diagnosticsTcs.Task, completed);

        var diagParams = await diagnosticsTcs.Task;
        Assert.NotNull(diagParams);

        // unassignedWithSerialize should NOT trigger CS0649
        Assert.DoesNotContain(diagParams.Diagnostics, d =>
            d.Code?.ToString() == "CS0649" && d.Message.Contains("unassignedWithSerialize"));

        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);
        await server.WaitForExitAsync();
    }

    [Fact]
    public async Task WatchedFiles_CreatedAndDeleted_UpdatesSolution()
    {
        var (server, client) = CreateSessionPair();
        client.StartListening();

        var dummyPath = GetDummyProjectPath();
        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new InitializeParams
            {
                RootUri = new Uri(dummyPath),
                Capabilities = new ClientCapabilities(),
            });

        var newFile = Path.Combine(dummyPath, "Assets", "Scripts", "DynamicNewFile.cs");
        File.WriteAllText(newFile, "namespace Game.Runtime { public class DynamicNewFile {} }");

        try
        {
            // Send Created notification
            await client.NotifyWithParameterObjectAsync(
                Methods.WorkspaceDidChangeWatchedFilesName,
                new DidChangeWatchedFilesParams
                {
                    Changes =
                    [
                        new FileEvent
                        {
                            Uri = new Uri(newFile),
                            FileChangeType = FileChangeType.Created,
                        },
                    ],
                });

            Microsoft.CodeAnalysis.Document? doc = null;
            for (int i = 0; i < 20; i++)
            {
                doc = server.WorkspaceManager?.GetDocument(newFile);
                if (doc is not null) break;
                await Task.Delay(50);
            }
            Assert.NotNull(doc);

            // Send Deleted notification
            await client.NotifyWithParameterObjectAsync(
                Methods.WorkspaceDidChangeWatchedFilesName,
                new DidChangeWatchedFilesParams
                {
                    Changes =
                    [
                        new FileEvent
                        {
                            Uri = new Uri(newFile),
                            FileChangeType = FileChangeType.Deleted,
                        },
                    ],
                });

            // Wait briefly for the notification to be dispatched and processed
            Microsoft.CodeAnalysis.Document? removedDoc = null;
            for (int i = 0; i < 20; i++)
            {
                removedDoc = server.WorkspaceManager?.GetDocument(newFile);
                if (removedDoc is null) break;
                await Task.Delay(50);
            }
            Assert.Null(removedDoc);

            await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
            await client.NotifyAsync(Methods.ExitName);
            await server.WaitForExitAsync();
        }
        finally
        {
            server.Dispose();
            client.Dispose();

            for (int i = 0; i < 5 && File.Exists(newFile); i++)
            {
                try
                {
                    File.Delete(newFile);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(50);
                }
            }
        }
    }
}
