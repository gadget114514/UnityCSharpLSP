using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using Xunit;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class UnityGuidHandlerTests
{
    private const string PlayerControllerGuid = "c5c9d31933d241c4b9047a5783f7f380";

    [Fact]
    public async Task GuidDefinition_ByGuid_ReturnsMetaGuidPositionAndAsset()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var result = await client.InvokeWithParameterObjectAsync<JObject?>(
                UnityGuidMethods.GuidDefinitionName,
                new { guid = PlayerControllerGuid.ToUpperInvariant() });

            Assert.NotNull(result);
            Assert.Equal(PlayerControllerGuid, result!["guid"]!.Value<string>());
            var scriptPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
            Assert.Equal(new Uri(scriptPath).AbsoluteUri, result["assetUri"]!.Value<string>());

            var meta = result["meta"]!;
            Assert.Equal(new Uri(scriptPath + ".meta").AbsoluteUri, meta["uri"]!.Value<string>());
            Assert.Equal(1, meta["range"]!["start"]!["line"]!.Value<int>());
            Assert.Equal(6, meta["range"]!["start"]!["character"]!.Value<int>());
            Assert.Equal(38, meta["range"]!["end"]!["character"]!.Value<int>());
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task GuidDefinition_UnknownOrInvalidGuid_ReturnsNull()
    {
        var (server, client, _) = await CreateInitializedSessionAsync();
        try
        {
            Assert.Null(await client.InvokeWithParameterObjectAsync<JObject?>(
                UnityGuidMethods.GuidDefinitionName,
                new { guid = "ffffffffffffffffffffffffffffffff" }));
            Assert.Null(await client.InvokeWithParameterObjectAsync<JObject?>(
                UnityGuidMethods.GuidDefinitionName,
                new { guid = "not-a-guid" }));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task GuidReferences_ByWorkspaceRelativeAsset_FindsPrefabScriptReference()
    {
        var (server, client, dummyPath) = await CreateInitializedSessionAsync();
        try
        {
            var result = await client.InvokeWithParameterObjectAsync<JObject?>(
                UnityGuidMethods.GuidReferencesName,
                new { asset = "Assets/Scripts/PlayerController.cs" });

            Assert.NotNull(result);
            Assert.Equal(PlayerControllerGuid, result!["guid"]!.Value<string>());
            Assert.False(result["truncated"]!.Value<bool>());

            var prefabUri = new Uri(Path.Combine(dummyPath, "Assets", "Prefabs", "Player.prefab")).AbsoluteUri;
            var refs = result["references"]!.Children<JObject>().ToList();
            var prefabRef = Assert.Single(refs, r => r["uri"]!.Value<string>() == prefabUri);
            Assert.Equal(45, prefabRef["range"]!["start"]!["line"]!.Value<int>());
            Assert.Contains("m_Script:", prefabRef["lineText"]!.Value<string>());
            Assert.Equal(114, prefabRef["classId"]!.Value<int>());
            Assert.Equal(100014, prefabRef["objectFileId"]!.Value<long>());
            Assert.Equal(11500000, prefabRef["fileId"]!.Value<long>());

            // The script's own .meta is the definition, not a reference.
            Assert.DoesNotContain(refs, r => r["uri"]!.Value<string>()!.EndsWith("PlayerController.cs.meta", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    [Fact]
    public async Task GuidReferences_WithPatterns_FiltersScannedFiles()
    {
        var (server, client, _) = await CreateInitializedSessionAsync();
        try
        {
            var excluded = await client.InvokeWithParameterObjectAsync<JObject?>(
                UnityGuidMethods.GuidReferencesName,
                new { guid = PlayerControllerGuid, excludePatterns = new[] { "*.prefab" } });
            Assert.NotNull(excluded);
            Assert.DoesNotContain(
                excluded!["references"]!.Children<JObject>(),
                r => r["uri"]!.Value<string>()!.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));

            var included = await client.InvokeWithParameterObjectAsync<JObject?>(
                UnityGuidMethods.GuidReferencesName,
                new { guid = PlayerControllerGuid, includePatterns = new[] { "Assets/Prefabs/**" } });
            Assert.NotNull(included);
            var refs = included!["references"]!.Children<JObject>().ToList();
            Assert.NotEmpty(refs);
            Assert.All(refs, r => Assert.Contains("/Assets/Prefabs/", r["uri"]!.Value<string>()));
        }
        finally
        {
            await ShutdownAsync(server, client);
        }
    }

    private static async Task<(LspJsonRpcSession Server, JsonRpc Client, string DummyPath)> CreateInitializedSessionAsync()
    {
        var rootPath = GetDummyProjectPath();
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
            new
            {
                rootUri = new Uri(rootPath),
                capabilities = new JObject(),
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
}
