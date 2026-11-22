using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol;

/// <summary>
/// Owns one <see cref="UnityWorkspaceManager"/> per workspace folder and routes file URIs
/// to the manager whose root is the longest matching prefix. With a single folder it
/// behaves exactly like a plain workspace manager.
/// </summary>
public sealed class WorkspaceRouter : IDisposable
{
    private sealed class Entry : IDisposable
    {
        public Entry(string rootPath, UnityWorkspaceManager manager, WorkspaceWatcher? watcher)
        {
            RootPath = rootPath;
            Manager = manager;
            Watcher = watcher;
        }

        public string RootPath { get; }
        public UnityWorkspaceManager Manager { get; }
        public WorkspaceWatcher? Watcher { get; }

        public void Dispose()
        {
            Watcher?.Dispose();
            Manager.Dispose();
        }
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly object _lock = new();
    private readonly object _stateLock = new();
    private readonly List<Entry> _entries = new();
    private readonly List<string> _rootPaths = new();

    // Signalled when no background load is in flight. Accessors block on it so requests that
    // arrive while the workspace is still loading are answered once it is ready.
    private readonly ManualResetEventSlim _ready = new(initialState: true);
    private Task _loadTask = Task.CompletedTask;
    private bool _loading;
    private bool _disposed;

    /// <summary>Completes when the most recent background load has finished.</summary>
    public Task LoadTask
    {
        get
        {
            lock (_stateLock)
            {
                return _loadTask;
            }
        }
    }

    /// <summary>
    /// Starts building the workspace managers on a dedicated thread and returns immediately.
    /// Every accessor on this router waits for the load to finish before answering.
    /// </summary>
    public Task BeginInitialize(IEnumerable<string> rootPaths)
    {
        var roots = rootPaths.ToArray();
        lock (_stateLock)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _loading = true;
            _ready.Reset();
            _loadTask = Task.Factory.StartNew(
                () => RunBackgroundLoad(roots),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return _loadTask;
        }
    }

    private void RunBackgroundLoad(string[] roots)
    {
        try
        {
            Initialize(roots);
        }
        finally
        {
            bool disposeNow;
            lock (_stateLock)
            {
                _loading = false;
                disposeNow = _disposed;
            }

            if (disposeNow)
            {
                lock (_lock)
                {
                    DisposeEntries();
                }
            }

            _ready.Set();
        }
    }

    private void WaitReady() => _ready.Wait();

    public UnityWorkspaceManager? DefaultManager
    {
        get
        {
            WaitReady();
            lock (_lock)
            {
                return _entries.Count > 0 ? _entries[0].Manager : null;
            }
        }
    }

    public IReadOnlyList<UnityWorkspaceManager> AllManagers
    {
        get
        {
            WaitReady();
            lock (_lock)
            {
                return _entries.Select(e => e.Manager).ToArray();
            }
        }
    }

    public IReadOnlyList<string> RootPaths
    {
        get
        {
            WaitReady();
            lock (_lock)
            {
                return _rootPaths.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            WaitReady();
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Rebuilds all workspace managers from the supplied root paths. Invalid or unloadable
    /// folders are skipped so a partially configured session still works.
    /// </summary>
    public void Initialize(IEnumerable<string> rootPaths)
    {
        lock (_lock)
        {
            DisposeEntries();
            if (IsDisposed)
            {
                return;
            }

            _rootPaths.Clear();
            foreach (var candidate in rootPaths)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
                {
                    continue;
                }

                var full = NormalizeDirectory(candidate);
                if (!_rootPaths.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    _rootPaths.Add(full);
                }
            }

            foreach (var root in _rootPaths)
            {
                if (IsDisposed)
                {
                    break;
                }

                try
                {
                    var manager = UnityWorkspaceManager.Create(root);
                    var watcher = new WorkspaceWatcher(manager, root);
                    _entries.Add(new Entry(root, manager, watcher));
                }
                catch
                {
                    // Keep other roots alive when a single folder cannot be loaded.
                }
            }
        }
    }

    /// <summary>
    /// Rebuilds every manager from the previously configured roots.
    /// </summary>
    public void Reload()
    {
        WaitReady();
        string[] roots;
        lock (_lock)
        {
            roots = _rootPaths.ToArray();
        }

        Initialize(roots);
    }

    /// <summary>
    /// Returns the manager with the longest root prefix matching <paramref name="filePath"/>,
    /// or the default manager when there is no match.
    /// </summary>
    public UnityWorkspaceManager? GetManagerForFile(string? filePath)
    {
        WaitReady();
        if (string.IsNullOrEmpty(filePath))
        {
            return DefaultManager;
        }

        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch
        {
            return DefaultManager;
        }

        lock (_lock)
        {
            Entry? best = null;
            foreach (var entry in _entries)
            {
                var root = NormalizeDirectory(entry.RootPath);
                if (!full.StartsWith(root, PathComparison))
                {
                    continue;
                }

                if (best is null || root.Length > NormalizeDirectory(best.RootPath).Length)
                {
                    best = entry;
                }
            }

            return best?.Manager ?? (_entries.Count > 0 ? _entries[0].Manager : null);
        }
    }

    public string GetSummary()
    {
        WaitReady();
        lock (_lock)
        {
            var projectCount = _entries.Sum(e => e.Manager.CurrentSolution.ProjectIds.Count);
            var documentCount = _entries.Sum(e => e.Manager.CurrentSolution.Projects
                .Sum(p => p.DocumentIds.Count));
            return $"workspaces={_entries.Count}; projects={projectCount}; documents={documentCount}";
        }
    }

    private bool IsDisposed
    {
        get
        {
            lock (_stateLock)
            {
                return _disposed;
            }
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _disposed = true;
            if (_loading)
            {
                // The background load disposes whatever it built once it finishes, so shutdown
                // does not have to wait for Roslyn to finish loading.
                return;
            }
        }

        lock (_lock)
        {
            DisposeEntries();
        }
    }

    private void DisposeEntries()
    {
        foreach (var entry in _entries)
        {
            entry.Dispose();
        }

        _entries.Clear();
    }

    private static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar)
            ? full
            : full + Path.DirectorySeparatorChar;
    }
}
