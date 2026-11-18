using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles textDocument synchronization notifications (didOpen, didChange, willSave,
/// willSaveWaitUntil, didSave, didClose) and routes each one to the owning workspace.
/// </summary>
public sealed class TextDocumentSyncHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;
    private readonly Func<DiagnosticsHandler?> _diagnosticsHandlerProvider;

    public TextDocumentSyncHandler(
        WorkspaceManagerProvider workspaceManagerProvider,
        Func<DiagnosticsHandler?> diagnosticsHandlerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
        _diagnosticsHandlerProvider = diagnosticsHandlerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentDidOpenName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidOpen(DidOpenTextDocumentParams @params)
    {
        if (@params.TextDocument?.Uri is null) return;
        var ws = _workspaceManagerProvider(@params.TextDocument.Uri.LocalPath);
        if (ws is null) return;

        var filePath = @params.TextDocument.Uri.LocalPath;
        if (!UnityAssetFileHelper.IsYamlAssetFile(filePath))
        {
            var text = @params.TextDocument.Text;
            ws.OpenOrUpdateDocument(filePath, text, @params.TextDocument.Version);
        }

        await PublishAsync(@params.TextDocument.Uri, filePath).ConfigureAwait(false);
    }

    [JsonRpcMethod(Methods.TextDocumentDidChangeName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidChange(DidChangeTextDocumentParams @params)
    {
        if (@params.TextDocument?.Uri is null) return;
        var filePath = @params.TextDocument.Uri.LocalPath;
        var ws = _workspaceManagerProvider(filePath);
        if (ws is null) return;

        if (@params.ContentChanges is not { Length: > 0 } changes)
        {
            return;
        }

        var doc = ws.GetDocument(filePath);
        if (doc is null)
        {
            return;
        }

        var sourceText = await doc.GetTextAsync().ConfigureAwait(false);
        foreach (var change in changes)
        {
            if (change.Range is null)
            {
                sourceText = SourceText.From(change.Text);
                continue;
            }

            var start = sourceText.Lines.GetPosition(new LinePosition(
                change.Range.Start.Line,
                change.Range.Start.Character));
            var end = sourceText.Lines.GetPosition(new LinePosition(
                change.Range.End.Line,
                change.Range.End.Character));
            sourceText = sourceText.WithChanges(new TextChange(
                TextSpan.FromBounds(start, end),
                change.Text));
        }

        ws.UpdateDocumentText(filePath, sourceText, @params.TextDocument.Version);
        await PublishAsync(@params.TextDocument.Uri, filePath).ConfigureAwait(false);
    }

    [JsonRpcMethod(Methods.TextDocumentWillSaveName, UseSingleObjectParameterDeserialization = true)]
    public void WillSave(WillSaveTextDocumentParams @params)
    {
        // No workspace edits are produced before a save; the hook exists so clients can register it.
    }

    [JsonRpcMethod(Methods.TextDocumentWillSaveWaitUntilName, UseSingleObjectParameterDeserialization = true)]
    public Task<TextEdit[]?> WillSaveWaitUntil(WillSaveTextDocumentParams @params, CancellationToken ct = default)
    {
        return Task.FromResult<TextEdit[]?>(Array.Empty<TextEdit>());
    }

    [JsonRpcMethod(Methods.TextDocumentDidSaveName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidSave(DidSaveTextDocumentParams @params)
    {
        if (@params.TextDocument?.Uri is null) return;
        var ws = _workspaceManagerProvider(@params.TextDocument.Uri.LocalPath);
        if (ws is null) return;

        var filePath = @params.TextDocument.Uri.LocalPath;
        if (UnityAssetFileHelper.IsYamlAssetFile(filePath))
        {
            await PublishAsync(@params.TextDocument.Uri, filePath).ConfigureAwait(false);
            return;
        }

        string text;
        if (@params.Text is not null)
        {
            text = @params.Text;
        }
        else
        {
            try
            {
                text = File.ReadAllText(filePath);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }

        ws.OpenOrUpdateDocument(filePath, text);
        await PublishAsync(@params.TextDocument.Uri, filePath).ConfigureAwait(false);
    }

    [JsonRpcMethod(Methods.TextDocumentDidCloseName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidClose(DidCloseTextDocumentParams @params)
    {
        if (@params.TextDocument?.Uri is null) return;
        var ws = _workspaceManagerProvider(@params.TextDocument.Uri.LocalPath);
        if (ws is null) return;

        ws.CloseDocument(@params.TextDocument.Uri.LocalPath);

        var diag = _diagnosticsHandlerProvider();
        if (diag is not null)
        {
            await diag.ClearDiagnosticsAsync(@params.TextDocument.Uri).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(Uri uri, string filePath)
    {
        var diag = _diagnosticsHandlerProvider();
        if (diag is not null)
        {
            await diag.PublishDiagnosticsAsync(uri, filePath).ConfigureAwait(false);
        }
    }
}
