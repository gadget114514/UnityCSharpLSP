using System.CommandLine;
using System.CommandLine.Invocation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityCSharpLSP.AssetServer.Daemon;
using UnityCSharpLSP.Mcp;
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
  --mcp                 Run as an MCP (Model Context Protocol) server over stdio, bridging tools to LSP.
  --asset-daemon        Run as a persistent Unity asset data daemon over Named Pipe (one per project root).

OPTIONS:
  --stdio                 Use standard input/output for LSP JSON-RPC communication (default: true).
  --mcp                   Run as an MCP server over stdio instead of raw LSP.
  --workspace <path>      Unity project root directory for --mcp (default: nearest ancestor with Assets/ or ProjectSettings/, else CWD).
  --settings <json>       Initial JSON settings for workspace/didChangeConfiguration in --mcp mode.
  --timeout <sec>         Per-call LSP timeout in seconds for --mcp mode (<= 0 disables; default 120).
  --editor-path <path>    Explicit path to the Unity Editor installation (optional; auto-detected if omitted).
  --print-config          Print LLM agent LSP configuration snippet (or mcpServers JSON with --mcp) to stdout and exit.
  --asset-daemon          Run as a persistent Unity asset data daemon over Named Pipe.
  --project-root <path>   Unity project root directory for --asset-daemon (defaults to current working directory).
  --daemon-idle-minutes <n>  Idle shutdown timeout in minutes for --asset-daemon (0 or negative disables; default 30).

EXAMPLES:
  UnityCSharpLSP.Server --stdio
  UnityCSharpLSP.Server --mcp --workspace D:/MyUnityProject
  UnityCSharpLSP.Server --print-config
  UnityCSharpLSP.Server --print-config --mcp --workspace D:/MyUnityProject
  UnityCSharpLSP.Server --asset-daemon --project-root D:/MyUnityProject
  UnityCSharpLSP.Server --stdio --editor-path ""C:/Program Files/Unity/Hub/Editor/2022.3.x/Editor""

LLM / AI AGENT INTEGRATION (OpenCode, Claude Code, Cursor, etc.):
  To register this LSP server with OpenCode, add to your opencode.json:
    { ""lsp"": { ""unity-csharp-2nek"": { ""command"": [""<path/to/UnityCSharpLSP.Server.exe>"", ""--stdio""], ""extensions"": ["".cs"", "".prefab"", "".unity"", "".asset"", "".mat""] } } }
  To register as an MCP server (Claude Code, etc.), run `UnityCSharpLSP.Server --print-config --mcp`
  and merge the emitted `mcpServers` JSON snippet into your MCP configuration.
  Replace <path/to/UnityCSharpLSP.Server.exe> with the actual binary path (or just the exe name if on PATH).

MCP MODE (--mcp) RULES FOR LLM AGENTS:
  - Tools mirror LSP methods 1:1: tool name = LSP method with '/' replaced by '_'
    (textDocument/definition -> textDocument_definition, workspace/executeCommand -> workspace_executeCommand).
  - Tool arguments are the LSP params object unchanged; the result is the LSP result JSON unchanged
    (null means ""nothing found"", not an error). LSP errors come back as isError: true with {code, message}.
  - Positions are LSP positions: 0-based line and 0-based UTF-16 character. Subtract 1 from editor line/column.
  - textDocument.uri accepts a file:/// URI, an absolute path, or a path relative to --workspace
    (NOT relative to the agent's current directory).
  - No didOpen/didChange needed: files are opened from disk on first use and re-synced when their
    mtime/size changes. Edit files with your own tools, then call the LSP tool again.
  - Edit-type tools (textDocument_rename, textDocument_codeAction, textDocument_formatting,
    workspace_willRenameFiles, ...) only RETURN a WorkspaceEdit / TextEdit[]; nothing is written to disk.
    Apply the edits yourself (rename edits may include .prefab/.unity/.asset YAML files).
  - ""Which prefab/scene uses this script?"", ""Is this asset still used?"", or before renaming, moving or
    deleting a MonoBehaviour / ScriptableObject / asset: unity_guid_references {""asset"": ""Assets/Scripts/Player.cs""}.
    Do not grep .meta GUIDs by hand. Narrow with includePatterns / excludePatterns ({""includePatterns"": [""Assets/Scenes/**""]}).
    What a 'guid: ...' line in a prefab/scene points to: unity_guid_definition {""guid"": ""<32 hex>""}.
  - Unity commands go through workspace_executeCommand: {""command"": ""unitycsharp.findSymbolUsagesInAssets"",
    ""arguments"": [{""symbolName"": ""PlayerController""}]}.
  - lsp_reset closes all documents and reloads the workspace (use after large branch switches).
    lsp_diagnostics_cached returns the last pushed diagnostics; prefer textDocument_diagnostic for fresh results.
  - Not exposed: semanticTokens, foldingRange, selectionRange, linkedEditingRange, inlineValue,
    onTypeFormatting, codeLens/resolve. See doc/mcp-csharp-lsp.md for the full specification.

GLOB FILTERS FOR FIND REFERENCES:
  textDocument/references accepts optional ""includePatterns"" / ""excludePatterns"" string arrays
  beside the standard params. Patterns without '/' match the file name (""*.cs"", ""Something*.cs"",
  ""*.asset""); '**' spans directories, a leading '~' matches a path substring (e.g. ""~Weapon""),
  and a leading '!' negates a pattern. The same fields are
  accepted by unitycsharp.findSymbolUsagesInAssets and unitycsharp.findGuidReferences.

NOTES:
  - stdout is reserved for LSP or MCP JSON-RPC; diagnostics go to stderr.
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

        var mcpOption = new Option<bool>(
            name: "--mcp",
            description: "Run as an MCP (Model Context Protocol) server over stdio",
            getDefaultValue: () => false);

        var workspaceOption = new Option<string?>(
            name: "--workspace",
            description: "Unity project root directory for --mcp (defaults to nearest ancestor containing Assets/ or ProjectSettings/, else CWD)");

        var settingsOption = new Option<string?>(
            name: "--settings",
            description: "Initial JSON settings passed to workspace/didChangeConfiguration in --mcp mode");

        var timeoutOption = new Option<int>(
            name: "--timeout",
            description: "Per-call LSP timeout in seconds for --mcp mode (<= 0 disables timeout)",
            getDefaultValue: () => 120);

        var editorPathOption = new Option<string?>(
            name: "--editor-path",
            description: "Explicit path to the Unity Editor installation (optional; auto-detected if omitted)");

        var printConfigOption = new Option<bool>(
            name: "--print-config",
            description: "Print LLM agent LSP configuration snippet (or mcpServers JSON when combined with --mcp) to stdout and exit");

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
        rootCommand.AddOption(mcpOption);
        rootCommand.AddOption(workspaceOption);
        rootCommand.AddOption(settingsOption);
        rootCommand.AddOption(timeoutOption);
        rootCommand.AddOption(editorPathOption);
        rootCommand.AddOption(printConfigOption);
        rootCommand.AddOption(assetDaemonOption);
        rootCommand.AddOption(projectRootOption);
        rootCommand.AddOption(daemonIdleMinutesOption);

        rootCommand.SetHandler(async (InvocationContext context) =>
        {
            var parse = context.ParseResult;
            var stdio = parse.GetValueForOption(stdioOption);
            var mcp = parse.GetValueForOption(mcpOption);
            var workspace = parse.GetValueForOption(workspaceOption);
            var settingsJson = parse.GetValueForOption(settingsOption);
            var timeoutSeconds = parse.GetValueForOption(timeoutOption);
            var editorPath = parse.GetValueForOption(editorPathOption);
            var printConfig = parse.GetValueForOption(printConfigOption);
            var assetDaemon = parse.GetValueForOption(assetDaemonOption);
            var projectRoot = parse.GetValueForOption(projectRootOption);
            var daemonIdleMinutes = parse.GetValueForOption(daemonIdleMinutesOption);

            if (printConfig)
            {
                var exePath = Environment.ProcessPath ?? "UnityCSharpLSP.Server.exe";
                if (mcp)
                {
                    var resolvedWorkspace = WorkspaceResolver.Resolve(workspace);
                    var mcpArgs = new JArray("--mcp", "--workspace", resolvedWorkspace.Replace("\\", "/"));
                    if (!string.IsNullOrWhiteSpace(settingsJson))
                    {
                        mcpArgs.Add("--settings");
                        mcpArgs.Add(settingsJson);
                    }

                    if (timeoutSeconds != 120)
                    {
                        mcpArgs.Add("--timeout");
                        mcpArgs.Add(timeoutSeconds.ToString());
                    }

                    var snippet = new JObject
                    {
                        ["mcpServers"] = new JObject
                        {
                            ["unity-csharp-lsp"] = new JObject
                            {
                                ["command"] = exePath.Replace("\\", "/"),
                                ["args"] = mcpArgs,
                            },
                        },
                    };
                    Console.WriteLine(snippet.ToString(Formatting.Indented));
                    return;
                }

                Console.WriteLine(
$$"""
{
  "lsp": {
    "unity-csharp-2nek": {
      "command": ["{{exePath.Replace("\\", "/")}}", "--stdio"],
      "extensions": [".cs", ".prefab", ".unity", ".asset", ".mat"]
    }
  }
}
""");
                return;
            }

            if (!string.IsNullOrEmpty(editorPath))
            {
                Environment.SetEnvironmentVariable("UNITY_EDITOR_PATH", editorPath);
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

            if (mcp)
            {
                var resolvedWorkspace = WorkspaceResolver.Resolve(workspace);
                if (!Directory.Exists(resolvedWorkspace))
                {
                    await Console.Error.WriteLineAsync($"Workspace directory not found: {resolvedWorkspace}").ConfigureAwait(false);
                    context.ExitCode = 2;
                    return;
                }

                JToken? initialSettings = null;
                if (!string.IsNullOrWhiteSpace(settingsJson))
                {
                    try
                    {
                        initialSettings = JToken.Parse(settingsJson);
                    }
                    catch (JsonException ex)
                    {
                        await Console.Error.WriteLineAsync($"Invalid JSON passed to --settings: {ex.Message}").ConfigureAwait(false);
                        context.ExitCode = 2;
                        return;
                    }
                }

                var callTimeout = timeoutSeconds <= 0
                    ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromSeconds(timeoutSeconds);

                using var cts = new CancellationTokenSource();
                ConsoleCancelEventHandler cancelHandler = (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                Console.CancelKeyPress += cancelHandler;
                try
                {
                    await McpBridgeServer.RunStdioAsync(resolvedWorkspace, initialSettings, callTimeout, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Clean shutdown via Ctrl+C.
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                }

                return;
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
                context.ExitCode = exitCode;
            }
        });

        return await rootCommand.InvokeAsync(args).ConfigureAwait(false);
    }
}
