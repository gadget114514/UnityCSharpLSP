using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;

namespace UnityCSharpLSP.AssetServer.Daemon;

/// <summary>
/// Client wrapper implementing <see cref="IAssetDataService"/> over a Named Pipe connection to
/// <see cref="AssetDaemonHost"/>, with automatic daemon spawning when running inside
/// <c>UnityCSharpLSP.Server</c> and seamless in-process <see cref="AssetDataServiceImpl"/> fallback.
/// </summary>
public sealed class AssetDaemonClient : IAssetDataService, IDisposable
{
    public const int DefaultConnectTimeoutMs = 150;
    public const int DefaultSpawnWaitTimeoutMs = 3000;

    private readonly string _projectRoot;
    private readonly string _pipeName;
    private readonly bool _hasCustomPipeName;
    private readonly int _connectTimeoutMs;
    private readonly int _spawnWaitTimeoutMs;
    private readonly string? _databasePath;
    private readonly bool _enableFileWatcher;
    private readonly bool _allowFallback;

    private readonly SemaphoreSlim _initLock = new(1, 1);

    private IAssetDataService? _target;
    private NamedPipeClientStream? _pipeStream;
    private JsonRpc? _rpc;
    private AssetDataServiceImpl? _inProcessService;
    private int _disposed;

    public string ProjectRoot => _projectRoot;
    public string PipeName => _pipeName;
    public bool IsInProcessFallback => _inProcessService is not null;
    public bool IsConnected => _pipeStream is { IsConnected: true };

    private AssetDaemonClient(
        string projectRoot,
        string pipeName,
        bool hasCustomPipeName,
        int connectTimeoutMs,
        int spawnWaitTimeoutMs,
        string? databasePath,
        bool enableFileWatcher,
        bool allowFallback)
    {
        _projectRoot = projectRoot;
        _pipeName = pipeName;
        _hasCustomPipeName = hasCustomPipeName;
        _connectTimeoutMs = connectTimeoutMs;
        _spawnWaitTimeoutMs = spawnWaitTimeoutMs;
        _databasePath = databasePath;
        _enableFileWatcher = enableFileWatcher;
        _allowFallback = allowFallback;
    }

    /// <summary>
    /// Creates an <see cref="AssetDaemonClient"/> that lazily connects to the project's daemon,
    /// spawns it if running from <c>UnityCSharpLSP.Server</c>, or falls back to an in-process
    /// <see cref="AssetDataServiceImpl"/> when running inside a test host or restricted environment.
    /// </summary>
    public static AssetDaemonClient CreateOrFallbackInProcess(
        string projectRoot,
        string? pipeName = null,
        string? databasePath = null,
        bool enableFileWatcher = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        bool hasCustomPipeName = !string.IsNullOrWhiteSpace(pipeName);
        string resolvedPipeName = AssetPipeNaming.GetPipeName(normalizedRoot, pipeName);

        return new AssetDaemonClient(
            normalizedRoot,
            resolvedPipeName,
            hasCustomPipeName,
            DefaultConnectTimeoutMs,
            DefaultSpawnWaitTimeoutMs,
            databasePath,
            enableFileWatcher,
            allowFallback: true);
    }

    /// <summary>
    /// Eagerly connects to a running daemon, spawns one if running from <c>UnityCSharpLSP.Server</c>,
    /// or falls back to an initialized in-process <see cref="AssetDataServiceImpl"/>.
    /// </summary>
    public static async Task<AssetDaemonClient> ConnectOrSpawnAsync(
        string projectRoot,
        string? pipeName = null,
        int connectTimeoutMs = DefaultConnectTimeoutMs,
        int spawnWaitTimeoutMs = DefaultSpawnWaitTimeoutMs,
        string? databasePath = null,
        bool enableFileWatcher = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        bool hasCustomPipeName = !string.IsNullOrWhiteSpace(pipeName);
        string resolvedPipeName = AssetPipeNaming.GetPipeName(normalizedRoot, pipeName);

        var client = new AssetDaemonClient(
            normalizedRoot,
            resolvedPipeName,
            hasCustomPipeName,
            connectTimeoutMs,
            spawnWaitTimeoutMs,
            databasePath,
            enableFileWatcher,
            allowFallback: true);

        try
        {
            await client.EnsureInitializedAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Connects directly to an already-running daemon over a Named Pipe without falling back in-process.
    /// </summary>
    public static async Task<AssetDaemonClient> ConnectAsync(
        string pipeName,
        int connectTimeoutMs = DefaultSpawnWaitTimeoutMs,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        var client = new AssetDaemonClient(
            projectRoot: string.Empty,
            pipeName,
            hasCustomPipeName: true,
            connectTimeoutMs,
            spawnWaitTimeoutMs: 0,
            databasePath: null,
            enableFileWatcher: false,
            allowFallback: false);

        try
        {
            await client.EnsureInitializedAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<DaemonHandshakeResponse> HandshakeAsync(DaemonHandshakeRequest request, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.HandshakeAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<DaemonStatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.GetStatusAsync(ct).ConfigureAwait(false);
    }

    public async Task<AssetEntryDto?> GetAssetByGuidAsync(string guidHex, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.GetAssetByGuidAsync(guidHex, ct).ConfigureAwait(false);
    }

    public async Task<AssetEntryDto?> GetAssetByPathAsync(string filePath, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.GetAssetByPathAsync(filePath, ct).ConfigureAwait(false);
    }

    public async Task<ScriptReferenceCountDto[]> GetScriptReferenceCountsAsync(string[] scriptGuids, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.GetScriptReferenceCountsAsync(scriptGuids, ct).ConfigureAwait(false);
    }

    public async Task<AssetReferenceResultDto> FindReferencesAsync(AssetReferenceQueryDto query, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.FindReferencesAsync(query, ct).ConfigureAwait(false);
    }

    public async Task<AssetWorkspaceEditDto> ComputeRenameEditsAsync(AssetRenameQueryDto query, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.ComputeRenameEditsAsync(query, ct).ConfigureAwait(false);
    }

    public async Task<AssetDocumentLinksDto> GetDocumentLinksAsync(string assetPath, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.GetDocumentLinksAsync(assetPath, ct).ConfigureAwait(false);
    }

    public async Task<AssetHoverDto?> GetHoverAsync(string assetPath, int line, int character, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.GetHoverAsync(assetPath, line, character, ct).ConfigureAwait(false);
    }

    public async Task<AssetLocationDto?> GetDefinitionAsync(string assetPath, int line, int character, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.GetDefinitionAsync(assetPath, line, character, ct).ConfigureAwait(false);
    }

    public async Task<AssetHierarchyNodeDto[]> GetHierarchyAsync(string assetPath, int maxDepth, bool includeSerializedValues, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.GetHierarchyAsync(assetPath, maxDepth, includeSerializedValues, ct).ConfigureAwait(false);
    }

    public async Task<BuildSceneDto[]> GetBuildScenesAsync(CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.GetBuildScenesAsync(ct).ConfigureAwait(false);
    }

    public async Task<AssetDiagnosticDto[]> GetAssetDiagnosticsAsync(string assetPath, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.GetAssetDiagnosticsAsync(assetPath, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsMethodReferencedByUnityEventAsync(string scriptGuid, string methodName, CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.IsMethodReferencedByUnityEventAsync(scriptGuid, methodName, ct).ConfigureAwait(false);
    }

    public async Task<DaemonStatusResponse> RescanAsync(CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await target.RescanAsync(ct).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        IAssetDataService target = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await target.ShutdownAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _target = null;

        try { _rpc?.Dispose(); } catch { }
        _rpc = null;

        try { _pipeStream?.Dispose(); } catch { }
        _pipeStream = null;

        try { _inProcessService?.Dispose(); } catch { }
        _inProcessService = null;
    }

    private async Task<IAssetDataService> EnsureInitializedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        IAssetDataService? existing = Volatile.Read(ref _target);
        if (existing is not null)
        {
            return existing;
        }

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_target is not null)
            {
                return _target;
            }

            if (!_allowFallback)
            {
                (NamedPipeClientStream pipe, JsonRpc rpc, IAssetDataService proxy) =
                    await ConnectPipeCoreAsync(_pipeName, _connectTimeoutMs, ct).ConfigureAwait(false);

                if (Volatile.Read(ref _disposed) != 0)
                {
                    rpc.Dispose();
                    pipe.Dispose();
                    throw new ObjectDisposedException(nameof(AssetDaemonClient));
                }

                _pipeStream = pipe;
                _rpc = rpc;
                Volatile.Write(ref _target, proxy);
                return proxy;
            }

            // 1. Try connecting to an already-running daemon over Named Pipe.
            (NamedPipeClientStream Pipe, JsonRpc Rpc, IAssetDataService Proxy)? connected =
                await TryConnectPipeAsync(_pipeName, _connectTimeoutMs, ct).ConfigureAwait(false);

            // 2. If not running and we are running inside UnityCSharpLSP.Server (not testhost),
            //    spawn `--asset-daemon --project-root <root>` and retry up to _spawnWaitTimeoutMs.
            if (connected is null && !_hasCustomPipeName && TrySpawnDaemonProcess(_projectRoot, out Process? spawnedProcess))
            {
                using (spawnedProcess)
                {
                    connected = await RetryConnectAfterSpawnAsync(
                        _pipeName,
                        _connectTimeoutMs,
                        _spawnWaitTimeoutMs,
                        spawnedProcess,
                        ct).ConfigureAwait(false);
                }
            }

            if (connected is not null)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    connected.Value.Rpc.Dispose();
                    connected.Value.Pipe.Dispose();
                    throw new ObjectDisposedException(nameof(AssetDaemonClient));
                }

                _pipeStream = connected.Value.Pipe;
                _rpc = connected.Value.Rpc;
                Volatile.Write(ref _target, connected.Value.Proxy);
                return connected.Value.Proxy;
            }

            // 3. Fallback seamlessly to an in-process AssetDataServiceImpl (e.g., inside testhost or restricted sandboxes).
            var fallback = new AssetDataServiceImpl(_projectRoot, _databasePath, _enableFileWatcher);
            try
            {
                await fallback.InitializeAndSyncAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                fallback.Dispose();
                throw;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                fallback.Dispose();
                throw new ObjectDisposedException(nameof(AssetDaemonClient));
            }

            _inProcessService = fallback;
            Volatile.Write(ref _target, fallback);
            return fallback;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task<(NamedPipeClientStream Pipe, JsonRpc Rpc, IAssetDataService Proxy)> ConnectPipeCoreAsync(
        string pipeName,
        int timeoutMs,
        CancellationToken ct)
    {
        var pipeStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipeStream.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            var formatter = new JsonMessageFormatter();
            var handler = new HeaderDelimitedMessageHandler(pipeStream, pipeStream, formatter);
            var rpc = new JsonRpc(handler);
#pragma warning disable StreamJsonRpc0003 // Dynamic proxy is intentional; contract uses Newtonsoft.Json JsonMessageFormatter
            IAssetDataService proxy = rpc.Attach<IAssetDataService>();
#pragma warning restore StreamJsonRpc0003
            rpc.StartListening();
            return (pipeStream, rpc, proxy);
        }
        catch
        {
            pipeStream.Dispose();
            throw;
        }
    }

    private static async Task<(NamedPipeClientStream Pipe, JsonRpc Rpc, IAssetDataService Proxy)?> TryConnectPipeAsync(
        string pipeName,
        int timeoutMs,
        CancellationToken ct)
    {
        try
        {
            return await ConnectPipeCoreAsync(pipeName, timeoutMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static async Task<(NamedPipeClientStream Pipe, JsonRpc Rpc, IAssetDataService Proxy)?> RetryConnectAfterSpawnAsync(
        string pipeName,
        int singleAttemptTimeoutMs,
        int totalWaitTimeoutMs,
        Process? spawnedProcess,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < totalWaitTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            int remainingMs = Math.Max(1, totalWaitTimeoutMs - (int)sw.ElapsedMilliseconds);
            int attemptTimeoutMs = Math.Min(singleAttemptTimeoutMs, remainingMs);

            (NamedPipeClientStream Pipe, JsonRpc Rpc, IAssetDataService Proxy)? result =
                await TryConnectPipeAsync(pipeName, attemptTimeoutMs, ct).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }

            if (spawnedProcess is not null && spawnedProcess.HasExited)
            {
                // One final attempt in case another daemon won the single-instance mutex race.
                return await TryConnectPipeAsync(pipeName, Math.Min(singleAttemptTimeoutMs, 100), ct).ConfigureAwait(false);
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static bool TrySpawnDaemonProcess(string projectRoot, out Process? process)
    {
        process = null;

        try
        {
            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                return false;
            }

            string exeName = Path.GetFileNameWithoutExtension(processPath);
            ProcessStartInfo startInfo;

            if (string.Equals(exeName, "UnityCSharpLSP.Server", StringComparison.OrdinalIgnoreCase))
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = processPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
            }
            else if (string.Equals(exeName, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string? entryLocation = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(entryLocation) ||
                    !string.Equals(Path.GetFileName(entryLocation), "UnityCSharpLSP.Server.dll", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(entryLocation))
                {
                    return false;
                }

                startInfo = new ProcessStartInfo
                {
                    FileName = processPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                startInfo.ArgumentList.Add(entryLocation);
            }
            else
            {
                return false;
            }

            startInfo.ArgumentList.Add("--asset-daemon");
            startInfo.ArgumentList.Add("--project-root");
            startInfo.ArgumentList.Add(projectRoot);

            process = Process.Start(startInfo);
            return process is not null;
        }
        catch
        {
            process = null;
            return false;
        }
    }
}
