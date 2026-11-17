using System.Collections.Concurrent;
using System.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol;
using UnityCSharpLSP.Protocol.Models;
using Xunit;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class SymbolFeatureTests
{
    [Fact]
    public async Task Initialize_AdvertisesSymbolAndCallHierarchyCapabilities()
    {
        var (server, client, _) = await CreateInitializedSessionAsync();

        var result = await client.InvokeWithParameterObjectAsync<JObject>(
            Methods.InitializeName,
            new InitializeParams { Capabilities = new ClientCapabilities() });

        Assert.NotNull(result);
        var capabilities = result["capabilities"];
        Assert.NotNull(capabilities);
        Assert.True((bool?)capabilities!["documentSymbolProvider"]);
        Assert.True((bool?)capabilities["workspaceSymbolProvider"]?["resolveProvider"]);
        Assert.True((bool?)capabilities["implementationProvider"]);
        Assert.True((bool?)capabilities["callHierarchyProvider"]);
        Assert.True((bool?)capabilities["typeDefinitionProvider"]);
        Assert.True((bool?)capabilities["declarationProvider"]);
        Assert.True((bool?)capabilities["documentHighlightProvider"]);
        Assert.True((bool?)capabilities["foldingRangeProvider"]);
        Assert.True((bool?)capabilities["selectionRangeProvider"]);
        Assert.True((bool?)capabilities["typeHierarchyProvider"]);
        Assert.True((bool?)capabilities["monikerProvider"]);
        Assert.True((bool?)capabilities["completionProvider"]?["resolveProvider"]);
        Assert.True((bool?)capabilities["renameProvider"]?["prepareProvider"]);
        var signatureTriggers = capabilities["signatureHelpProvider"]?["triggerCharacters"]?.ToObject<string[]>();
        Assert.NotNull(signatureTriggers);
        Assert.Contains("(", signatureTriggers!);
        Assert.Contains(",", signatureTriggers!);

        await ShutdownAsync(server, client);
    }

    [Fact]
    public async Task DocumentSymbol_ReturnsNestedOutline()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();

        var uri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs"));
        var symbols = await client.InvokeWithParameterObjectAsync<DocumentSymbol[]?>(
            Methods.TextDocumentDocumentSymbolName,
            new DocumentSymbolParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
            });

        Assert.NotNull(symbols);
        var all = Flatten(symbols).ToList();

        var type = Assert.Single(all, s => s.Name == "PlayerController");
        Assert.Equal(SymbolKind.Class, type.Kind);
        Assert.Contains(all, s => s.Name == "config" && s.Kind == SymbolKind.Field);
        Assert.Contains(all, s => s.Name == "GetConfiguredHealth" && s.Kind == SymbolKind.Method);

        await ShutdownAsync(server, client);
    }

    [Fact]
    public async Task WorkspaceSymbol_FindsTypeAcrossAssemblies()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();

        var symbols = await client.InvokeWithParameterObjectAsync<SymbolInformation[]?>(
            Methods.WorkspaceSymbolName,
            new WorkspaceSymbolParams { Query = "GameConfig" });

        Assert.NotNull(symbols);
        var gameConfig = Assert.Single(symbols, s => s.Name == "GameConfig");
        Assert.Equal(SymbolKind.Class, gameConfig.Kind);
        Assert.Equal(
            Path.Combine(dummyPath, "Assets", "Scripts", "Data", "GameConfig.cs"),
            gameConfig.Location.Uri.LocalPath,
            ignoreCase: true);

        await ShutdownAsync(server, client);
    }

    [Fact]
    public async Task WorkspaceSymbol_ReturnsMoreThan200MatchingSourceSymbols()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        var prefix = "CapRegression" + Guid.NewGuid().ToString("N");
        var filePath = Path.Combine(dummyPath, "Assets", "Scripts", "Data", prefix + ".cs");

        try
        {
            WriteSymbolFile(filePath, prefix, symbolCount: 220);
            Assert.NotNull(server.WorkspaceManager?.AddOrRefreshDiskFile(filePath));

            var symbols = await client.InvokeWithParameterObjectAsync<SymbolInformation[]?>(
                Methods.WorkspaceSymbolName,
                new WorkspaceSymbolParams { Query = prefix });

            Assert.NotNull(symbols);
            var generated = symbols.Where(s =>
                string.Equals(s.Location.Uri.LocalPath, filePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.Equal(220, generated.Length);
            Assert.Equal(220, generated.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            server.WorkspaceManager?.RemoveDocument(filePath);
            File.Delete(filePath);
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task WorkspaceSymbol_WithPartialResultToken_StreamsBatchesAndReturnsNull()
    {
        var progress = new ProgressCollector();
        var (server, client, dummyPath) = await CreateInitializedSessionAsync(progress);
        var prefix = "PartialRegression" + Guid.NewGuid().ToString("N");
        var filePath = Path.Combine(dummyPath, "Assets", "Scripts", "Data", prefix + ".cs");
        const string token = "workspace-symbol-progress";

        try
        {
            WriteSymbolFile(filePath, prefix, symbolCount: 125);
            Assert.NotNull(server.WorkspaceManager?.AddOrRefreshDiskFile(filePath));

            var result = await client.InvokeWithParameterObjectAsync<SymbolInformation[]?>(
                Methods.WorkspaceSymbolName,
                new { query = prefix, partialResultToken = token });

            Assert.Null(result);
            var notifications = progress.Notifications.ToArray();
            Assert.True(notifications.Length > 1, $"Expected multiple progress notifications, got {notifications.Length}.");
            Assert.All(notifications, notification => Assert.Equal(token, notification["token"]?.Value<string>()));

            var generated = notifications
                .SelectMany(n => n["value"]!.ToObject<SymbolInformation[]>()!)
                .Where(s => string.Equals(s.Location.Uri.LocalPath, filePath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Equal(125, generated.Length);
            Assert.Equal(125, generated.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            server.WorkspaceManager?.RemoveDocument(filePath);
            File.Delete(filePath);
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task GotoImplementation_InterfaceMethod_NavigatesToImplementation()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();

        var sourceUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs"));
        var (line, character) = FindPosition(sourceUri.LocalPath, "void ApplyDamage", "ApplyDamage");

        var locations = await client.InvokeWithParameterObjectAsync<Microsoft.VisualStudio.LanguageServer.Protocol.Location[]?>(
            Methods.TextDocumentImplementationName,
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = sourceUri },
                Position = new Position(line, character),
            });

        Assert.NotNull(locations);
        var implementation = Assert.Single(locations);
        Assert.Equal(sourceUri.LocalPath, implementation.Uri.LocalPath, ignoreCase: true);
        Assert.Equal(
            FindPosition(implementation.Uri.LocalPath, "public void ApplyDamage", "ApplyDamage").Line,
            implementation.Range.Start.Line);

        await ShutdownAsync(server, client);
    }

    [Fact]
    public async Task CallHierarchy_OutgoingCalls_IncludesCalledMethod()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();

        var sourceUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs"));
        var item = await PrepareAsync(client, sourceUri, "public void ApplyDamage", "ApplyDamage");
        Assert.Equal("ApplyDamage", item.Name);

        var outgoing = await client.InvokeWithParameterObjectAsync<CallHierarchyOutgoingCall[]?>(
            CallHierarchyMethods.OutgoingCallsName,
            new CallHierarchyOutgoingCallsParams { Item = item });

        Assert.NotNull(outgoing);
        var report = Assert.Single(outgoing, call => call.To.Name == "Report");
        Assert.NotEmpty(report.FromRanges);

        await ShutdownAsync(server, client);
    }

    [Fact]
    public async Task CallHierarchy_IncomingCalls_IncludesCaller()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();

        var sourceUri = new Uri(Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs"));
        var item = await PrepareAsync(client, sourceUri, "private void Report", "Report");
        Assert.Equal("Report", item.Name);

        var incoming = await client.InvokeWithParameterObjectAsync<CallHierarchyIncomingCall[]?>(
            CallHierarchyMethods.IncomingCallsName,
            new CallHierarchyIncomingCallsParams { Item = item });

        Assert.NotNull(incoming);
        var caller = Assert.Single(incoming, call => call.From.Name == "ApplyDamage");
        Assert.NotEmpty(caller.FromRanges);

        await ShutdownAsync(server, client);
    }

    private static void WriteSymbolFile(string filePath, string prefix, int symbolCount)
    {
        var text = new StringBuilder("namespace Game.Data\n{\n");
        for (var i = 0; i < symbolCount; i++)
        {
            text.Append("    public class ").Append(prefix).Append(i.ToString("D3")).AppendLine(" { }");
        }

        text.AppendLine("}");
        File.WriteAllText(filePath, text.ToString());
    }

    private sealed class ProgressCollector
    {
        public ConcurrentQueue<JObject> Notifications { get; } = new();

        [JsonRpcMethod(Methods.ProgressNotificationName, UseSingleObjectParameterDeserialization = true)]
        public void OnProgress(JObject notification)
        {
            Notifications.Enqueue(notification);
        }
    }

    private static async Task<CallHierarchyItem> PrepareAsync(
        JsonRpc client,
        Uri uri,
        string lineNeedle,
        string positionNeedle)
    {
        var (line, character) = FindPosition(uri.LocalPath, lineNeedle, positionNeedle);
        var items = await client.InvokeWithParameterObjectAsync<CallHierarchyItem[]?>(
            CallHierarchyMethods.PrepareName,
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
            });

        Assert.NotNull(items);
        return Assert.Single(items);
    }

    private static async Task<(LspJsonRpcSession Server, JsonRpc Client, string DummyPath)> CreateInitializedSessionAsync(
        object? clientCallbackTarget = null,
        JObject? clientCapabilities = null)
    {
        var dummyPath = GetDummyProjectPath();
        clientCapabilities ??= new JObject
        {
            ["textDocument"] = new JObject
            {
                ["documentSymbol"] = new JObject
                {
                    ["hierarchicalDocumentSymbolSupport"] = true,
                },
            },
        };
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var server = new LspJsonRpcSession(serverStream, serverStream);
        server.Start();

        var clientHandler = new HeaderDelimitedMessageHandler(clientStream, clientStream, new JsonMessageFormatter());
        var client = new JsonRpc(clientHandler);
        if (clientCallbackTarget is not null)
        {
            client.AddLocalRpcTarget(clientCallbackTarget);
        }

        client.StartListening();

        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new
            {
                rootUri = new Uri(dummyPath),
                capabilities = clientCapabilities,
            });

        return (server, client, dummyPath);
    }

    private static async Task ShutdownAsync(LspJsonRpcSession server, JsonRpc client)
    {
        await client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
        await client.NotifyAsync(Methods.ExitName);
        await server.WaitForExitAsync();
    }

    private static (int Line, int Character) FindPosition(string filePath, string lineNeedle, string positionNeedle)
    {
        var lines = File.ReadAllLines(filePath);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(lineNeedle, StringComparison.Ordinal))
            {
                continue;
            }

            var character = lines[i].IndexOf(positionNeedle, StringComparison.Ordinal);
            if (character >= 0)
            {
                return (i, character);
            }
        }

        throw new InvalidOperationException($"Could not find '{lineNeedle}' in {filePath}.");
    }

    private static IEnumerable<DocumentSymbol> Flatten(IEnumerable<DocumentSymbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            yield return symbol;

            if (symbol.Children is null)
            {
                continue;
            }

            foreach (var child in Flatten(symbol.Children))
            {
                yield return child;
            }
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
