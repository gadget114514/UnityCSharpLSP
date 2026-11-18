using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Implements <c>workspace/executeCommand</c> for workspace reload, workspace info,
/// and Unity asset/hierarchy commands.
/// </summary>
public sealed class ExecuteCommandHandler
{
    public const string ReloadWorkspaceCommand = "unitycsharp.reloadWorkspace";
    public const string ShowWorkspaceInfoCommand = "unitycsharp.showWorkspaceInfo";
    public const string GetSceneOrPrefabHierarchyCommand = "unitycsharp.getSceneOrPrefabHierarchy";
    public const string FindSymbolUsagesInAssetsCommand = "unitycsharp.findSymbolUsagesInAssets";
    public const string AssetServerStatusCommand = "unitycsharp.assetServerStatus";

    public static readonly string[] Commands =
    {
        ReloadWorkspaceCommand,
        ShowWorkspaceInfoCommand,
        GetSceneOrPrefabHierarchyCommand,
        FindSymbolUsagesInAssetsCommand,
        AssetServerStatusCommand,
    };

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
                {
                    var reloadRouter = _routerProvider();
                    if (reloadRouter is not null)
                    {
                        reloadRouter.Reload();
                        foreach (var manager in reloadRouter.AllManagers)
                        {
                            await manager.AssetService.RescanAsync(ct).ConfigureAwait(false);
                        }
                    }

                    return null;
                }

                case ShowWorkspaceInfoCommand:
                {
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
                }

                case GetSceneOrPrefabHierarchyCommand:
                    return await HandleGetSceneOrPrefabHierarchyAsync(@params, ct).ConfigureAwait(false);

                case FindSymbolUsagesInAssetsCommand:
                    return await HandleFindSymbolUsagesInAssetsAsync(@params, ct).ConfigureAwait(false);

                case AssetServerStatusCommand:
                    return await HandleAssetServerStatusAsync(@params, ct).ConfigureAwait(false);

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

    private async Task<AssetHierarchyNodeDto[]> HandleGetSceneOrPrefabHierarchyAsync(
        JObject? @params,
        CancellationToken ct)
    {
        var router = _routerProvider();
        var arg0 = GetFirstArgumentObject(@params);
        var path = NormalizePathOrUri(arg0?["path"]?.Value<string>());
        if (router is null || string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<AssetHierarchyNodeDto>();
        }

        var ws = ResolveManager(router, path);
        if (ws is null)
        {
            return Array.Empty<AssetHierarchyNodeDto>();
        }

        var maxDepth = arg0?["maxDepth"]?.Value<int?>() ?? 16;
        var includeSerializedValues = arg0?["includeSerializedValues"]?.Value<bool?>() ?? true;

        return await ws.AssetService
            .GetHierarchyAsync(path, maxDepth, includeSerializedValues, ct)
            .ConfigureAwait(false);
    }

    private async Task<AssetReferenceResultDto> HandleFindSymbolUsagesInAssetsAsync(
        JObject? @params,
        CancellationToken ct)
    {
        var router = _routerProvider();
        if (router is null)
        {
            return new AssetReferenceResultDto();
        }

        var arg0 = GetFirstArgumentObject(@params);
        var pathHint = NormalizePathOrUri(
            arg0?["path"]?.Value<string>() ?? arg0?["projectRoot"]?.Value<string>());
        var ws = ResolveManager(router, pathHint);
        if (ws is null)
        {
            return new AssetReferenceResultDto();
        }

        var scriptGuid = arg0?["scriptGuid"]?.Value<string>();
        var symbolName = arg0?["symbolName"]?.Value<string>();

        if (string.IsNullOrWhiteSpace(scriptGuid) && !string.IsNullOrWhiteSpace(symbolName))
        {
            scriptGuid = await ResolveScriptGuidBySymbolNameAsync(ws, router, symbolName, ct)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(scriptGuid))
        {
            return new AssetReferenceResultDto();
        }

        var memberKind = ParseMemberKind(arg0?["memberKind"]);
        var memberName = arg0?["memberName"]?.Value<string>();
        var includeTransitiveScenes = arg0?["includeTransitiveScenes"]?.Value<bool?>() ?? true;
        var maxResults = arg0?["maxResults"]?.Value<int?>() ?? 500;

        var query = new AssetReferenceQueryDto
        {
            ScriptGuid = scriptGuid,
            MemberKind = memberKind,
            MemberName = memberName,
            IncludeTransitiveScenes = includeTransitiveScenes,
            MaxResults = maxResults,
        };

        return await ws.AssetService.FindReferencesAsync(query, ct).ConfigureAwait(false);
    }

    private async Task<DaemonStatusResponse?> HandleAssetServerStatusAsync(
        JObject? @params,
        CancellationToken ct)
    {
        var router = _routerProvider();
        if (router is null)
        {
            return null;
        }

        var arg0 = GetFirstArgumentObject(@params);
        var pathHint = NormalizePathOrUri(
            arg0?["path"]?.Value<string>() ?? arg0?["projectRoot"]?.Value<string>());
        var ws = ResolveManager(router, pathHint);
        if (ws is null)
        {
            return null;
        }

        return await ws.AssetService.GetStatusAsync(ct).ConfigureAwait(false);
    }

    private static JObject? GetFirstArgumentObject(JObject? @params)
    {
        var args = @params?["arguments"] as JArray;
        return args?.FirstOrDefault() as JObject;
    }

    private static string? NormalizePathOrUri(string? pathOrUri)
    {
        if (string.IsNullOrWhiteSpace(pathOrUri))
        {
            return null;
        }

        if (Uri.TryCreate(pathOrUri, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        return pathOrUri;
    }

    private static UnityWorkspaceManager? ResolveManager(WorkspaceRouter router, string? pathOrRoot)
    {
        if (string.IsNullOrWhiteSpace(pathOrRoot))
        {
            return router.DefaultManager;
        }

        if (Path.IsPathFullyQualified(pathOrRoot))
        {
            return router.GetManagerForFile(pathOrRoot);
        }

        foreach (var manager in router.AllManagers)
        {
            var candidate = Path.Combine(manager.ProjectInfo.ProjectRootPath, pathOrRoot);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return manager;
            }
        }

        return router.DefaultManager;
    }

    private static AssetMemberKind ParseMemberKind(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
        {
            return AssetMemberKind.Class;
        }

        if (token.Type == JTokenType.Integer)
        {
            var intVal = token.Value<int>();
            return Enum.IsDefined(typeof(AssetMemberKind), intVal)
                ? (AssetMemberKind)intVal
                : AssetMemberKind.Class;
        }

        var strVal = token.Value<string>();
        return !string.IsNullOrWhiteSpace(strVal) &&
               Enum.TryParse<AssetMemberKind>(strVal, ignoreCase: true, out var parsed)
            ? parsed
            : AssetMemberKind.Class;
    }

    private static async Task<string?> ResolveScriptGuidBySymbolNameAsync(
        UnityWorkspaceManager primary,
        WorkspaceRouter router,
        string symbolName,
        CancellationToken ct)
    {
        var managers = new List<UnityWorkspaceManager> { primary };
        foreach (var other in router.AllManagers)
        {
            if (!ReferenceEquals(other, primary))
            {
                managers.Add(other);
            }
        }

        var shortName = symbolName.Contains('.')
            ? symbolName[(symbolName.LastIndexOf('.') + 1)..]
            : symbolName;

        foreach (var manager in managers)
        {
            foreach (var project in manager.CurrentSolution.Projects)
            {
                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation is null)
                {
                    continue;
                }

                var candidates = compilation.GetSymbolsWithName(
                    name => string.Equals(name, shortName, StringComparison.Ordinal),
                    SymbolFilter.Type,
                    ct);

                foreach (var candidate in candidates.OfType<INamedTypeSymbol>())
                {
                    var fullName = candidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        .Replace("global::", string.Empty, StringComparison.Ordinal);

                    if (!string.Equals(candidate.Name, symbolName, StringComparison.Ordinal) &&
                        !string.Equals(fullName, symbolName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (UnitySymbolGuidHelper.IsUnityObjectType(candidate) &&
                        UnitySymbolGuidHelper.TryGetScriptGuid(candidate, out var guid))
                    {
                        return guid;
                    }
                }
            }
        }

        return null;
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
