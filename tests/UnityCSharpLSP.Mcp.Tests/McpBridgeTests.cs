using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Mcp;
using UnityCSharpLSP.Protocol;
using UnityCSharpLSP.Protocol.Handlers;

namespace UnityCSharpLSP.Mcp.Tests;

public sealed class McpBridgeTests
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

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find DummyProject folder.");
    }

    private static async Task<(McpBridgeServer Bridge, McpClient Client, Task ServerTask, Stream C2S, Stream S2C)>
        CreateMcpPairAsync(string workspaceRoot, JToken? initialSettings = null, TimeSpan? timeout = null)
    {
        var bridge = new McpBridgeServer(workspaceRoot, timeout ?? TimeSpan.FromSeconds(60), TextWriter.Null);
        await bridge.InitializeLspAsync(initialSettings);

        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var serverTransport = new ModelContextProtocol.Server.StreamServerTransport(
            serverStream,
            serverStream,
            "unity-csharp-lsp-test",
            NullLoggerFactory.Instance);
        var server = bridge.CreateServer(serverTransport, NullLoggerFactory.Instance);
        var serverTask = server.RunAsync(CancellationToken.None);

        var clientTransport = new StreamClientTransport(
            clientStream,
            clientStream,
            NullLoggerFactory.Instance);
        var client = await McpClient.CreateAsync(clientTransport, loggerFactory: NullLoggerFactory.Instance);

        return (bridge, client, serverTask, clientStream, serverStream);
    }

    private static async Task DisposeMcpPairAsync(
        McpBridgeServer bridge,
        McpClient client,
        Task serverTask,
        Stream clientStream,
        Stream serverStream)
    {
        await client.DisposeAsync();
        await clientStream.DisposeAsync();
        await serverStream.DisposeAsync();
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Transport closed on dispose.
        }

        await bridge.DisposeAsync();
    }

    private static (LspJsonRpcSession Server, JsonRpc Client) CreateDirectLspPair()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var server = new LspJsonRpcSession(serverStream, serverStream);
        server.Start();

        var clientHandler = new HeaderDelimitedMessageHandler(
            clientStream,
            clientStream,
            new JsonMessageFormatter());
        var client = new JsonRpc(clientHandler);
        client.StartListening();

        return (server, client);
    }

    private static string GetTextContent(CallToolResult result)
    {
        Assert.NotNull(result.Content);
        Assert.NotEmpty(result.Content);
        var textBlock = Assert.IsType<TextContentBlock>(result.Content[0]);
        return textBlock.Text;
    }

    [Fact]
    public async Task ToolsList_ContainsAllExpectedToolsWithValidInputSchemas()
    {
        var dummyPath = GetDummyProjectPath();
        var (bridge, client, serverTask, cs, ss) = await CreateMcpPairAsync(dummyPath);
        try
        {
            var tools = await client.ListToolsAsync();
            Assert.Equal(LspToolTable.All.Count, tools.Count);
            Assert.Equal(40, tools.Count);

            var expectedNames = new[]
            {
                "textDocument_hover",
                "textDocument_definition",
                "textDocument_declaration",
                "textDocument_typeDefinition",
                "textDocument_implementation",
                "textDocument_references",
                "textDocument_documentSymbol",
                "textDocument_documentHighlight",
                "textDocument_signatureHelp",
                "textDocument_completion",
                "completionItem_resolve",
                "textDocument_diagnostic",
                "workspace_diagnostic",
                "workspace_symbol",
                "workspaceSymbol_resolve",
                "textDocument_prepareCallHierarchy",
                "callHierarchy_incomingCalls",
                "callHierarchy_outgoingCalls",
                "textDocument_prepareTypeHierarchy",
                "typeHierarchy_supertypes",
                "typeHierarchy_subtypes",
                "textDocument_codeLens",
                "textDocument_documentLink",
                "textDocument_inlayHint",
                "textDocument_moniker",
                "textDocument_prepareRename",
                "textDocument_rename",
                "textDocument_codeAction",
                "codeAction_resolve",
                "textDocument_formatting",
                "textDocument_rangeFormatting",
                "workspace_willRenameFiles",
                "workspace_willCreateFiles",
                "workspace_willDeleteFiles",
                "workspace_executeCommand",
                "workspace_didChangeConfiguration",
                "lsp_reset",
                "lsp_diagnostics_cached",
            };

            foreach (var name in expectedNames)
            {
                Assert.Contains(tools, t => t.Name == name);
            }

            foreach (var tool in tools)
            {
                var schema = tool.JsonSchema;
                Assert.Equal(JsonValueKind.Object, schema.ValueKind);
                Assert.True(schema.TryGetProperty("type", out var typeProp));
                Assert.Equal("object", typeProp.GetString());
                Assert.True(schema.TryGetProperty("properties", out var propsProp));
                Assert.Equal(JsonValueKind.Object, propsProp.ValueKind);
            }

            var refsTool = tools.Single(t => t.Name == "textDocument_references");
            Assert.True(refsTool.JsonSchema.GetProperty("properties").TryGetProperty("includePatterns", out _));
            Assert.True(refsTool.JsonSchema.GetProperty("properties").TryGetProperty("excludePatterns", out _));

            var execTool = tools.Single(t => t.Name == "workspace_executeCommand");
            var commandEnum = execTool.JsonSchema
                .GetProperty("properties")
                .GetProperty("command")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(e => e.GetString())
                .ToArray();
            Assert.Equal(ExecuteCommandHandler.Commands, commandEnum);
        }
        finally
        {
            await DisposeMcpPairAsync(bridge, client, serverTask, cs, ss);
        }
    }

    [Fact]
    public async Task DefinitionAndReferences_MatchDirectLspResults()
    {
        var dummyPath = GetDummyProjectPath();
        var playerControllerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
        var playerControllerUri = new Uri(playerControllerPath).AbsoluteUri;
        var gameConfigPath = Path.Combine(dummyPath, "Assets", "Scripts", "Data", "GameConfig.cs");
        var gameConfigUri = new Uri(gameConfigPath).AbsoluteUri;

        // 1. Direct LSP baseline
        var (lspServer, lspClient) = CreateDirectLspPair();
        JToken? directDefinition;
        JToken? directReferences;
        JToken? directAssetUsages;
        try
        {
            await lspClient.InvokeWithParameterObjectAsync<Microsoft.VisualStudio.LanguageServer.Protocol.InitializeResult>(
                Methods.InitializeName,
                new InitializeParams
                {
                    RootUri = new Uri(dummyPath),
                    Capabilities = new Microsoft.VisualStudio.LanguageServer.Protocol.ClientCapabilities(),
                });
            await lspClient.NotifyAsync(Methods.InitializedName);

            directDefinition = await lspClient.InvokeWithParameterObjectAsync<JToken?>(
                Methods.TextDocumentDefinitionName,
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(playerControllerUri) },
                    Position = new Position(7, 35),
                });

            directReferences = await lspClient.InvokeWithParameterObjectAsync<JToken?>(
                Methods.TextDocumentReferencesName,
                new ReferenceParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = new Uri(gameConfigUri) },
                    Position = new Position(10, 22),
                    Context = new ReferenceContext { IncludeDeclaration = true },
                });

            directAssetUsages = await lspClient.InvokeWithParameterObjectAsync<JToken?>(
                Methods.WorkspaceExecuteCommandName,
                new ExecuteCommandParams
                {
                    Command = ExecuteCommandHandler.FindSymbolUsagesInAssetsCommand,
                    Arguments = new object[]
                    {
                        new JObject { ["symbolName"] = "PlayerController" },
                    },
                });

            await lspClient.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
            await lspClient.NotifyAsync(Methods.ExitName);
            await lspServer.WaitForExitAsync();
        }
        finally
        {
            lspClient.Dispose();
            lspServer.Dispose();
        }

        // 2. MCP bridge
        var (bridge, mcpClient, serverTask, cs, ss) = await CreateMcpPairAsync(dummyPath);
        try
        {
            var defResult = await mcpClient.CallToolAsync(
                "textDocument_definition",
                new Dictionary<string, object?>
                {
                    ["textDocument"] = new Dictionary<string, object?> { ["uri"] = playerControllerUri },
                    ["position"] = new Dictionary<string, object?> { ["line"] = 7, ["character"] = 35 },
                });

            Assert.False(defResult.IsError ?? false);
            var mcpDefJson = JToken.Parse(GetTextContent(defResult));
            Assert.True(JToken.DeepEquals(directDefinition, mcpDefJson));

            var refsResult = await mcpClient.CallToolAsync(
                "textDocument_references",
                new Dictionary<string, object?>
                {
                    ["textDocument"] = new Dictionary<string, object?> { ["uri"] = gameConfigUri },
                    ["position"] = new Dictionary<string, object?> { ["line"] = 10, ["character"] = 22 },
                    ["context"] = new Dictionary<string, object?> { ["includeDeclaration"] = true },
                });

            Assert.False(refsResult.IsError ?? false);
            var mcpRefsJson = JToken.Parse(GetTextContent(refsResult));
            Assert.True(JToken.DeepEquals(directReferences, mcpRefsJson));

            // Filtered references using includePatterns
            var filteredRefsResult = await mcpClient.CallToolAsync(
                "textDocument_references",
                new Dictionary<string, object?>
                {
                    ["textDocument"] = new Dictionary<string, object?> { ["uri"] = gameConfigUri },
                    ["position"] = new Dictionary<string, object?> { ["line"] = 10, ["character"] = 22 },
                    ["context"] = new Dictionary<string, object?> { ["includeDeclaration"] = true },
                    ["includePatterns"] = new[] { "PlayerController.cs" },
                });

            Assert.False(filteredRefsResult.IsError ?? false);
            var filteredRefs = Assert.IsType<JArray>(JToken.Parse(GetTextContent(filteredRefsResult)));
            Assert.NotEmpty(filteredRefs);
            Assert.All(filteredRefs, item =>
                Assert.Contains("PlayerController.cs", item["uri"]!.Value<string>()!, StringComparison.OrdinalIgnoreCase));

            // Asset usages command matches direct LSP
            var assetUsagesResult = await mcpClient.CallToolAsync(
                "workspace_executeCommand",
                new Dictionary<string, object?>
                {
                    ["command"] = ExecuteCommandHandler.FindSymbolUsagesInAssetsCommand,
                    ["arguments"] = new object[]
                    {
                        new Dictionary<string, object?> { ["symbolName"] = "PlayerController" },
                    },
                });

            Assert.False(assetUsagesResult.IsError ?? false);
            Assert.NotNull(assetUsagesResult.StructuredContent);
            var mcpAssetUsagesJson = JToken.Parse(GetTextContent(assetUsagesResult));
            Assert.True(JToken.DeepEquals(directAssetUsages, mcpAssetUsagesJson));
        }
        finally
        {
            await DisposeMcpPairAsync(bridge, mcpClient, serverTask, cs, ss);
        }
    }

    [Fact]
    public async Task ExecuteCommand_ShowWorkspaceInfo_ReturnsStructuredObject()
    {
        var dummyPath = GetDummyProjectPath();
        var (bridge, client, serverTask, cs, ss) = await CreateMcpPairAsync(dummyPath);
        try
        {
            var result = await client.CallToolAsync(
                "workspace_executeCommand",
                new Dictionary<string, object?>
                {
                    ["command"] = ExecuteCommandHandler.ShowWorkspaceInfoCommand,
                });

            Assert.False(result.IsError ?? false);
            Assert.NotNull(result.StructuredContent);
            Assert.Equal(JsonValueKind.Object, result.StructuredContent.Value.ValueKind);
            Assert.True(result.StructuredContent.Value.TryGetProperty("summary", out var summaryProp));
            Assert.False(string.IsNullOrWhiteSpace(summaryProp.GetString()));
            Assert.True(result.StructuredContent.Value.TryGetProperty("roots", out var rootsProp));
            Assert.Equal(JsonValueKind.Array, rootsProp.ValueKind);
            Assert.NotEmpty(rootsProp.EnumerateArray());

            var textObj = JObject.Parse(GetTextContent(result));
            Assert.Equal(summaryProp.GetString(), textObj["summary"]?.Value<string>());
        }
        finally
        {
            await DisposeMcpPairAsync(bridge, client, serverTask, cs, ss);
        }
    }

    [Fact]
    public async Task PathAndUriNormalization_AcceptsUriAbsoluteAndRelativePaths()
    {
        var dummyPath = GetDummyProjectPath();
        var relPath = Path.Combine("Assets", "Scripts", "PlayerController.cs");
        var absPath = Path.GetFullPath(Path.Combine(dummyPath, relPath));
        var fileUri = new Uri(absPath).AbsoluteUri;

        var (bridge, client, serverTask, cs, ss) = await CreateMcpPairAsync(dummyPath);
        try
        {
            async Task<JToken> CallDefAsync(string uriOrPath)
            {
                var res = await client.CallToolAsync(
                    "textDocument_definition",
                    new Dictionary<string, object?>
                    {
                        ["textDocument"] = new Dictionary<string, object?> { ["uri"] = uriOrPath },
                        ["position"] = new Dictionary<string, object?> { ["line"] = 7, ["character"] = 35 },
                    });
                Assert.False(res.IsError ?? false);
                return JToken.Parse(GetTextContent(res));
            }

            var fromUri = await CallDefAsync(fileUri);
            var fromAbs = await CallDefAsync(absPath);
            var fromRel = await CallDefAsync(relPath.Replace('\\', '/'));

            Assert.True(JToken.DeepEquals(fromUri, fromAbs));
            Assert.True(JToken.DeepEquals(fromUri, fromRel));
            Assert.Equal(1, bridge.SyncTracker.OpenDocumentCount);
        }
        finally
        {
            await DisposeMcpPairAsync(bridge, client, serverTask, cs, ss);
        }
    }

    [Fact]
    public async Task DocumentSync_ResyncsWhenFileModifiedOnDisk()
    {
        var dummyPath = GetDummyProjectPath();
        var tempRoot = Path.Combine(AppContext.BaseDirectory, "TempMcpFixture_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(dummyPath, "Assets"), Path.Combine(tempRoot, "Assets"));
        CopyDirectory(Path.Combine(dummyPath, "ProjectSettings"), Path.Combine(tempRoot, "ProjectSettings"));
        if (Directory.Exists(Path.Combine(dummyPath, "Packages")))
        {
            CopyDirectory(Path.Combine(dummyPath, "Packages"), Path.Combine(tempRoot, "Packages"));
        }

        try
        {
            var gameConfigPath = Path.Combine(tempRoot, "Assets", "Scripts", "Data", "GameConfig.cs");
            var (bridge, client, serverTask, cs, ss) = await CreateMcpPairAsync(tempRoot);
            try
            {
                var before = await client.CallToolAsync(
                    "textDocument_documentSymbol",
                    new Dictionary<string, object?>
                    {
                        ["textDocument"] = new Dictionary<string, object?> { ["uri"] = "Assets/Scripts/Data/GameConfig.cs" },
                    });
                Assert.False(before.IsError ?? false);
                var beforeText = GetTextContent(before);
                Assert.DoesNotContain("AddedAfterSync", beforeText);

                // Modify the file on disk and ensure mtime + size change
                var original = await File.ReadAllTextAsync(gameConfigPath);
                var idx = original.LastIndexOf("    }", StringComparison.Ordinal);
                Assert.True(idx > 0);
                var updated = original.Insert(idx, "        public int AddedAfterSync() => 42;\n");
                await File.WriteAllTextAsync(gameConfigPath, updated);
                File.SetLastWriteTimeUtc(gameConfigPath, DateTime.UtcNow.AddSeconds(2));

                var after = await client.CallToolAsync(
                    "textDocument_documentSymbol",
                    new Dictionary<string, object?>
                    {
                        ["textDocument"] = new Dictionary<string, object?> { ["uri"] = "Assets/Scripts/Data/GameConfig.cs" },
                    });
                Assert.False(after.IsError ?? false);
                var afterText = GetTextContent(after);
                Assert.Contains("AddedAfterSync", afterText);
            }
            finally
            {
                await DisposeMcpPairAsync(bridge, client, serverTask, cs, ss);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResetAndCachedDiagnostics_WorkAsExpected()
    {
        var dummyPath = GetDummyProjectPath();
        var tempRoot = Path.Combine(AppContext.BaseDirectory, "TempMcpReset_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(dummyPath, "Assets"), Path.Combine(tempRoot, "Assets"));
        CopyDirectory(Path.Combine(dummyPath, "ProjectSettings"), Path.Combine(tempRoot, "ProjectSettings"));
        if (Directory.Exists(Path.Combine(dummyPath, "Packages")))
        {
            CopyDirectory(Path.Combine(dummyPath, "Packages"), Path.Combine(tempRoot, "Packages"));
        }

        try
        {
            var brokenFile = Path.Combine(tempRoot, "Assets", "Scripts", "Data", "GameConfig.cs");
            var original = await File.ReadAllTextAsync(brokenFile);
            await File.WriteAllTextAsync(brokenFile, original + "\npublic class Broken { invalid_token_here }\n");

            var (bridge, client, serverTask, cs, ss) = await CreateMcpPairAsync(tempRoot);
            try
            {
                // Trigger didOpen via hover so publishDiagnostics is sent
                var hoverRes = await client.CallToolAsync(
                    "textDocument_hover",
                    new Dictionary<string, object?>
                    {
                        ["textDocument"] = new Dictionary<string, object?> { ["uri"] = brokenFile },
                        ["position"] = new Dictionary<string, object?> { ["line"] = 5, ["character"] = 26 },
                    });
                Assert.False(hoverRes.IsError ?? false);
                Assert.Equal(1, bridge.SyncTracker.OpenDocumentCount);

                // Poll briefly for async publishDiagnostics notification
                JObject? cached = null;
                for (int i = 0; i < 40; i++)
                {
                    var diagRes = await client.CallToolAsync(
                        "lsp_diagnostics_cached",
                        new Dictionary<string, object?> { ["uri"] = brokenFile });
                    cached = JObject.Parse(GetTextContent(diagRes));
                    if (cached["diagnostics"] is JArray arr && arr.Count > 0)
                    {
                        break;
                    }

                    await Task.Delay(50);
                }

                Assert.NotNull(cached);
                Assert.NotEmpty((JArray)cached!["diagnostics"]!);

                // Reset closes all tracked docs and reloads workspace
                var resetRes = await client.CallToolAsync("lsp_reset");
                Assert.False(resetRes.IsError ?? false);
                var resetObj = JObject.Parse(GetTextContent(resetRes));
                Assert.Equal(1, resetObj["closedDocuments"]?.Value<int>());
                Assert.True(resetObj["reloaded"]?.Value<bool>());
                Assert.Equal(0, bridge.SyncTracker.OpenDocumentCount);
            }
            finally
            {
                await DisposeMcpPairAsync(bridge, client, serverTask, cs, ss);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LspResponseError_MapsToMcpIsErrorWithCodeAndMessage()
    {
        var dummyPath = GetDummyProjectPath();
        var (bridge, client, serverTask, cs, ss) = await CreateMcpPairAsync(dummyPath);
        try
        {
            // Invoke a tool whose underlying LSP method does not exist via bridge's ExecuteToolAsync
            var missingDef = new LspToolDefinition(
                Name: "nonexistent_method",
                LspMethod: "nonexistent/method",
                ParamsType: null,
                ReadOnly: true,
                Description: "Test error mapping");
            var result = await bridge.ExecuteToolAsync(missingDef, null, CancellationToken.None);

            Assert.True(result.IsError);
            var errObj = JObject.Parse(GetTextContent(result));
            Assert.NotNull(errObj["code"]);
            Assert.False(string.IsNullOrWhiteSpace(errObj["message"]?.Value<string>()));
        }
        finally
        {
            await DisposeMcpPairAsync(bridge, client, serverTask, cs, ss);
        }
    }

    [Fact]
    public void WorkspaceResolver_FindsNearestUnityRootOrFallsBack()
    {
        var dummyPath = GetDummyProjectPath();
        var nestedDir = Path.Combine(dummyPath, "Assets", "Scripts", "Data");

        Assert.Equal(Path.GetFullPath(dummyPath), WorkspaceResolver.Resolve(null, nestedDir));
        Assert.Equal(Path.GetFullPath(dummyPath), WorkspaceResolver.Resolve(dummyPath, AppContext.BaseDirectory));

        var tempNoUnity = Path.Combine(AppContext.BaseDirectory, "NoUnityHere_" + Guid.NewGuid().ToString("N"));
        var nestedNoUnity = Path.Combine(tempNoUnity, "sub", "leaf");
        Directory.CreateDirectory(nestedNoUnity);
        try
        {
            Assert.Equal(
                Path.GetFullPath(nestedNoUnity),
                WorkspaceResolver.Resolve(null, nestedNoUnity, boundaryDirectory: tempNoUnity));
        }
        finally
        {
            Directory.Delete(tempNoUnity, recursive: true);
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }
}
