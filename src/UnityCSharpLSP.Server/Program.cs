using System.CommandLine;
using UnityCSharpLSP.AssetServer.Daemon;
using UnityCSharpLSP.Protocol;

namespace UnityCSharpLSP.Server;

public static class Program
{
    private const string HelpDescription =
@"Unity C# Language Server Protocol (LSP) Server

USAGE:
  UnityCSharpLSP.Server [options]

MODES:
  (default)             Run as LSP server over stdio (JSON-RPC). Normal mode for editors/agents.
  --asset-daemon         Run as a persistent Unity asset data daemon over Named Pipe (one per project root).

OPTIONS:
  --stdio                 Use standard input/output for LSP JSON-RPC communication (default: true).
  --editor-path <path>    Explicit path to the Unity Editor installation (optional; auto-detected if omitted).
  --print-config          Print LLM agent LSP configuration snippet (opencode.json format) to stdout and exit.
  --asset-daemon          Run as a persistent Unity asset data daemon over Named Pipe.
  --project-root <path>   Unity project root directory for --asset-daemon (defaults to current working directory).
  --daemon-idle-minutes <n>  Idle shutdown timeout in minutes for --asset-daemon (0 or negative disables; default 30).

EXAMPLES:
  UnityCSharpLSP.Server --stdio
  UnityCSharpLSP.Server --print-config
  UnityCSharpLSP.Server --asset-daemon --project-root D:/MyUnityProject
  UnityCSharpLSP.Server --stdio --editor-path ""C:/Program Files/Unity/Hub/Editor/2022.3.x/Editor""

LLM / AI AGENT INTEGRATION (OpenCode, Claude Code, Cursor, etc.):
  To register this LSP server with OpenCode, add to your opencode.json:
    { ""lsp"": { ""unity-csharp-2nek"": { ""command"": [""<path/to/UnityCSharpLSP.Server.exe>"", ""--stdio""], ""extensions"": ["".cs""] } } }
  Replace <path/to/UnityCSharpLSP.Server.exe> with the actual binary path (or just the exe name if on PATH).
  Alternatively, run `UnityCSharpLSP.Server --print-config` to auto-generate this with your resolved path.

NOTES:
  - stdout is reserved for LSP JSON-RPC; diagnostics go to stderr.
  - Multi-root workspaces supported (multiple workspaceFolders in initialize).
  - Asset daemon is spawned automatically by the LSP server when needed.
  - Exit code 0 on success.";

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand(HelpDescription);

        var stdioOption = new Option<bool>(
            name: "--stdio",
            description: "Use standard input/output for LSP JSON-RPC communication",
            getDefaultValue: () => true);

        var editorPathOption = new Option<string?>(
            name: "--editor-path",
            description: "Explicit path to the Unity Editor installation (optional; auto-detected if omitted)");

        var printConfigOption = new Option<bool>(
            name: "--print-config",
            description: "Print LLM agent LSP configuration snippet (opencode.json format) to stdout and exit");

        var assetDaemonOption = new Option<bool>(
            name: "--asset-daemon",
            description: "Run as a persistent Unity asset data daemon over Named Pipe",
            getDefaultValue: () => false);

        var projectRootOption = new Option<string?>(
            name: "--project-root",
            description: "Unity project root directory for --asset-daemon (defaults to current working directory)");

        var daemonIdleMinutesOption = new Option<int>(
            name: "--daemon-idle-minutes",
            description: "Idle shutdown timeout in minutes for --asset-daemon (0 or negative disables idle timeout)",
            getDefaultValue: () => 30);

        rootCommand.AddOption(stdioOption);
        rootCommand.AddOption(editorPathOption);
        rootCommand.AddOption(printConfigOption);
        rootCommand.AddOption(assetDaemonOption);
        rootCommand.AddOption(projectRootOption);
        rootCommand.AddOption(daemonIdleMinutesOption);

        rootCommand.SetHandler(async (stdio, editorPath, printConfig, assetDaemon, projectRoot, daemonIdleMinutes) =>
        {
            if (printConfig)
            {
                var exePath = Environment.ProcessPath ?? "UnityCSharpLSP.Server.exe";
                Console.WriteLine(
$$"""
{
  "lsp": {
    "unity-csharp-2nek": {
      "command": ["{{exePath.Replace("\\", "/")}}", "--stdio"],
      "extensions": [".cs"]
    }
  }
}
""");
                return;
            }

            if (assetDaemon)
            {
                string resolvedProjectRoot = string.IsNullOrWhiteSpace(projectRoot)
                    ? Directory.GetCurrentDirectory()
                    : projectRoot;
                TimeSpan idleTimeout = daemonIdleMinutes <= 0
                    ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromMinutes(daemonIdleMinutes);

                using var cts = new CancellationTokenSource();
                ConsoleCancelEventHandler cancelHandler = (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                Console.CancelKeyPress += cancelHandler;
                try
                {
                    await AssetDaemonHost.RunAsync(resolvedProjectRoot, idleTimeout, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                }

                return;
            }

            if (!string.IsNullOrEmpty(editorPath))
            {
                Environment.SetEnvironmentVariable("UNITY_EDITOR_PATH", editorPath);
            }

            if (stdio)
            {
                // Ensure standard error is used for any diagnostic messages to keep stdout clean for LSP
                Console.SetError(Console.Error);

                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();

                using var session = new LspJsonRpcSession(input, output);
                session.Start();

                var exitCode = await session.WaitForExitAsync().ConfigureAwait(false);
                Environment.ExitCode = exitCode;
            }
        }, stdioOption, editorPathOption, printConfigOption, assetDaemonOption, projectRootOption, daemonIdleMinutesOption);

        return await rootCommand.InvokeAsync(args).ConfigureAwait(false);
    }
}
