using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Handlers;
using UnityCSharpLSP.Protocol.Models;
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
    private readonly ClientCapabilityStore _clientCapabilities = new();
    private readonly WorkspaceSettingsStore _settings = new();

    private WorkspaceRouter? _router;
    private bool _isShutdown;

    public UnityWorkspaceManager? WorkspaceManager => _router?.DefaultManager;
    public WorkspaceRouter? Router => _router;
    public WorkspaceSettingsStore Settings => _settings;
    public JsonRpc JsonRpc => _rpc;

    public LspJsonRpcSession(Stream input, Stream output)
    {
        _input = input;
        _output = output;

        var handler = new HeaderDelimitedMessageHandler(output, input, new JsonMessageFormatter());
        _rpc = new JsonRpc(handler);

        // Register lifecycle methods on this session
        _rpc.AddLocalRpcTarget(this);

        var manager = (WorkspaceManagerProvider)(path => _router?.GetManagerForFile(path));
        var allManagers = (Func<IReadOnlyList<UnityWorkspaceManager>>)(() =>
            _router?.AllManagers ?? Array.Empty<UnityWorkspaceManager>());

        // Register text document sync handler
        var syncHandler = new TextDocumentSyncHandler(manager, () => _diagnosticsHandler);
        _rpc.AddLocalRpcTarget(syncHandler);

        // Register language feature handlers
        _rpc.AddLocalRpcTarget(new HoverHandler(manager, _clientCapabilities));
        _rpc.AddLocalRpcTarget(new DefinitionHandler(manager));
        _rpc.AddLocalRpcTarget(new TypeDefinitionHandler(manager));
        _rpc.AddLocalRpcTarget(new DeclarationHandler(manager));
        _rpc.AddLocalRpcTarget(new CompletionHandler(manager, _clientCapabilities));
        _rpc.AddLocalRpcTarget(new ReferencesHandler(manager));
        _rpc.AddLocalRpcTarget(new DocumentSymbolHandler(manager, _clientCapabilities));
        _rpc.AddLocalRpcTarget(new DocumentHighlightHandler(manager));
        _rpc.AddLocalRpcTarget(new FoldingRangeHandler(manager));
        _rpc.AddLocalRpcTarget(new SelectionRangeHandler(manager));
        _rpc.AddLocalRpcTarget(new SignatureHelpHandler(manager));
        _rpc.AddLocalRpcTarget(new RenameHandler(manager, _settings));
        _rpc.AddLocalRpcTarget(new WorkspaceSymbolHandler(allManagers, _rpc));
        _rpc.AddLocalRpcTarget(new ImplementationHandler(manager));
        _rpc.AddLocalRpcTarget(new CallHierarchyHandler(manager));
        _rpc.AddLocalRpcTarget(new TypeHierarchyHandler(manager));
        _rpc.AddLocalRpcTarget(new MonikerHandler(manager));
        _rpc.AddLocalRpcTarget(new WatchedFilesHandler(manager));
        _rpc.AddLocalRpcTarget(new FormattingHandler(manager));
        _rpc.AddLocalRpcTarget(new CodeActionHandler(manager));
        _rpc.AddLocalRpcTarget(new SemanticTokensHandler(manager));
        _rpc.AddLocalRpcTarget(new InlayHintHandler(manager));
        _rpc.AddLocalRpcTarget(new CodeLensHandler(manager));
        _rpc.AddLocalRpcTarget(new DocumentLinkHandler(manager));
        _rpc.AddLocalRpcTarget(new LinkedEditingRangeHandler(manager));
        _rpc.AddLocalRpcTarget(new InlineValueHandler(manager));
        _rpc.AddLocalRpcTarget(new PullDiagnosticsHandler(manager, () => _diagnosticsHandler));
        _rpc.AddLocalRpcTarget(new WorkspaceDiagnosticsHandler(allManagers, () => _diagnosticsHandler));

        // Workspace-level handlers
        _rpc.AddLocalRpcTarget(new ExecuteCommandHandler(() => _router, _rpc));
        _rpc.AddLocalRpcTarget(new ConfigurationHandler(_settings));
        _rpc.AddLocalRpcTarget(new FileOperationsHandler(manager));

        _diagnosticsHandler = new DiagnosticsHandler(_rpc, manager, _settings);
    }

    private DiagnosticsHandler? _diagnosticsHandler;

    public void Start()
    {
        _rpc.StartListening();
    }

    public Task<int> WaitForExitAsync() => _exitTcs.Task;

    [JsonRpcMethod(Methods.InitializeName, UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(JObject @params)
    {
        var initializeParams = @params.ToObject<InitializeParams>() ?? new InitializeParams();
        _clientCapabilities.Update(
            initializeParams.Capabilities,
            @params["capabilities"] as JObject);

        var roots = CollectWorkspaceRoots(@params, initializeParams);
        if (_router is null || roots.Count > 0)
        {
            var newRouter = new WorkspaceRouter();
            newRouter.Initialize(roots);
            _router?.Dispose();
            _router = newRouter;
        }

        return new InitializeResult
        {
            Capabilities = new ExtendedServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Incremental,
                    Save = new SaveOptions { IncludeText = false },
                    WillSave = true,
                    WillSaveWaitUntil = true,
                },
                HoverProvider = true,
                DefinitionProvider = true,
                TypeDefinitionProvider = true,
                DeclarationProvider = true,
                ReferencesProvider = true,
                ImplementationProvider = true,
                DocumentSymbolProvider = true,
                DocumentHighlightProvider = true,
                WorkspaceSymbolProvider = new WorkspaceSymbolResolveOptions
                {
                    ResolveProvider = true,
                    WorkDoneProgress = true,
                },
                CallHierarchyProvider = true,
                FoldingRangeProvider = true,
                SelectionRangeProvider = true,
                TypeHierarchyProvider = true,
                MonikerProvider = true,
                RenameProvider = new RenameOptions { PrepareProvider = true },
                SignatureHelpProvider = new SignatureHelpOptions
                {
                    TriggerCharacters = new[] { "(", "," },
                    RetriggerCharacters = new[] { "," },
                },
                CompletionProvider = new CompletionOptions
                {
                    TriggerCharacters = new[] { ".", "\"" },
                    ResolveProvider = true,
                },
                DocumentFormattingProvider = true,
                DocumentRangeFormattingProvider = true,
                DocumentOnTypeFormattingProvider = new DocumentOnTypeFormattingOptions
                {
                    FirstTriggerCharacter = "}",
                    MoreTriggerCharacter = new[] { ";", "\n" },
                },
                CodeActionProvider = new CodeActionOptions
                {
                    CodeActionKinds = new[] { CodeActionKind.QuickFix, CodeActionKind.Refactor, CodeActionKind.Source },
                    ResolveProvider = true,
                },
                SemanticTokensOptions = SemanticTokensHandler.CreateOptions(),
                CodeLensProvider = new CodeLensOptions { ResolveProvider = true },
                DocumentLinkProvider = new DocumentLinkOptions { ResolveProvider = false },
                LinkedEditingRangeProvider = true,
                InlayHintProvider = new InlayHintOptions { ResolveProvider = false },
                InlineValueProvider = true,
                DiagnosticProvider = new DiagnosticOptions
                {
                    Identifier = "unitycsharp",
                    InterFileDependencies = false,
                    WorkspaceDiagnostics = true,
                },
                ExecuteCommandProvider = new ExecuteCommandOptions
                {
                    Commands = ExecuteCommandHandler.Commands,
                    WorkDoneProgress = true,
                },
                PositionEncoding = "utf-16",
                Workspace = new WorkspaceServerCapabilities
                {
                    FileOperations = new FileOperationOptions
                    {
                        DidCreate = new FileOperationRegistrationOptions(),
                        WillCreate = new FileOperationRegistrationOptions(),
                        DidRename = new FileOperationRegistrationOptions(),
                        WillRename = new FileOperationRegistrationOptions(),
                        DidDelete = new FileOperationRegistrationOptions(),
                        WillDelete = new FileOperationRegistrationOptions(),
                    },
                },
            },
        };
    }

    private static IReadOnlyList<string> CollectWorkspaceRoots(JObject @params, InitializeParams initializeParams)
    {
        var roots = new List<string>();
        if (@params["workspaceFolders"] is JArray folders)
        {
            foreach (var folder in folders)
            {
                var path = ToLocalPath(folder?["uri"]?.Value<string>());
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    roots.Add(path);
                }
            }
        }

        if (roots.Count > 0)
        {
            return roots;
        }

#pragma warning disable CS0618
        var fallback = ToLocalPath(initializeParams.RootUri?.OriginalString)
                       ?? initializeParams.RootPath;
#pragma warning restore CS0618
        return string.IsNullOrEmpty(fallback) || !Directory.Exists(fallback)
            ? Array.Empty<string>()
            : new[] { fallback };
    }

    private static string? ToLocalPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.LocalPath : uri;
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
        _diagnosticsHandler = null;
        _router?.Dispose();
        _router = null;
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
        _diagnosticsHandler = null;
        _router?.Dispose();
        _rpc.Dispose();
        _input.Dispose();
        _output.Dispose();
    }
}
