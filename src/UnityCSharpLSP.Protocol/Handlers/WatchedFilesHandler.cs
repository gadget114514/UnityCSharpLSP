using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>workspace/didChangeWatchedFiles</c> notifications sent by the client.
/// </summary>
public sealed class WatchedFilesHandler
{
    private readonly Func<UnityWorkspaceManager?> _workspaceManagerProvider;

    public WatchedFilesHandler(Func<UnityWorkspaceManager?> workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.WorkspaceDidChangeWatchedFilesName, UseSingleObjectParameterDeserialization = true)]
    public void DidChangeWatchedFiles(DidChangeWatchedFilesParams @params)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null || @params.Changes is null) return;

        foreach (var change in @params.Changes)
        {
            if (change.Uri is null) continue;
            var path = change.Uri.LocalPath;

            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
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
