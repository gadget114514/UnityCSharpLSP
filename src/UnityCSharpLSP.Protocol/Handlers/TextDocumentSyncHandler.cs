using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles textDocument synchronization notifications (didOpen, didChange, didClose).
/// </summary>
public sealed class TextDocumentSyncHandler
{
    private readonly Func<UnityWorkspaceManager?> _workspaceManagerProvider;
    private readonly Func<DiagnosticsHandler?> _diagnosticsHandlerProvider;

    public TextDocumentSyncHandler(
        Func<UnityWorkspaceManager?> workspaceManagerProvider,
        Func<DiagnosticsHandler?> diagnosticsHandlerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
        _diagnosticsHandlerProvider = diagnosticsHandlerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentDidOpenName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidOpen(DidOpenTextDocumentParams @params)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null || @params.TextDocument?.Uri is null) return;

        var filePath = @params.TextDocument.Uri.LocalPath;
        var text = @params.TextDocument.Text;

        ws.OpenOrUpdateDocument(filePath, text);

        var diag = _diagnosticsHandlerProvider();
        if (diag is not null)
        {
            await diag.PublishDiagnosticsAsync(@params.TextDocument.Uri, filePath).ConfigureAwait(false);
        }
    }

    [JsonRpcMethod(Methods.TextDocumentDidChangeName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidChange(DidChangeTextDocumentParams @params)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null || @params.TextDocument?.Uri is null) return;

        var filePath = @params.TextDocument.Uri.LocalPath;
        if (@params.ContentChanges is { Length: > 0 } changes)
        {
            // Full sync provides the whole file in the last change element
            var newText = changes[^1].Text;
            ws.UpdateDocumentText(filePath, newText);

            var diag = _diagnosticsHandlerProvider();
            if (diag is not null)
            {
                await diag.PublishDiagnosticsAsync(@params.TextDocument.Uri, filePath).ConfigureAwait(false);
            }
        }
    }

    [JsonRpcMethod(Methods.TextDocumentDidCloseName, UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams @params)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null || @params.TextDocument?.Uri is null) return;

        var filePath = @params.TextDocument.Uri.LocalPath;
        ws.CloseDocument(filePath);
    }
}
