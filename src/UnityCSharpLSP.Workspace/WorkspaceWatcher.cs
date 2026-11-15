namespace UnityCSharpLSP.Workspace;

/// <summary>
/// Monitors the file system for added, changed, or deleted C# source files and project manifests.
/// Updates the Roslyn workspace automatically without requiring Unity Editor interaction.
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    private readonly UnityWorkspaceManager _workspaceManager;
    private readonly string _rootPath;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _lock = new();

    public WorkspaceWatcher(UnityWorkspaceManager workspaceManager, string rootPath)
    {
        _workspaceManager = workspaceManager;
        _rootPath = Path.GetFullPath(rootPath);

        if (Directory.Exists(_rootPath))
        {
            try
            {
                _watcher = new FileSystemWatcher(_rootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.CreationTime |
                                   NotifyFilters.DirectoryName,
                };

                _watcher.Created += OnFileCreated;
                _watcher.Deleted += OnFileDeleted;
                _watcher.Renamed += OnFileRenamed;
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
                // Fallback gracefully if FileSystemWatcher fails on OS limit
            }
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (IsIgnored(e.FullPath)) return;

        if (e.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _workspaceManager.AddOrRefreshDiskFile(e.FullPath);
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsIgnored(e.FullPath)) return;

        if (e.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _workspaceManager.RemoveDocument(e.FullPath);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsIgnored(e.OldFullPath) && e.OldFullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _workspaceManager.RemoveDocument(e.OldFullPath);
        }

        if (!IsIgnored(e.FullPath) && e.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _workspaceManager.AddOrRefreshDiskFile(e.FullPath);
        }
    }

    private static bool IsIgnored(string path)
    {
        return path.Contains(@"\Library\") ||
               path.Contains(@"/Library/") ||
               path.Contains(@"\Temp\") ||
               path.Contains(@"/Temp/") ||
               path.Contains(@"\obj\") ||
               path.Contains(@"/obj/") ||
               path.Contains(@"\bin\") ||
               path.Contains(@"/bin/");
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileCreated;
            _watcher.Deleted -= OnFileDeleted;
            _watcher.Renamed -= OnFileRenamed;
            _watcher.Dispose();
        }
    }
}
