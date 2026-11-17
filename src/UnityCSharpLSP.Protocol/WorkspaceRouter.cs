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
    private readonly List<Entry> _entries = new();
    private readonly List<string> _rootPaths = new();

    public UnityWorkspaceManager? DefaultManager
    {
        get
        {
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
        lock (_lock)
        {
            var projectCount = _entries.Sum(e => e.Manager.CurrentSolution.ProjectIds.Count);
            var documentCount = _entries.Sum(e => e.Manager.CurrentSolution.Projects
                .Sum(p => p.DocumentIds.Count));
            return $"workspaces={_entries.Count}; projects={projectCount}; documents={documentCount}";
        }
    }

    public void Dispose()
    {
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
