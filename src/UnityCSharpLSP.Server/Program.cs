using System.CommandLine;
using UnityCSharpLSP.Protocol;

namespace UnityCSharpLSP.Server;

public static class Program
{
    private const string HelpDescription =
@"Unity C# Language Server Protocol (LSP) Server

LLM / AI AGENT INTEGRATION (OpenCode, Claude Code, Cursor, etc.):
  To register this LSP server with OpenCode, add to your `opencode.json`:

    {
      ""lsp"": {
        ""unity-csharp"": {
          ""command"": [""UnityCSharpLSP.Server.exe"", ""--stdio""],
          ""extensions"": ["".cs""]
        }
      }
    }

  Run `UnityCSharpLSP.Server --print-config` to output this JSON snippet directly.";

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

        rootCommand.AddOption(stdioOption);
        rootCommand.AddOption(editorPathOption);
        rootCommand.AddOption(printConfigOption);

        rootCommand.SetHandler(async (stdio, editorPath, printConfig) =>
        {
            if (printConfig)
            {
                var exePath = Environment.ProcessPath ?? "UnityCSharpLSP.Server.exe";
                Console.WriteLine(
$$"""
{
  "lsp": {
    "unity-csharp": {
      "command": ["{{exePath.Replace("\\", "/")}}", "--stdio"],
      "extensions": [".cs"]
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
        }, stdioOption, editorPathOption, printConfigOption);

        return await rootCommand.InvokeAsync(args).ConfigureAwait(false);
    }
}
