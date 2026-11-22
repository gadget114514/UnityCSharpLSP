using System.Collections.Concurrent;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol;

namespace UnityCSharpLSP.Mcp;

/// <summary>
/// Connects to an in-process <see cref="LspJsonRpcSession"/> over an in-memory duplex stream
/// and handles the LSP client lifecycle, server-to-client notifications/requests, and RPC invocation.
/// </summary>
public sealed class LspLoopbackClient : IAsyncDisposable
{
    private readonly Stream _clientStream;
    private readonly Stream _serverStream;
    private readonly LspJsonRpcSession _session;
    private readonly JsonRpc _rpc;
    private readonly TextWriter _logWriter;
    private readonly ConcurrentDictionary<string, JObject> _diagnosticsByPath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public LspLoopbackClient(TextWriter? logWriter = null)
    {
        _logWriter = logWriter ?? Console.Error;
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _diagnosticsByPath = new ConcurrentDictionary<string, JObject>(pathComparer);

        (_clientStream, _serverStream) = FullDuplexStream.CreatePair();
        _session = new LspJsonRpcSession(_serverStream, _serverStream);
        _session.Start();

        var clientHandler = new HeaderDelimitedMessageHandler(
            _clientStream,
            _clientStream,
            new JsonMessageFormatter());
        _rpc = new JsonRpc(clientHandler);
        _rpc.AddLocalRpcTarget(new ClientCallbackTarget(this));
        _rpc.StartListening();
    }

    public LspJsonRpcSession Session => _session;

    /// <summary>
    /// Sends <c>initialize</c>, <c>initialized</c>, and optional initial <c>workspace/didChangeConfiguration</c>.
    /// </summary>
    public async Task<JToken?> InitializeAsync(
        IReadOnlyList<string> workspaceRoots,
        JToken? initialSettings = null,
        CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return null;
            }

            var folders = new JArray();
            string? firstUri = null;
            foreach (var root in workspaceRoots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var fullRoot = Path.GetFullPath(root);
                var uri = new Uri(fullRoot).AbsoluteUri;
                firstUri ??= uri;
                folders.Add(new JObject
                {
                    ["uri"] = uri,
                    ["name"] = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                });
            }

            var initParams = new JObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = firstUri,
                ["workspaceFolders"] = folders,
                ["capabilities"] = BuildFullClientCapabilities(),
            };

            var result = await _rpc.InvokeWithParameterObjectAsync<JToken?>(
                Methods.InitializeName,
                initParams,
                ct).ConfigureAwait(false);

            await _rpc.NotifyAsync(Methods.InitializedName).ConfigureAwait(false);

            if (initialSettings is not null)
            {
                await _rpc.NotifyWithParameterObjectAsync(
                    Methods.WorkspaceDidChangeConfigurationName,
                    new JObject { ["settings"] = initialSettings }).ConfigureAwait(false);
            }

            _initialized = true;
            return result;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Invokes an LSP request method and returns the raw JSON result.
    /// </summary>
    public async Task<JToken?> InvokeAsync(string method, JObject @params, CancellationToken ct = default)
    {
        return await _rpc.InvokeWithParameterObjectAsync<JToken?>(method, @params, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an LSP notification method.
    /// </summary>
    public Task NotifyAsync(string method, JObject @params)
    {
        return _rpc.NotifyWithParameterObjectAsync(method, @params);
    }

    /// <summary>
    /// Returns the cached <c>textDocument/publishDiagnostics</c> payload for a single file path/URI,
    /// or all cached entries when <paramref name="fullPath"/> is null.
    /// </summary>
    public JObject GetCachedDiagnostics(string? fullPath = null)
    {
        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            var normalized = Path.GetFullPath(fullPath);
            if (_diagnosticsByPath.TryGetValue(normalized, out var entry))
            {
                return (JObject)entry.DeepClone();
            }

            return new JObject
            {
                ["uri"] = new Uri(normalized).AbsoluteUri,
                ["diagnostics"] = new JArray(),
                ["cached"] = false,
            };
        }

        var items = new JArray();
        foreach (var pair in _diagnosticsByPath.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            items.Add((JObject)pair.Value.DeepClone());
        }

        return new JObject
        {
            ["documents"] = items,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_initialized)
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _rpc.InvokeWithParameterObjectAsync<object?>(
                    Methods.ShutdownName,
                    new JObject(),
                    shutdownCts.Token).ConfigureAwait(false);
                await _rpc.NotifyAsync(Methods.ExitName).ConfigureAwait(false);
                await _session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log($"[mcp] Error during LSP shutdown: {ex.Message}");
        }
        finally
        {
            _rpc.Dispose();
            _session.Dispose();
            await _clientStream.DisposeAsync().ConfigureAwait(false);
            await _serverStream.DisposeAsync().ConfigureAwait(false);
            _initLock.Dispose();
        }
    }

    private void OnPublishDiagnostics(JObject payload)
    {
        var uriString = payload["uri"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(uriString))
        {
            return;
        }

        var key = Uri.TryCreate(uriString, UriKind.Absolute, out var uri) && uri.IsFile
            ? Path.GetFullPath(uri.LocalPath)
            : uriString;

        _diagnosticsByPath[key] = (JObject)payload.DeepClone();
    }

    private void Log(string message)
    {
        try
        {
            _logWriter.WriteLine(message);
        }
        catch
        {
            // Ignore logging failures on shutdown.
        }
    }

    private static JObject BuildFullClientCapabilities()
    {
        var markdownPlain = new JArray("markdown", "plaintext");
        return new JObject
        {
            ["general"] = new JObject
            {
                ["positionEncodings"] = new JArray("utf-16"),
            },
            ["workspace"] = new JObject
            {
                ["workspaceFolders"] = true,
                ["configuration"] = true,
                ["didChangeConfiguration"] = new JObject { ["dynamicRegistration"] = false },
                ["didChangeWatchedFiles"] = new JObject { ["dynamicRegistration"] = false },
                ["executeCommand"] = new JObject { ["dynamicRegistration"] = false },
                ["symbol"] = new JObject { ["dynamicRegistration"] = false, ["resolveSupport"] = new JObject { ["properties"] = new JArray("location.range") } },
                ["fileOperations"] = new JObject
                {
                    ["didCreate"] = true,
                    ["willCreate"] = true,
                    ["didRename"] = true,
                    ["willRename"] = true,
                    ["didDelete"] = true,
                    ["willDelete"] = true,
                },
                ["diagnostics"] = new JObject { ["refreshSupport"] = false },
            },
            ["textDocument"] = new JObject
            {
                ["synchronization"] = new JObject
                {
                    ["dynamicRegistration"] = false,
                    ["willSave"] = true,
                    ["willSaveWaitUntil"] = true,
                    ["didSave"] = true,
                },
                ["hover"] = new JObject { ["contentFormat"] = markdownPlain },
                ["completion"] = new JObject
                {
                    ["completionItem"] = new JObject
                    {
                        ["snippetSupport"] = true,
                        ["documentationFormat"] = markdownPlain.DeepClone(),
                        ["resolveSupport"] = new JObject { ["properties"] = new JArray("documentation", "detail", "additionalTextEdits") },
                    },
                },
                ["signatureHelp"] = new JObject
                {
                    ["contextSupport"] = true,
                    ["signatureInformation"] = new JObject
                    {
                        ["documentationFormat"] = markdownPlain.DeepClone(),
                    },
                },
                ["declaration"] = new JObject { ["linkSupport"] = false },
                ["definition"] = new JObject { ["linkSupport"] = false },
                ["typeDefinition"] = new JObject { ["linkSupport"] = false },
                ["implementation"] = new JObject { ["linkSupport"] = false },
                ["references"] = new JObject(),
                ["documentHighlight"] = new JObject(),
                ["documentSymbol"] = new JObject { ["hierarchicalDocumentSymbolSupport"] = true },
                ["codeAction"] = new JObject
                {
                    ["codeActionLiteralSupport"] = new JObject
                    {
                        ["codeActionKind"] = new JObject
                        {
                            ["valueSet"] = new JArray("quickfix", "refactor", "source"),
                        },
                    },
                    ["resolveSupport"] = new JObject { ["properties"] = new JArray("edit") },
                },
                ["codeLens"] = new JObject(),
                ["documentLink"] = new JObject(),
                ["formatting"] = new JObject(),
                ["rangeFormatting"] = new JObject(),
                ["rename"] = new JObject { ["prepareSupport"] = true },
                ["callHierarchy"] = new JObject(),
                ["typeHierarchy"] = new JObject(),
                ["inlayHint"] = new JObject(),
                ["moniker"] = new JObject(),
                ["diagnostic"] = new JObject { ["relatedDocumentSupport"] = false },
                ["publishDiagnostics"] = new JObject(),
            },
        };
    }

    private sealed class ClientCallbackTarget
    {
        private readonly LspLoopbackClient _owner;

        public ClientCallbackTarget(LspLoopbackClient owner)
        {
            _owner = owner;
        }

        [JsonRpcMethod(Methods.TextDocumentPublishDiagnosticsName, UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(JObject @params)
        {
            _owner.OnPublishDiagnostics(@params);
        }

        [JsonRpcMethod(Methods.WindowShowMessageName, UseSingleObjectParameterDeserialization = true)]
        public void ShowMessage(JObject @params)
        {
            var type = @params["type"]?.Value<int?>();
            var message = @params["message"]?.Value<string>();
            _owner.Log($"[lsp/showMessage:{type}] {message}");
        }

        [JsonRpcMethod(Methods.WindowLogMessageName, UseSingleObjectParameterDeserialization = true)]
        public void LogMessage(JObject @params)
        {
            var type = @params["type"]?.Value<int?>();
            var message = @params["message"]?.Value<string>();
            _owner.Log($"[lsp/logMessage:{type}] {message}");
        }

        [JsonRpcMethod(Methods.ProgressNotificationName, UseSingleObjectParameterDeserialization = true)]
        public void Progress(JObject @params)
        {
            _owner.Log($"[lsp/progress] {@params.ToString(Formatting.None)}");
        }

        [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
        public JArray Configuration(JObject @params)
        {
            var items = @params["items"] as JArray;
            var result = new JArray();
            if (items is not null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    result.Add(new JObject());
                }
            }

            return result;
        }

        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public object? CreateWorkDoneProgress(JObject @params) => null;

        [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
        public object? RegisterCapability(JObject @params) => null;

        [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
        public object? UnregisterCapability(JObject @params) => null;

        [JsonRpcMethod("workspace/applyEdit", UseSingleObjectParameterDeserialization = true)]
        public JObject ApplyEdit(JObject @params) => new() { ["applied"] = false };
    }
}
