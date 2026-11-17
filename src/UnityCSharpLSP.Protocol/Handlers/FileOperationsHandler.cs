using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Implements LSP 3.16 file operation events. "Will" requests return no automatic edits;
/// "did" notifications update the owning Roslyn workspace.
/// </summary>
public sealed class FileOperationsHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public FileOperationsHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(CustomLspMethods.WorkspaceWillCreateFilesName, UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceEdit?> WillCreateFiles(CreateFilesParams @params, CancellationToken ct = default)
        => Task.FromResult<WorkspaceEdit?>(null);

    [JsonRpcMethod(CustomLspMethods.WorkspaceDidCreateFilesName, UseSingleObjectParameterDeserialization = true)]
    public void DidCreateFiles(CreateFilesParams @params)
    {
        if (@params?.Files is null) return;
        foreach (var file in @params.Files)
        {
            var path = ToPath(file?.Uri);
            if (path is null) continue;
            _workspaceManagerProvider(path)?.AddOrRefreshDiskFile(path);
        }
    }

    [JsonRpcMethod(CustomLspMethods.WorkspaceWillRenameFilesName, UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceEdit?> WillRenameFiles(RenameFilesParams @params, CancellationToken ct = default)
        => Task.FromResult<WorkspaceEdit?>(null);

    [JsonRpcMethod(CustomLspMethods.WorkspaceDidRenameFilesName, UseSingleObjectParameterDeserialization = true)]
    public void DidRenameFiles(RenameFilesParams @params)
    {
        if (@params?.Files is null) return;
        foreach (var file in @params.Files)
        {
            var oldPath = ToPath(file?.OldUri);
            var newPath = ToPath(file?.NewUri);
            if (oldPath is not null)
            {
                _workspaceManagerProvider(oldPath)?.RemoveDocument(oldPath);
            }

            if (newPath is not null)
            {
                _workspaceManagerProvider(newPath)?.AddOrRefreshDiskFile(newPath);
            }
        }
    }

    [JsonRpcMethod(CustomLspMethods.WorkspaceWillDeleteFilesName, UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceEdit?> WillDeleteFiles(DeleteFilesParams @params, CancellationToken ct = default)
        => Task.FromResult<WorkspaceEdit?>(null);

    [JsonRpcMethod(CustomLspMethods.WorkspaceDidDeleteFilesName, UseSingleObjectParameterDeserialization = true)]
    public void DidDeleteFiles(DeleteFilesParams @params)
    {
        if (@params?.Files is null) return;
        foreach (var file in @params.Files)
        {
            var path = ToPath(file?.Uri);
            if (path is null) continue;
            _workspaceManagerProvider(path)?.RemoveDocument(path);
        }
    }

    private static string? ToPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.LocalPath : uri;
    }
}
