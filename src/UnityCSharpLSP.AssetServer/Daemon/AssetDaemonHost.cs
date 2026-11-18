using System.IO.Pipes;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;

namespace UnityCSharpLSP.AssetServer.Daemon;

/// <summary>
/// Multi-client Named Pipe JSON-RPC daemon host wrapping <see cref="AssetDataServiceImpl"/>.
/// Automatically shuts down on <see cref="IAssetDataService.ShutdownAsync"/> or after
/// active connections remain at zero for the configured idle timeout.
/// </summary>
public sealed class AssetDaemonHost : IDisposable
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromMilliseconds(250);

    private readonly string _projectRoot;
    private readonly string _pipeName;
    private readonly TimeSpan _idleTimeout;
    private readonly string? _databasePath;
    private readonly bool _enableFileWatcher;
    private readonly bool _useSingleInstanceMutex;

    private readonly object _stateLock = new();
    private readonly Dictionary<long, ActiveClientConnection> _connections = new();

    private AssetDataServiceImpl? _service;
    private CancellationTokenSource? _shutdownCts;
    private Timer? _idleTimer;
    private long _nextConnectionId;
    private int _activeConnections;
    private bool _shutdownRequested;
    private bool _disposed;

    public string ProjectRoot => _projectRoot;
    public string PipeName => _pipeName;
    public TimeSpan IdleTimeout => _idleTimeout;
    public int ActiveConnectionCount => Volatile.Read(ref _activeConnections);
    public AssetDataServiceImpl? Service => _service;

    public AssetDaemonHost(
        string projectRoot,
        TimeSpan? idleTimeout = null,
        string? pipeName = null,
        string? databasePath = null,
        bool enableFileWatcher = true,
        bool? useSingleInstanceMutex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        TimeSpan resolvedIdleTimeout = idleTimeout ?? DefaultIdleTimeout;
        if (resolvedIdleTimeout < TimeSpan.Zero && resolvedIdleTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "Idle timeout must be non-negative or Timeout.InfiniteTimeSpan.");
        }

        _projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        _pipeName = AssetPipeNaming.GetPipeName(_projectRoot, pipeName);
        _idleTimeout = resolvedIdleTimeout;
        _databasePath = databasePath;
        _enableFileWatcher = enableFileWatcher;
        // Skip the global project-root mutex when an isolated custom pipe name is supplied (e.g., unit tests).
        _useSingleInstanceMutex = useSingleInstanceMutex ?? string.IsNullOrWhiteSpace(pipeName);
    }

    /// <summary>
    /// Starts an <see cref="AssetDaemonHost"/> for <paramref name="projectRoot"/> and runs until
    /// shutdown or idle timeout.
    /// </summary>
    public static async Task RunAsync(
        string projectRoot,
        TimeSpan idleTimeout,
        CancellationToken ct = default,
        string? pipeName = null,
        string? databasePath = null,
        bool enableFileWatcher = true,
        bool? useSingleInstanceMutex = null)
    {
        using var host = new AssetDaemonHost(
            projectRoot,
            idleTimeout,
            pipeName,
            databasePath,
            enableFileWatcher,
            useSingleInstanceMutex);
        await host.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the asset service in the background and immediately begins accepting
    /// concurrent Named Pipe client connections.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Mutex? instanceMutex = null;
        if (_useSingleInstanceMutex)
        {
            string mutexName = AssetPipeNaming.GetMutexName(_projectRoot);
            instanceMutex = new Mutex(initiallyOwned: false, mutexName);
            bool acquired;
            try
            {
                acquired = instanceMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                instanceMutex.Dispose();
                return;
            }
        }

        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var service = new AssetDataServiceImpl(
            _projectRoot,
            _databasePath,
            _enableFileWatcher);

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _service = service;
            _shutdownCts = shutdownCts;
            _shutdownRequested = false;
            _activeConnections = 0;
        }

        EventHandler shutdownHandler = (_, _) => RequestShutdown();
        service.ShutdownRequested += shutdownHandler;

        using var idleTimer = new Timer(OnIdleTimerElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_stateLock)
        {
            _idleTimer = idleTimer;
            ArmIdleTimerLocked();
        }

        // Start workspace indexing in the background so Named Pipe listening starts immediately.
        Task initTask = Task.Run(async () =>
        {
            try
            {
                await service.InitializeAndSyncAsync(shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AssetDaemonHost] Background initialization failed: {ex.Message}");
            }
        }, CancellationToken.None);

        try
        {
            await AcceptLoopAsync(service, shutdownCts.Token).ConfigureAwait(false);
        }
        finally
        {
            service.ShutdownRequested -= shutdownHandler;

            lock (_stateLock)
            {
                _shutdownRequested = true;
                _idleTimer = null;
            }

            idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            await DrainAndCloseConnectionsAsync().ConfigureAwait(false);

            try
            {
                shutdownCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            await initTask.ConfigureAwait(false);

            lock (_stateLock)
            {
                _shutdownCts = null;
                _service = null;
            }

            service.Dispose();
            instanceMutex?.Dispose();
        }
    }

    /// <summary>
    /// Requests the daemon host to stop accepting new connections and shut down cleanly.
    /// </summary>
    public void RequestShutdown()
    {
        lock (_stateLock)
        {
            RequestShutdownLocked();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RequestShutdown();
    }

    private async Task AcceptLoopAsync(AssetDataServiceImpl service, CancellationToken shutdownToken)
    {
        while (!shutdownToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipeStream = null;
            try
            {
                pipeStream = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipeStream.WaitForConnectionAsync(shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (pipeStream is not null)
                {
                    await pipeStream.DisposeAsync().ConfigureAwait(false);
                }
                break;
            }
            catch (ObjectDisposedException)
            {
                if (pipeStream is not null)
                {
                    await pipeStream.DisposeAsync().ConfigureAwait(false);
                }
                break;
            }

            long connectionId = 0;
            bool accepted = false;
            lock (_stateLock)
            {
                if (!_shutdownRequested && !shutdownToken.IsCancellationRequested)
                {
                    connectionId = ++_nextConnectionId;
                    _activeConnections++;
                    _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    accepted = true;
                }
            }

            if (!accepted)
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                break;
            }

            StartClientSession(connectionId, pipeStream, service);
        }
    }

    private void StartClientSession(long connectionId, NamedPipeServerStream pipeStream, AssetDataServiceImpl service)
    {
        var formatter = new JsonMessageFormatter();
        var handler = new HeaderDelimitedMessageHandler(pipeStream, pipeStream, formatter);
        var jsonRpc = new JsonRpc(handler);
        jsonRpc.AddLocalRpcTarget<IAssetDataService>(service, null);

        var connection = new ActiveClientConnection(connectionId, pipeStream, jsonRpc);

        Task sessionTask = RunClientSessionAsync(connection);
        connection.SessionTask = sessionTask;

        lock (_stateLock)
        {
            _connections[connectionId] = connection;
        }

        jsonRpc.StartListening();
    }

    private async Task RunClientSessionAsync(ActiveClientConnection connection)
    {
        try
        {
            await connection.JsonRpc.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AssetDaemonHost] Client session error: {ex.Message}");
        }
        finally
        {
            connection.JsonRpc.Dispose();
            await connection.PipeStream.DisposeAsync().ConfigureAwait(false);

            lock (_stateLock)
            {
                _connections.Remove(connection.ConnectionId);
                if (_activeConnections > 0)
                {
                    _activeConnections--;
                }

                if (_activeConnections == 0 && !_shutdownRequested)
                {
                    ArmIdleTimerLocked();
                }
            }
        }
    }

    private async Task DrainAndCloseConnectionsAsync()
    {
        ActiveClientConnection[] snapshot;
        lock (_stateLock)
        {
            snapshot = _connections.Values.ToArray();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        Task[] tasks = snapshot
            .Select(static c => c.SessionTask)
            .Where(static t => t is not null)
            .ToArray()!;

        if (tasks.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(ShutdownDrainTimeout)).ConfigureAwait(false);
        }

        foreach (ActiveClientConnection conn in snapshot)
        {
            try
            {
                conn.JsonRpc.Dispose();
            }
            catch
            {
            }

            try
            {
                await conn.PipeStream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private void OnIdleTimerElapsed(object? state)
    {
        lock (_stateLock)
        {
            if (_activeConnections == 0 && !_shutdownRequested)
            {
                RequestShutdownLocked();
            }
        }
    }

    private void ArmIdleTimerLocked()
    {
        if (_idleTimer is null || _shutdownRequested || _idleTimeout == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
    }

    private void RequestShutdownLocked()
    {
        if (_shutdownRequested)
        {
            return;
        }

        _shutdownRequested = true;
        _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        try
        {
            _shutdownCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class ActiveClientConnection
    {
        public ActiveClientConnection(long connectionId, NamedPipeServerStream pipeStream, JsonRpc jsonRpc)
        {
            ConnectionId = connectionId;
            PipeStream = pipeStream;
            JsonRpc = jsonRpc;
        }

        public long ConnectionId { get; }
        public NamedPipeServerStream PipeStream { get; }
        public JsonRpc JsonRpc { get; }
        public Task? SessionTask { get; set; }
    }
}
