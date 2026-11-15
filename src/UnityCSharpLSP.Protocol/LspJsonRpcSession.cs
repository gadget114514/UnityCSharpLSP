using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Handlers;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol;

/// <summary>
/// Manages a JSON-RPC session over streams (e.g. stdio or named pipes) implementing the LSP protocol.
/// </summary>
public sealed class LspJsonRpcSession : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly JsonRpc _rpc;
    private readonly TaskCompletionSource<int> _exitTcs = new();

    private UnityWorkspaceManager? _workspaceManager;
    private DiagnosticsHandler? _diagnosticsHandler;
    private bool _isShutdown;

    public UnityWorkspaceManager? WorkspaceManager => _workspaceManager;
    public JsonRpc JsonRpc => _rpc;

    public LspJsonRpcSession(Stream input, Stream output)
    {
        _input = input;
        _output = output;

        var handler = new HeaderDelimitedMessageHandler(output, input, new JsonMessageFormatter());
        _rpc = new JsonRpc(handler);

        // Register lifecycle methods on this session
        _rpc.AddLocalRpcTarget(this);

        // Register text document sync handler
        var syncHandler = new TextDocumentSyncHandler(
            () => _workspaceManager,
            () => _diagnosticsHandler);
        _rpc.AddLocalRpcTarget(syncHandler);

        // Register language feature handlers
        var hoverHandler = new HoverHandler(() => _workspaceManager);
        _rpc.AddLocalRpcTarget(hoverHandler);

        var definitionHandler = new DefinitionHandler(() => _workspaceManager);
        _rpc.AddLocalRpcTarget(definitionHandler);

        var completionHandler = new CompletionHandler(() => _workspaceManager);
        _rpc.AddLocalRpcTarget(completionHandler);

        var referencesHandler = new ReferencesHandler(() => _workspaceManager);
        _rpc.AddLocalRpcTarget(referencesHandler);
    }

    public void Start()
    {
        _rpc.StartListening();
    }

    public Task<int> WaitForExitAsync() => _exitTcs.Task;

    [JsonRpcMethod(Methods.InitializeName, UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(InitializeParams @params)
    {
        string? rootPath = null;
#pragma warning disable CS0618
        if (@params.RootUri is not null)
        {
            rootPath = @params.RootUri.LocalPath;
        }
        else if (!string.IsNullOrEmpty(@params.RootPath))
        {
            rootPath = @params.RootPath;
        }
#pragma warning restore CS0618

        if (!string.IsNullOrEmpty(rootPath) && Directory.Exists(rootPath))
        {
            try
            {
                _workspaceManager = UnityWorkspaceManager.Create(rootPath);
                _diagnosticsHandler = new DiagnosticsHandler(_rpc, _workspaceManager);
            }
            catch
            {
                // If loading fails, keep running with empty workspace
            }
        }

        return new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Full,
                    Save = new SaveOptions { IncludeText = false },
                },
                HoverProvider = true,
                DefinitionProvider = true,
                ReferencesProvider = true,
                CompletionProvider = new CompletionOptions
                {
                    TriggerCharacters = new[] { "." },
                    ResolveProvider = false,
                },
            },
        };
    }

    [JsonRpcMethod(Methods.InitializedName)]
    public void Initialized()
    {
        // Ready to process language features
    }

    [JsonRpcMethod(Methods.ShutdownName)]
    public object? Shutdown()
    {
        _isShutdown = true;
        _workspaceManager?.Dispose();
        _workspaceManager = null;
        _diagnosticsHandler = null;
        return null;
    }

    [JsonRpcMethod(Methods.ExitName)]
    public void Exit()
    {
        _exitTcs.TrySetResult(_isShutdown ? 0 : 1);
        Dispose();
    }

    public void Dispose()
    {
        _workspaceManager?.Dispose();
        _rpc.Dispose();
        _input.Dispose();
        _output.Dispose();
    }
}
