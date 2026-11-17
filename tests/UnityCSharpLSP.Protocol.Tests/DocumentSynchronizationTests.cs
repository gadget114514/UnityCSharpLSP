using System.Threading.Channels;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Handlers;
using UnityCSharpLSP.Protocol.Models;
using Xunit;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynLocation = Microsoft.CodeAnalysis.Location;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class DocumentSynchronizationTests
{
    [Fact]
    public async Task DidChange_AppliesSequentialIncrementalAndFullChanges_AndPublishesVersions()
    {
        var (server, client, dummyPath, diagnostics) = await CreateInitializedSessionAsync();
        try
        {
            var uri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "Data", "GameConfig.cs"));
            const string originalText = "public class GameConfig\n{\n    public int Value;\n}\n";
            await OpenDocumentAsync(client, uri, originalText, version: 10);

            var opened = await diagnostics.ReadNextAsync();
            Assert.Equal(10, opened.Version);
            Assert.Empty(opened.Diagnostics);

            await client.NotifyWithParameterObjectAsync(
                Methods.TextDocumentDidChangeName,
                new DidChangeTextDocumentParams
                {
                    TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = 11 },
                    ContentChanges = new[]
                    {
                        new TextDocumentContentChangeEvent
                        {
                            Range = new Microsoft.VisualStudio.LanguageServer.Protocol.Range
                            {
                                Start = new Position(2, 15),
                                End = new Position(2, 20),
                            },
                            Text = "LongerValue",
                        },
                        new TextDocumentContentChangeEvent
                        {
                            Range = new Microsoft.VisualStudio.LanguageServer.Protocol.Range
                            {
                                Start = new Position(2, 15),
                                End = new Position(2, 26),
                            },
                            Text = "FinalValue",
                        },
                    },
                });

            var incrementallyChanged = await diagnostics.ReadNextAsync();
            Assert.Equal(11, incrementallyChanged.Version);
            Assert.Equal(
                "public class GameConfig\n{\n    public int FinalValue;\n}\n",
                await GetDocumentTextAsync(server, uri.LocalPath));

            const string fullReplacement = "public class GameConfig { public int Replacement; }";
            await client.NotifyWithParameterObjectAsync(
                Methods.TextDocumentDidChangeName,
                new DidChangeTextDocumentParams
                {
                    TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = 12 },
                    ContentChanges = new[]
                    {
                        new TextDocumentContentChangeEvent { Text = fullReplacement },
                    },
                });

            var fullyChanged = await diagnostics.ReadNextAsync();
            Assert.Equal(12, fullyChanged.Version);
            Assert.Equal(fullReplacement, await GetDocumentTextAsync(server, uri.LocalPath));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task DidSave_WithText_UpdatesDocumentAndPublishesDiagnostics()
    {
        var (server, client, dummyPath, diagnostics) = await CreateInitializedSessionAsync();
        try
        {
            var uri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "Data", "GameConfig.cs"));
            await OpenDocumentAsync(client, uri, "public class GameConfig { }", version: 20);
            Assert.Equal(20, (await diagnostics.ReadNextAsync()).Version);

            const string invalidText = "public class GameConfig { invalid_syntax_error }";
            await client.NotifyWithParameterObjectAsync(
                Methods.TextDocumentDidSaveName,
                new DidSaveTextDocumentParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Text = invalidText,
                });

            var saved = await diagnostics.ReadNextAsync();
            Assert.Equal(uri, saved.Uri);
            Assert.Equal(20, saved.Version);
            Assert.NotEmpty(saved.Diagnostics);
            Assert.Equal(invalidText, await GetDocumentTextAsync(server, uri.LocalPath));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task DidSave_WithoutText_ReloadsDiskContent()
    {
        var className = "DidSaveFromDisk" + Guid.NewGuid().ToString("N");
        var dummyPath = GetDummyProjectPath();
        var filePath = Path.Combine(dummyPath, "Assets", "Scripts", "Data", className + ".cs");
        var diskText = "namespace Game.Data { public class " + className + " { public int Value { get; set; } } }";
        File.WriteAllText(filePath, diskText);

        var (server, client, _, diagnostics) = await CreateInitializedSessionAsync();
        try
        {
            var uri = new Uri(filePath);
            var invalidText = "namespace Game.Data { public class " + className + " { invalid_syntax_error } }";
            await OpenDocumentAsync(client, uri, invalidText, version: 31);
            Assert.NotEmpty((await diagnostics.ReadNextAsync()).Diagnostics);

            await client.NotifyWithParameterObjectAsync(
                Methods.TextDocumentDidSaveName,
                new DidSaveTextDocumentParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                });

            var saved = await diagnostics.ReadNextAsync();
            Assert.Equal(31, saved.Version);
            Assert.Empty(saved.Diagnostics);
            Assert.Equal(diskText, await GetDocumentTextAsync(server, filePath));
        }
        finally
        {
            server.WorkspaceManager?.RemoveDocument(filePath);
            File.Delete(filePath);
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task DidClose_PublishesEmptyDiagnosticsAndClearsVersion()
    {
        var (server, client, dummyPath, diagnostics) = await CreateInitializedSessionAsync();
        try
        {
            var uri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "Data", "GameConfig.cs"));
            await OpenDocumentAsync(client, uri, "public class GameConfig { invalid_syntax_error }", version: 42);
            Assert.NotEmpty((await diagnostics.ReadNextAsync()).Diagnostics);
            Assert.Equal(42, server.WorkspaceManager?.GetDocumentVersion(uri.LocalPath));

            await client.NotifyWithParameterObjectAsync(
                Methods.TextDocumentDidCloseName,
                new DidCloseTextDocumentParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                });

            var closed = await diagnostics.ReadNextAsync();
            Assert.Equal(uri, closed.Uri);
            Assert.Empty(closed.Diagnostics);
            Assert.Null(closed.Version);
            Assert.Null(server.WorkspaceManager?.GetDocumentVersion(uri.LocalPath));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public void ConvertDiagnostics_SkipsDiagnosticsWithoutSourceLocation()
    {
        var descriptor = new Microsoft.CodeAnalysis.DiagnosticDescriptor(
            "TEST001",
            "Test diagnostic",
            "Test message",
            "Test",
            Microsoft.CodeAnalysis.DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        var noLocation = RoslynDiagnostic.Create(descriptor, RoslynLocation.None);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class C { }", path: "Test.cs");
        var sourceLocation = RoslynDiagnostic.Create(
            descriptor,
            RoslynLocation.Create(tree, new TextSpan(0, 5)));

        var converted = DiagnosticsHandler.ConvertDiagnostics(new[] { noLocation, sourceLocation });

        var diagnostic = Assert.Single(converted);
        Assert.Equal("Test message", diagnostic.Message);
        Assert.Equal(0, diagnostic.Range.Start.Line);
        Assert.Equal(0, diagnostic.Range.Start.Character);
    }

    private static async Task OpenDocumentAsync(JsonRpc client, Uri uri, string text, int version)
    {
        await client.NotifyWithParameterObjectAsync(
            Methods.TextDocumentDidOpenName,
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = uri,
                    LanguageId = "csharp",
                    Version = version,
                    Text = text,
                },
            });
    }

    private static async Task<string> GetDocumentTextAsync(LspJsonRpcSession server, string filePath)
    {
        var document = server.WorkspaceManager?.GetDocument(filePath);
        Assert.NotNull(document);
        return (await document.GetTextAsync()).ToString();
    }

    private static async Task<(
        LspJsonRpcSession Server,
        JsonRpc Client,
        string DummyPath,
        DiagnosticsCollector Diagnostics)> CreateInitializedSessionAsync()
    {
        var dummyPath = GetDummyProjectPath();
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var server = new LspJsonRpcSession(serverStream, serverStream);
        server.Start();

        var clientHandler = new HeaderDelimitedMessageHandler(clientStream, clientStream, new JsonMessageFormatter());
        var client = new JsonRpc(clientHandler);
        var diagnostics = new DiagnosticsCollector();
        client.AddLocalRpcTarget(diagnostics);
        client.StartListening();

        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new InitializeParams
            {
                RootUri = new Uri(dummyPath),
                Capabilities = new ClientCapabilities(),
            });

        return (server, client, dummyPath, diagnostics);
    }

    private static async Task ShutdownAsync(LspJsonRpcSession server, JsonRpc client)
    {
        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);
        await server.WaitForExitAsync();
        client.Dispose();
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
        private readonly Channel<VersionedPublishDiagnosticsParams> _notifications =
            Channel.CreateUnbounded<VersionedPublishDiagnosticsParams>();

        [JsonRpcMethod(Methods.TextDocumentPublishDiagnosticsName, UseSingleObjectParameterDeserialization = true)]
        public void OnPublishDiagnostics(VersionedPublishDiagnosticsParams diagnostics)
        {
            _notifications.Writer.TryWrite(diagnostics);
        }

        public Task<VersionedPublishDiagnosticsParams> ReadNextAsync()
        {
            return _notifications.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
