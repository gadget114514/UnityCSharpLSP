using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Implements <c>workspace/executeCommand</c> for workspace reload and information commands.
/// </summary>
public sealed class ExecuteCommandHandler
{
    public const string ReloadWorkspaceCommand = "unitycsharp.reloadWorkspace";
    public const string ShowWorkspaceInfoCommand = "unitycsharp.showWorkspaceInfo";

    public static readonly string[] Commands = { ReloadWorkspaceCommand, ShowWorkspaceInfoCommand };

    private readonly Func<WorkspaceRouter?> _routerProvider;
    private readonly JsonRpc _rpc;

    public ExecuteCommandHandler(Func<WorkspaceRouter?> routerProvider, JsonRpc rpc)
    {
        _routerProvider = routerProvider;
        _rpc = rpc;
    }

    [JsonRpcMethod(Methods.WorkspaceExecuteCommandName, UseSingleObjectParameterDeserialization = true)]
    public async Task<object?> ExecuteCommand(JObject @params, CancellationToken ct = default)
    {
        var command = @params?["command"]?.Value<string>();
        var progressToken = @params?["workDoneToken"];

        if (progressToken is not null && progressToken.Type != JTokenType.Null)
        {
            await ReportAsync(progressToken, "begin", "UnityCSharpLSP: " + command, null).ConfigureAwait(false);
        }

        try
        {
            switch (command)
            {
                case ReloadWorkspaceCommand:
                    _routerProvider()?.Reload();
                    return null;

                case ShowWorkspaceInfoCommand:
                    var router = _routerProvider();
                    var summary = router is null ? "no workspace" : router.GetSummary();
                    await _rpc.NotifyWithParameterObjectAsync(
                        Methods.WindowShowMessageName,
                        new ShowMessageParams
                        {
                            MessageType = MessageType.Info,
                            Message = "UnityCSharpLSP: " + summary,
                        }).ConfigureAwait(false);
                    return new { summary, roots = router?.RootPaths ?? Array.Empty<string>() };

                default:
                    return null;
            }
        }
        finally
        {
            if (progressToken is not null && progressToken.Type != JTokenType.Null)
            {
                await ReportAsync(progressToken, "end", null, "done").ConfigureAwait(false);
            }
        }
    }

    private Task ReportAsync(JToken token, string kind, string? title, string? message)
    {
        object value = new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["title"] = title,
            ["message"] = message,
        };

        return _rpc.NotifyWithParameterObjectAsync(
            Methods.ProgressNotificationName,
            new ProgressParams<object>
            {
                Token = token,
                Value = value,
            });
    }
}
