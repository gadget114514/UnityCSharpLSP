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
        Assert.Equal(TextDocumentSyncKind.Full, result.Capabilities.TextDocumentSync?.Change);

        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);

        var exitCode = await server.WaitForExitAsync();
        Assert.Equal(0, exitCode);
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
}
