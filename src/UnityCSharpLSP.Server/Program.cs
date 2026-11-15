using System.CommandLine;
using UnityCSharpLSP.Protocol;

namespace UnityCSharpLSP.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Unity C# Language Server Protocol (LSP) Server");

        var stdioOption = new Option<bool>(
            name: "--stdio",
            description: "Use standard input/output for LSP JSON-RPC communication",
            getDefaultValue: () => true);

        var editorPathOption = new Option<string?>(
            name: "--editor-path",
            description: "Explicit path to the Unity Editor installation");

        rootCommand.AddOption(stdioOption);
        rootCommand.AddOption(editorPathOption);

        rootCommand.SetHandler(async (stdio, editorPath) =>
        {
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
        }, stdioOption, editorPathOption);

        return await rootCommand.InvokeAsync(args).ConfigureAwait(false);
    }
}
