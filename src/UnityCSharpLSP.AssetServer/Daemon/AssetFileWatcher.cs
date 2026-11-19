using System.Threading.Channels;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Daemon;

/// <summary>
/// Watches Unity asset and <c>.meta</c> files under a project root using <see cref="FileSystemWatcher"/>,
/// batches bursts via a <see cref="Channel{T}"/> with a debounce window, and incrementally updates
/// <see cref="AssetSqliteStore"/> and <see cref="InMemoryAssetGraph"/>.
/// </summary>
public sealed class AssetFileWatcher : IDisposable
{
    private const string RescanSentinel = "\0RESCAN";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library",
        "Temp",
        "Logs",
        "obj",
        "bin",
        ".git",
        ".hancho",
        ".vs",
    };

    private static readonly string[] WatchedExtensions =
    [
        ".meta",
        ".prefab",
        ".unity",
        ".asset",
    ];

    private readonly AssetSqliteStore _store;
    private readonly TimeSpan _debounceWindow;
    private readonly TimeSpan _maxBatchDelay;
    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleLock = new();

    private FileSystemWatcher? _watcher;
    private Task? _processingTask;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Raised after a debounced batch of file events has been committed to SQLite and the in-memory graph.
    /// </summary>
    public event Action<IReadOnlyCollection<string>>? BatchProcessed;

    /// <summary>
    /// Raised after a full workspace rescan has been committed, invalidating all incremental caches.
    /// </summary>
    public event Action? FullRescanProcessed;

    public AssetFileWatcher(AssetSqliteStore store, TimeSpan? debounceWindow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _debounceWindow = debounceWindow ?? TimeSpan.FromMilliseconds(200);
        if (_debounceWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceWindow), "Debounce window must be positive.");
        }

        double maxMs = Math.Max(_debounceWindow.TotalMilliseconds * 3.0, 600.0);
        _maxBatchDelay = TimeSpan.FromMilliseconds(maxMs);

        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>
    /// Starts watching the project root directory and processing debounced updates.
    /// Safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _processingTask = Task.Run(() => ProcessLoopAsync(_cts.Token));

            if (!Directory.Exists(_store.ProjectRoot))
            {
                return;
            }

            var watcher = new FileSystemWatcher(_store.ProjectRoot)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 65536,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
            };

            watcher.Filters.Add("*.meta");
            watcher.Filters.Add("*.prefab");
            watcher.Filters.Add("*.unity");
            watcher.Filters.Add("*.asset");

            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
        }
    }

    /// <summary>
    /// Stops watching and disposes underlying watcher and background processing resources.
    /// </summary>
    public void Dispose()
    {
        FileSystemWatcher? watcherToDispose;
        Task? processingTask;

        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watcherToDispose = _watcher;
            _watcher = null;
            processingTask = _processingTask;
            _processingTask = null;
        }

        if (watcherToDispose is not null)
        {
            watcherToDispose.EnableRaisingEvents = false;
            watcherToDispose.Created -= OnChanged;
            watcherToDispose.Changed -= OnChanged;
            watcherToDispose.Deleted -= OnChanged;
            watcherToDispose.Renamed -= OnRenamed;
            watcherToDispose.Error -= OnError;
            watcherToDispose.Dispose();
        }

        _channel.Writer.TryComplete();
        _cts.Cancel();

        if (processingTask is not null)
        {
            try
            {
                processingTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        EnqueueIfRelevant(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        EnqueueIfRelevant(e.OldFullPath);
        EnqueueIfRelevant(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (!_disposed)
        {
            _channel.Writer.TryWrite(RescanSentinel);
        }
    }

    private void EnqueueIfRelevant(string fullPath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        if (!HasWatchedExtension(fullPath))
        {
            return;
        }

        if (IsIgnoredOrOutsideProject(fullPath))
        {
            return;
        }

        _channel.Writer.TryWrite(fullPath);
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        ChannelReader<string> reader = _channel.Reader;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                var batch = new HashSet<string>(PathComparer);
                bool needsFullRescan = false;
                DrainQueuedItems(reader, batch, ref needsFullRescan);

                DateTime deadlineUtc = DateTime.UtcNow + _maxBatchDelay;
                while (!ct.IsCancellationRequested)
                {
                    TimeSpan remainingCap = deadlineUtc - DateTime.UtcNow;
                    if (remainingCap <= TimeSpan.Zero)
                    {
                        break;
                    }

                    TimeSpan wait = remainingCap < _debounceWindow ? remainingCap : _debounceWindow;
                    using var debounceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    debounceCts.CancelAfter(wait);

                    bool hasMore;
                    try
                    {
                        hasMore = await reader.WaitToReadAsync(debounceCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Quiet period elapsed without further events.
                        break;
                    }

                    if (!hasMore)
                    {
                        break;
                    }

                    DrainQueuedItems(reader, batch, ref needsFullRescan);
                }

                if (ct.IsCancellationRequested || (!needsFullRescan && batch.Count == 0))
                {
                    continue;
                }

                await ProcessBatchAsync(batch, needsFullRescan, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ProcessBatchAsync(HashSet<string> batch, bool needsFullRescan, CancellationToken ct)
    {
        try
        {
            if (needsFullRescan)
            {
                await _store.SyncWorkspaceAsync(ct).ConfigureAwait(false);
            }
            else
            {
                foreach (string assetPath in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    await _store.UpdateSingleAssetAsync(assetPath, ct).ConfigureAwait(false);
                }
            }

            if (needsFullRescan)
            {
                FullRescanProcessed?.Invoke();
            }
            else
            {
                BatchProcessed?.Invoke(batch.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DrainQueuedItems(
        ChannelReader<string> reader,
        HashSet<string> batch,
        ref bool needsFullRescan)
    {
        while (reader.TryRead(out string? item))
        {
            if (string.Equals(item, RescanSentinel, StringComparison.Ordinal))
            {
                needsFullRescan = true;
                continue;
            }

            string normalized = NormalizeToAssetPath(item);
            batch.Add(normalized);
        }
    }

    private static string NormalizeToAssetPath(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath);
        return normalized.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^5]
            : normalized;
    }

    private static bool HasWatchedExtension(string fullPath)
    {
        foreach (string ext in WatchedExtensions)
        {
            if (fullPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsIgnoredOrOutsideProject(string fullPath)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(fullPath);
        }
        catch (Exception)
        {
            return true;
        }

        string relPath = Path.GetRelativePath(_store.ProjectRoot, normalized).Replace('\\', '/');
        if (string.IsNullOrEmpty(relPath) ||
            relPath == "." ||
            relPath == ".." ||
            relPath.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathRooted(relPath))
        {
            return true;
        }

        int start = 0;
        while (start < relPath.Length)
        {
            int slash = relPath.IndexOf('/', start);
            if (slash < 0)
            {
                break;
            }

            ReadOnlySpan<char> segment = relPath.AsSpan(start, slash - start);
            foreach (string ignored in IgnoredDirectoryNames)
            {
                if (segment.Equals(ignored.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            start = slash + 1;
        }

        return false;
    }
}
