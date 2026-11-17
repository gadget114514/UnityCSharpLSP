using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>workspace/didChangeWatchedFiles</c> notifications, routing each change
/// to the workspace that owns the file.
/// </summary>
public sealed class WatchedFilesHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public WatchedFilesHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.WorkspaceDidChangeWatchedFilesName, UseSingleObjectParameterDeserialization = true)]
    public void DidChangeWatchedFiles(DidChangeWatchedFilesParams @params)
    {
        if (@params.Changes is null)
        {
            return;
        }

        foreach (var change in @params.Changes)
        {
            if (change.Uri is null)
            {
                continue;
            }

            var path = change.Uri.LocalPath;
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ws = _workspaceManagerProvider(path);
            if (ws is null)
            {
                continue;
            }

            switch (change.FileChangeType)
            {
                case FileChangeType.Created:
                case FileChangeType.Changed:
                    ws.AddOrRefreshDiskFile(path);
                    break;

                case FileChangeType.Deleted:
                    ws.RemoveDocument(path);
                    break;
            }
        }
    }
}
