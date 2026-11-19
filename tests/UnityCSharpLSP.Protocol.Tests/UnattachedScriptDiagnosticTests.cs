using System.Collections.Concurrent;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using Xunit;
using LspDiagnostic = Microsoft.VisualStudio.LanguageServer.Protocol.Diagnostic;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class UnattachedScriptDiagnosticTests
{
    private const string AttachedGuid = "11111111111111111111111111111111";
    private const string UnattachedGuid = "22222222222222222222222222222222";
    private const string AbstractGuid = "33333333333333333333333333333333";
    private const string EditorOnlyGuid = "44444444444444444444444444444444";

    [Fact]
    public async Task Diagnostics_UnattachedScripts_AreOptInHintsAndSkipNonAttachableTypes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "UnityCSharpLSP.UnattachedScriptDiagTests", Guid.NewGuid().ToString("N"));
        try
        {
            var scriptsDir = Path.Combine(tempRoot, "Assets", "Scripts");
            var prefabsDir = Path.Combine(tempRoot, "Assets", "Prefabs");
            var editorDir = Path.Combine(scriptsDir, "Editor");
            Directory.CreateDirectory(scriptsDir);
            Directory.CreateDirectory(prefabsDir);
            Directory.CreateDirectory(editorDir);

            var attachedPath = WriteScript(scriptsDir, "AttachedBehaviour.cs", "AttachedBehaviour", "sealed", AttachedGuid);
            var unattachedPath = WriteScript(scriptsDir, "UnattachedBehaviour.cs", "UnattachedBehaviour", "sealed", UnattachedGuid);
            var abstractPath = WriteScript(scriptsDir, "AbstractBehaviour.cs", "AbstractBehaviour", "abstract", AbstractGuid);
            var editorOnlyPath = WriteScript(editorDir, "EditorOnlyBehaviour.cs", "EditorOnlyBehaviour", "sealed", EditorOnlyGuid);

            var prefabPath = Path.Combine(prefabsDir, "Attached.prefab");
            await File.WriteAllTextAsync(prefabPath, string.Join("\n",
            [
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1 &100000",
                "GameObject:",
                "  m_Name: Attached",
                "--- !u!114 &100014",
                "MonoBehaviour:",
                "  m_GameObject: {fileID: 100000}",
                $"  m_Script: {{fileID: 11500000, guid: {AttachedGuid}, type: 3}}",
            ]));
            await File.WriteAllTextAsync(prefabPath + ".meta", "fileFormatVersion: 2\nguid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");

            var collector = new DiagnosticsCollector();
            var (server, client) = await CreateInitializedSessionAsync(tempRoot, collector);
            try
            {
                // Default (opt-out): no UNITY0004 in pull or push diagnostics.
                var before = await PullAsync(client, unattachedPath);
                Assert.DoesNotContain(before, d => IsUnattachedDiagnostic(d));

                await client.NotifyWithParameterObjectAsync(
                    Methods.TextDocumentDidOpenName,
                    new DidOpenTextDocumentParams
                    {
                        TextDocument = new TextDocumentItem
                        {
                            Uri = new Uri(unattachedPath),
                            LanguageId = "csharp",
                            Version = 1,
                            Text = File.ReadAllText(unattachedPath),
                        },
                    });
                var beforePush = await collector.WaitForAsync(new Uri(unattachedPath));
                Assert.DoesNotContain(beforePush.Diagnostics, IsUnattachedDiagnostic);

                // Opt in (camelCase): the unattached script gets a single Hint diagnostic.
                await client.NotifyWithParameterObjectAsync(
                    Methods.WorkspaceDidChangeConfigurationName,
                    new { settings = new { reportUnattachedScripts = true } });

                var attached = await PullAsync(client, attachedPath);
                Assert.DoesNotContain(attached, IsUnattachedDiagnostic);

                var unattached = await PullAsync(client, unattachedPath);
                var diag = Assert.Single(unattached, IsUnattachedDiagnostic);
                Assert.Equal("UnityCSharpLSP", diag.Source);
                Assert.Equal(DiagnosticSeverity.Hint, diag.Severity);
                Assert.Equal("UnattachedBehaviour is not attached to any prefab or scene.", diag.Message);

                var abstractType = await PullAsync(client, abstractPath);
                Assert.DoesNotContain(abstractType, IsUnattachedDiagnostic);

                var editorOnly = await PullAsync(client, editorOnlyPath);
                Assert.DoesNotContain(editorOnly, IsUnattachedDiagnostic);

                // Push diagnostics also carry the hint once opted in.
                await client.NotifyWithParameterObjectAsync(
                    Methods.TextDocumentDidSaveName,
                    new DidSaveTextDocumentParams { TextDocument = new TextDocumentIdentifier { Uri = new Uri(unattachedPath) } });
                var afterPush = await collector.WaitForAsync(new Uri(unattachedPath));
                Assert.Contains(afterPush.Diagnostics, IsUnattachedDiagnostic);

                // Opt out via snake_case: the hint disappears again.
                await client.NotifyWithParameterObjectAsync(
                    Methods.WorkspaceDidChangeConfigurationName,
                    new { settings = new { report_unattached_scripts = false } });
                var afterOptOut = await PullAsync(client, unattachedPath);
                Assert.DoesNotContain(afterOptOut, IsUnattachedDiagnostic);
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
                    // Ignore transient SQLite file handles during temp cleanup.
                }
            }
        }
    }

    [Fact]
    public async Task PullDiagnostics_Unity0004ResultId_IsReusedOnlyWhenDiagnosticsAreUnchanged()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "UnityCSharpLSP.UnattachedResultIdTests", Guid.NewGuid().ToString("N"));
        try
        {
            var scriptsDir = Path.Combine(tempRoot, "Assets", "Scripts");
            Directory.CreateDirectory(scriptsDir);
            var scriptPath = WriteScript(scriptsDir, "LooseBehaviour.cs", "LooseBehaviour", "sealed", UnattachedGuid);

            var collector = new DiagnosticsCollector();
            var (server, client) = await CreateInitializedSessionAsync(tempRoot, collector);
            try
            {
                await client.NotifyWithParameterObjectAsync(
                    Methods.WorkspaceDidChangeConfigurationName,
                    new { settings = new { reportUnattachedScripts = true } });

                var first = await PullReportAsync(client, scriptPath, previousResultId: null);
                Assert.Equal("full", first.Kind);
                Assert.False(string.IsNullOrEmpty(first.ResultId));
                Assert.Contains(first.Diagnostics!, IsUnattachedDiagnostic);

                var unchanged = await PullReportAsync(client, scriptPath, first.ResultId);
                Assert.Equal("unchanged", unchanged.Kind);
                Assert.Equal(first.ResultId, unchanged.ResultId);
                Assert.Null(unchanged.Diagnostics);

                await client.NotifyWithParameterObjectAsync(
                    Methods.WorkspaceDidChangeConfigurationName,
                    new { settings = new { reportUnattachedScripts = false } });

                var updated = await PullReportAsync(client, scriptPath, first.ResultId);
                Assert.Equal("full", updated.Kind);
                Assert.NotEqual(first.ResultId, updated.ResultId);
                Assert.DoesNotContain(updated.Diagnostics!, IsUnattachedDiagnostic);

                var stable = await PullReportAsync(client, scriptPath, updated.ResultId);
                Assert.Equal("unchanged", stable.Kind);
                Assert.Equal(updated.ResultId, stable.ResultId);
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
                    // Ignore transient SQLite file handles during temp cleanup.
                }
            }
        }
    }

    private static Task<DocumentDiagnosticReport> PullReportAsync(
        JsonRpc client,
        string path,
        string? previousResultId)
    {
        return client.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(
            DiagnosticMethods.Name,
            new DocumentDiagnosticParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = new Uri(path) },
                PreviousResultId = previousResultId,
            });
    }

    private static string WriteScript(string directory, string fileName, string className, string modifier, string guid)
    {
        var path = Path.Combine(directory, fileName);
        var source = $"using UnityEngine;\n\npublic {modifier} class {className} : MonoBehaviour\n{{\n}}\n";
        File.WriteAllText(path, source);
        File.WriteAllText(path + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
        return path;
    }

    private static bool IsUnattachedDiagnostic(LspDiagnostic diagnostic)
    {
        return string.Equals(diagnostic.Code?.Value as string, "UNITY0004", StringComparison.Ordinal);
    }

    private static async Task<LspDiagnostic[]> PullAsync(JsonRpc client, string path)
    {
        var report = await client.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(
            DiagnosticMethods.Name,
            new DocumentDiagnosticParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = new Uri(path) },
            });
        return report.Diagnostics ?? [];
    }

    private static async Task<(LspJsonRpcSession Server, JsonRpc Client)> CreateInitializedSessionAsync(
        string rootPath,
        object clientCallbackTarget)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var server = new LspJsonRpcSession(serverStream, serverStream);
        server.Start();

        var client = new JsonRpc(new HeaderDelimitedMessageHandler(
            clientStream,
            clientStream,
            new JsonMessageFormatter()));
        client.AddLocalRpcTarget(clientCallbackTarget);
        client.StartListening();

        var capabilities = new JObject
        {
            ["textDocument"] = new JObject(),
        };

        await client.InvokeWithParameterObjectAsync<InitializeResult>(
            Methods.InitializeName,
            new
            {
                rootUri = new Uri(rootPath),
                capabilities,
            });

        return (server, client);
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
                    if (string.Equals(item.Uri.LocalPath, uri.LocalPath, StringComparison.OrdinalIgnoreCase))
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
