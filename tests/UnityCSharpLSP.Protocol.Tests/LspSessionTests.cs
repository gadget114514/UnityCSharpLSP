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
}
