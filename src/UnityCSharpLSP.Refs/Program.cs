namespace UnityCSharpLSP.Refs;

internal static class Program
{
    private const string HelpText = """
        lsp-refs - find C# symbol references via UnityCSharpLSP

        USAGE:
          lsp-refs <file> <line> <col> [--workspace <path>] [--include <glob>]... [--exclude <glob>]...
          lsp-refs --asset-daemon [--workspace <path>]   (resident mode)
          lsp-refs --help

        ARGUMENTS:
          <file>   Source file to inspect (.cs).
          <line>   1-based line number, as shown in editors.
          <col>    1-based column number.

        OPTIONS:
          --workspace <path>   Unity project root. Default: nearest ancestor of <file>
                               containing Assets/ or ProjectSettings/, else the file's directory.
          --include <glob>     Keep only references whose path matches the glob. Repeatable.
                               Patterns without '/' match the file name (e.g. '*.cs', 'Something*.cs').
                               '**' spans directories; a leading '~' matches a path substring
                               (e.g. '~Weapon'); a leading '!' negates a pattern.
          --exclude <glob>     Drop references whose path matches the glob. Repeatable.
          --asset-daemon       Resident mode: start a persistent Unity asset daemon and keep one
                               LSP server warm. Reads queries from stdin, one per line:
                                 <file> <line> <col>
                               until EOF or Ctrl+C. An initial query may also be given as args.
          -h, --help           Show this help and exit.

        OUTPUT:
          One location per line: <path>:<line> (1-based). Output includes the declaration.
          Empty output means no references were found. Diagnostics go to stderr.

        SERVER:
          Wrapper around 'UnityCSharpLSP.Server --stdio'. The server binary is resolved from
          $UNITYCSHARPLSP_SERVER, the lsp-refs directory, the current directory, or PATH.

        EXIT CODES:
          0  success     1  request failed     2  invalid arguments

        EXAMPLES:
          lsp-refs Assets/Scripts/Player.cs 12 9 --workspace D:/MyUnityProject
          lsp-refs --asset-daemon --workspace D:/MyUnityProject
          > Assets/Scripts/Player.cs 12 9
          > Assets/Scripts/GameConfig.cs 5 18

        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.Out.Write(HelpText);
            return 0;
        }

        string? workspaceArg = null;
        bool resident = false;
        var positional = new List<string>();
        var includePatterns = new List<string>();
        var excludePatterns = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                    if (++i >= args.Length) return Fail("--workspace にはパスを指定してください。");
                    workspaceArg = args[i];
                    break;
                case "--include":
                    if (++i >= args.Length) return Fail("--include には glob を指定してください。");
                    includePatterns.AddRange(SplitPatterns(args[i]));
                    break;
                case "--exclude":
                    if (++i >= args.Length) return Fail("--exclude には glob を指定してください。");
                    excludePatterns.AddRange(SplitPatterns(args[i]));
                    break;
                case "--asset-daemon":
                    resident = true;
                    break;
                case "--help":
                case "-h":
                    Console.Out.Write(HelpText);
                    return 0;
                default:
                    if (args[i].StartsWith('-')) return Fail($"未知のオプション: {args[i]}");
                    positional.Add(args[i]);
                    break;
            }
        }

        if (!resident && positional.Count != 3)
        {
            return Fail("<file> <line> <col> を指定してください。--help を参照してください。");
        }

        if (resident && positional.Count != 0 && positional.Count != 3)
        {
            return Fail("常駐モードではクエリは省略するか、<file> <line> <col> の3引数で指定してください。");
        }

        string workspace = ResolveWorkspace(workspaceArg, positional, resident);
        if (!Directory.Exists(workspace)) return Fail($"ワークスペースが見つかりません: {workspace}");

        string? serverPath = ResolveServerPath();
        if (serverPath is null)
        {
            return Fail("UnityCSharpLSP.Server.exe が見つかりません。$UNITYCSHARPLSP_SERVER で指定してください。");
        }

        AssetDaemonProcess? daemon = null;
        if (resident)
        {
            try
            {
                daemon = AssetDaemonProcess.Start(serverPath, workspace);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[lsp-refs] warning: could not start asset daemon: {ex.Message}");
            }
        }

        using var daemonScope = daemon;
        using var client = new LspRefsClient(serverPath, workspace);

        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await client.InitializeAsync(workspace, shutdownCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[lsp-refs] initialize failed: {ex.Message}");
            Console.Error.Write(client.GetStderr());
            return 1;
        }

        int failures = 0;
        if (positional.Count == 3)
        {
            if (!TryBuildQuery(positional.ToArray(), out var query, out string error))
            {
                Console.Error.WriteLine($"[lsp-refs] {error}");
                failures++;
            }
            else
            {
                failures += await RunQueryAsync(
                    client,
                    query,
                    CancellationToken.None,
                    includePatterns.ToArray(),
                    excludePatterns.ToArray()).ConfigureAwait(false);
            }
        }

        if (resident)
        {
            failures += await RunResidentLoopAsync(
                client,
                includePatterns.ToArray(),
                excludePatterns.ToArray()).ConfigureAwait(false);
        }

        await client.ShutdownAsync(shutdownCts.Token).ConfigureAwait(false);
        return failures == 0 ? 0 : 1;
    }

    private static string[] SplitPatterns(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<int> RunResidentLoopAsync(
        LspRefsClient client,
        string[] includePatterns,
        string[] excludePatterns)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        int failures = 0;

        while (!cts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Console.In.ReadLineAsync().WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                Console.Error.WriteLine("[lsp-refs] expected: <file> <line> <col>");
                failures++;
                continue;
            }

            string[] tokens = [string.Join(' ', parts[..^2]), parts[^2], parts[^1]];
            if (!TryBuildQuery(tokens, out var query, out string error))
            {
                Console.Error.WriteLine($"[lsp-refs] {error}");
                failures++;
                continue;
            }

            failures += await RunQueryAsync(
                client,
                query,
                cts.Token,
                includePatterns,
                excludePatterns).ConfigureAwait(false);
        }

        return failures;
    }

    private static async Task<int> RunQueryAsync(
        LspRefsClient client,
        Query query,
        CancellationToken ct,
        string[]? includePatterns = null,
        string[]? excludePatterns = null)
    {
        try
        {
            string fullPath = Path.GetFullPath(query.File);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("ソースファイルが見つかりません", fullPath);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            client.DidOpen(fullPath);
            var references = await client.ReferencesAsync(
                fullPath,
                query.Line - 1,
                query.Column - 1,
                timeout.Token,
                includePatterns,
                excludePatterns).ConfigureAwait(false);
            client.DidClose(fullPath);

            foreach (var (path, line) in references)
            {
                Console.Out.WriteLine($"{path}:{line + 1}");
            }

            Console.Out.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[lsp-refs] {ex.Message}");
            Console.Error.Write(client.GetStderr());
            return 1;
        }
    }

    private static bool TryBuildQuery(string[] tokens, out Query query, out string error)
    {
        query = default;
        error = string.Empty;

        if (tokens.Length != 3 || !int.TryParse(tokens[1], out int line) || !int.TryParse(tokens[2], out int column))
        {
            error = "<file> <line> <col> の形式で指定してください。";
            return false;
        }

        if (line < 1 || column < 1)
        {
            error = "行番号と列番号は1以上を指定してください。";
            return false;
        }

        query = new Query(tokens[0], line, column);
        return true;
    }

    private static string ResolveWorkspace(string? workspaceArg, List<string> positional, bool resident)
    {
        if (!string.IsNullOrWhiteSpace(workspaceArg)) return Path.GetFullPath(workspaceArg);

        if (resident) return FindUnityRoot(Environment.CurrentDirectory) ?? Environment.CurrentDirectory;

        string file = Path.GetFullPath(positional[0]);
        string? start = Path.GetDirectoryName(file);
        return (start is null ? null : FindUnityRoot(start)) ?? start ?? Environment.CurrentDirectory;
    }

    private static string? FindUnityRoot(string start)
    {
        for (string? current = Path.GetFullPath(start); current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(Path.Combine(current, "Assets")) ||
                File.Exists(Path.Combine(current, "ProjectSettings", "ProjectVersion.txt")))
            {
                return current;
            }
        }

        return null;
    }

    private static string? ResolveServerPath()
    {
        string? configured = Environment.GetEnvironmentVariable("UNITYCSHARPLSP_SERVER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        string exeName = OperatingSystem.IsWindows() ? "UnityCSharpLSP.Server.exe" : "UnityCSharpLSP.Server";
        foreach (string directory in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            string candidate = Path.Combine(directory, exeName);
            if (File.Exists(candidate)) return candidate;
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable)) return null;

        foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, exeName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[lsp-refs] {message}");
        return 2;
    }

    private readonly record struct Query(string File, int Line, int Column);
}
