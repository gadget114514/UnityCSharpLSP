using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Handlers;

namespace UnityCSharpLSP.Mcp;

/// <summary>
/// Hosts the Model Context Protocol (MCP) server bridging MCP tool calls to an in-process
/// <see cref="LspLoopbackClient"/> and <see cref="DocumentSyncTracker"/>.
/// </summary>
public sealed class McpBridgeServer : IAsyncDisposable
{
    private readonly LspLoopbackClient _lspClient;
    private readonly DocumentSyncTracker _syncTracker;
    private readonly TimeSpan _callTimeout;
    private readonly string _workspaceRoot;
    private readonly object _initGate = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _lspInitTask;
    private bool _disposed;

    public McpBridgeServer(
        string workspaceRoot,
        TimeSpan? callTimeout = null,
        TextWriter? logWriter = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(120);
        _lspClient = new LspLoopbackClient(logWriter);
        _syncTracker = new DocumentSyncTracker(_lspClient, _workspaceRoot);
    }

    public LspLoopbackClient LspClient => _lspClient;
    public DocumentSyncTracker SyncTracker => _syncTracker;

    public Task InitializeLspAsync(JToken? initialSettings = null, CancellationToken ct = default)
    {
        StartLspInitialization(initialSettings, ct);
        return _lspInitTask!;
    }

    /// <summary>
    /// Kicks off LSP <c>initialize</c> (which loads the Roslyn workspace) in the background so the
    /// MCP handshake and <c>tools/list</c> can be answered immediately. Tool calls await completion.
    /// </summary>
    public void StartLspInitialization(JToken? initialSettings = null, CancellationToken ct = default)
    {
        lock (_initGate)
        {
            if (_lspInitTask is not null)
            {
                return;
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            _lspInitTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await _lspClient.InitializeAsync(new[] { _workspaceRoot }, initialSettings, linked.Token)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        linked.Dispose();
                    }
                },
                CancellationToken.None);
        }
    }

    private async Task EnsureLspInitializedAsync(CancellationToken ct)
    {
        Task? init;
        lock (_initGate)
        {
            init = _lspInitTask;
        }

        if (init is null)
        {
            StartLspInitialization(null, CancellationToken.None);
            lock (_initGate)
            {
                init = _lspInitTask!;
            }
        }

        await init.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an <see cref="McpServer"/> over stdio and runs it until cancelled or EOF.
    /// </summary>
    public static async Task RunStdioAsync(
        string workspaceRoot,
        JToken? initialSettings = null,
        TimeSpan? callTimeout = null,
        CancellationToken ct = default)
    {
        await using var bridge = new McpBridgeServer(workspaceRoot, callTimeout);

        // Do not block the MCP handshake on workspace loading: clients expect initialize /
        // tools/list to answer immediately. Tool calls wait for LSP initialization instead.
        bridge.StartLspInitialization(initialSettings, ct);

        var options = bridge.CreateServerOptions();
        await using var transport = new StdioServerTransport(options, NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, options, NullLoggerFactory.Instance);
        await server.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates <see cref="McpServerOptions"/> populated dynamically from <see cref="LspToolTable"/>.
    /// </summary>
    public McpServerOptions CreateServerOptions()
    {
        var version = typeof(McpBridgeServer).Assembly.GetName().Version?.ToString() ?? "0.5.0";
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "unity-csharp-lsp",
                Version = version,
            },
            Capabilities = new ModelContextProtocol.Protocol.ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
            },
            ServerInstructions =
                "UnityCSharpLSP MCP bridge exposing LSP capabilities directly. " +
                "All positions are 0-based (UTF-16). Document URIs accept file:/// URIs, absolute paths, or workspace-relative paths.",
        };

        options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var def in LspToolTable.All)
        {
            options.ToolCollection.Add(new LspBridgeTool(def, this));
        }

        return options;
    }

    /// <summary>
    /// Creates an <see cref="McpServer"/> bound to a custom <see cref="ITransport"/> (primarily for testing).
    /// </summary>
    public McpServer CreateServer(ITransport transport, ILoggerFactory? loggerFactory = null)
    {
        return McpServer.Create(transport, CreateServerOptions(), loggerFactory ?? NullLoggerFactory.Instance);
    }

    internal async ValueTask<CallToolResult> ExecuteToolAsync(
        LspToolDefinition def,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken ct)
    {
        try
        {
            var paramsObj = ConvertArgumentsToJObject(arguments);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_callTimeout != Timeout.InfiniteTimeSpan)
            {
                timeoutCts.CancelAfter(_callTimeout);
            }

            var callCt = timeoutCts.Token;

            if (def.Kind != McpBridgeToolKind.CachedDiagnostics)
            {
                await EnsureLspInitializedAsync(callCt).ConfigureAwait(false);
            }

            switch (def.Kind)
            {
                case McpBridgeToolKind.LspRequest:
                {
                    await _syncTracker.EnsureSyncedForParamsAsync(paramsObj, callCt).ConfigureAwait(false);
                    var result = await _lspClient.InvokeAsync(def.LspMethod!, paramsObj, callCt).ConfigureAwait(false);
                    return CreateSuccessResult(result);
                }

                case McpBridgeToolKind.LspNotification:
                {
                    await _syncTracker.EnsureSyncedForParamsAsync(paramsObj, callCt).ConfigureAwait(false);
                    await _lspClient.NotifyAsync(def.LspMethod!, paramsObj).ConfigureAwait(false);
                    return CreateSuccessResult(null);
                }

                case McpBridgeToolKind.Reset:
                {
                    var closedCount = await _syncTracker.CloseAllAsync(callCt).ConfigureAwait(false);
                    var reloadResult = await _lspClient.InvokeAsync(
                        Methods.WorkspaceExecuteCommandName,
                        new JObject { ["command"] = ExecuteCommandHandler.ReloadWorkspaceCommand },
                        callCt).ConfigureAwait(false);
                    var payload = new JObject
                    {
                        ["closedDocuments"] = closedCount,
                        ["reloaded"] = true,
                        ["result"] = reloadResult ?? JValue.CreateNull(),
                    };
                    return CreateSuccessResult(payload);
                }

                case McpBridgeToolKind.CachedDiagnostics:
                {
                    var rawUri = paramsObj["uri"]?.Value<string>();
                    string? fullPath = null;
                    if (!string.IsNullOrWhiteSpace(rawUri))
                    {
                        fullPath = _syncTracker.NormalizePathAndUri(rawUri).FullPath;
                    }

                    var cached = _lspClient.GetCachedDiagnostics(fullPath);
                    return CreateSuccessResult(cached);
                }

                default:
                    return CreateErrorResult(-32601, $"Unsupported tool kind '{def.Kind}'.");
            }
        }
        catch (RemoteInvocationException ex)
        {
            return CreateErrorResult(ex.ErrorCode, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CreateErrorResult(
                -32001,
                $"LSP call '{def.LspMethod ?? def.Name}' timed out after {_callTimeout.TotalSeconds:0.#}s.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateErrorResult(-32603, ex.Message);
        }
    }

    private static JObject ConvertArgumentsToJObject(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return new JObject();
        }

        var json = System.Text.Json.JsonSerializer.Serialize(arguments);
        return JObject.Parse(json);
    }

    private static CallToolResult CreateSuccessResult(JToken? result)
    {
        var jsonText = result is null || result.Type == JTokenType.Null
            ? "null"
            : result.ToString(Formatting.None);

        JsonElement? structuredContent = null;
        if (result is JObject)
        {
            using var doc = JsonDocument.Parse(jsonText);
            structuredContent = doc.RootElement.Clone();
        }

        return new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = jsonText },
            },
            StructuredContent = structuredContent,
            IsError = false,
        };
    }

    private static CallToolResult CreateErrorResult(int code, string message)
    {
        var errorJson = JsonConvert.SerializeObject(new { code, message });
        return new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = errorJson },
            },
            IsError = true,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();

        Task? init;
        lock (_initGate)
        {
            init = _lspInitTask;
        }

        if (init is not null)
        {
            try
            {
                // Workspace loading is synchronous inside the LSP session and may not observe
                // cancellation; don't hang shutdown waiting for it.
                await init.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // Initialization failure/cancellation/timeout is irrelevant during shutdown.
            }
        }

        _syncTracker.Dispose();
        await _lspClient.DisposeAsync().ConfigureAwait(false);
        _disposeCts.Dispose();
    }

    private sealed class LspBridgeTool : McpServerTool
    {
        private readonly LspToolDefinition _definition;
        private readonly McpBridgeServer _bridge;
        private readonly Tool _protocolTool;

        public LspBridgeTool(LspToolDefinition definition, McpBridgeServer bridge)
        {
            _definition = definition;
            _bridge = bridge;
            _protocolTool = new Tool
            {
                Name = definition.Name,
                Description = definition.Description,
                InputSchema = definition.BuildInputSchema(),
                Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = definition.ReadOnly,
                    DestructiveHint = false,
                    IdempotentHint = definition.ReadOnly || definition.Kind == McpBridgeToolKind.Reset,
                },
            };
        }

        public override Tool ProtocolTool => _protocolTool;

        public override IReadOnlyList<object> Metadata => Array.Empty<object>();

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            return _bridge.ExecuteToolAsync(_definition, request.Params?.Arguments, cancellationToken);
        }
    }
}
