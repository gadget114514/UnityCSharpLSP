using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;
using LspSymbolKind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>workspace/symbol</c> across every workspace folder and the custom
/// <c>workspaceSymbol/resolve</c> request for enriching the returned symbols.
/// </summary>
public sealed class WorkspaceSymbolHandler
{
    private const int PartialResultBatchSize = 100;
    private const int MaxResults = 200;

    private readonly Func<IReadOnlyList<UnityWorkspaceManager>> _allManagersProvider;
    private readonly JsonRpc _rpc;

    public WorkspaceSymbolHandler(
        Func<IReadOnlyList<UnityWorkspaceManager>> allManagersProvider,
        JsonRpc rpc)
    {
        _allManagersProvider = allManagersProvider;
        _rpc = rpc;
    }

    [JsonRpcMethod(Methods.WorkspaceSymbolName, UseSingleObjectParameterDeserialization = true)]
    public async Task<WorkspaceSymbol[]?> WorkspaceSymbol(
        WorkspaceSymbolRequestParams @params,
        CancellationToken ct = default)
    {
        var managers = _allManagersProvider();
        if (managers.Count == 0)
        {
            return null;
        }

        var query = @params.Query ?? string.Empty;
        var results = new List<WorkspaceSymbol>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ws in managers)
        {
            var workspaceRoot = ws.ProjectInfo.ProjectRootPath;
            foreach (var project in ws.CurrentSolution.Projects)
            {
                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation is null)
                {
                    continue;
                }

                var symbols = compilation.GetSymbolsWithName(
                    name => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase),
                    SymbolFilter.TypeAndMember,
                    ct);

                foreach (var symbol in symbols)
                {
                    if (symbol.IsImplicitlyDeclared)
                    {
                        continue;
                    }

                    var info = ToWorkspaceSymbol(symbol, workspaceRoot);
                    if (info is null)
                    {
                        continue;
                    }

                    var key = $"{info.Name}|{info.Location.Uri.LocalPath}|{info.Location.Range.Start.Line}|{info.Location.Range.Start.Character}";
                    if (seen.Add(key))
                    {
                        results.Add(info);
                    }
                }
            }
        }

        if (query.Length > 0 && results.Count < MaxResults)
        {
            foreach (var ws in managers)
            {
                var assets = await ws.AssetService.GetAssetsAsync(null, ct).ConfigureAwait(false);
                foreach (var asset in assets)
                {
                    if (results.Count >= MaxResults)
                    {
                        break;
                    }

                    if (asset.Kind == AssetKind.CSharpScript)
                    {
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(asset.RelativePath);
                    if (string.IsNullOrEmpty(name) ||
                        !name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(asset.FullPath))
                    {
                        continue;
                    }

                    var origin = new Position(0, 0);
                    var location = new LspLocation
                    {
                        Uri = new Uri(Path.GetFullPath(asset.FullPath)),
                        Range = new LspRange { Start = origin, End = origin },
                    };

                    var key = $"{name}|{location.Uri.LocalPath}|0|0";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    var detail = $"{asset.Kind} — {asset.RelativePath}";
                    results.Add(new WorkspaceSymbol
                    {
                        Name = name,
                        Kind = LspSymbolKind.File,
                        Location = location,
                        Detail = detail,
                        Data = new WorkspaceSymbolData
                        {
                            Detail = detail,
                            Kind = LspSymbolKind.File,
                            Workspaces = new[] { ws.ProjectInfo.ProjectRootPath },
                        },
                    });
                }

                if (results.Count >= MaxResults)
                {
                    break;
                }
            }
        }

        if (@params.PartialResultToken is { } token)
        {
            foreach (var batch in results.Chunk(PartialResultBatchSize))
            {
                await _rpc.NotifyWithParameterObjectAsync(
                    Methods.ProgressNotificationName,
                    new ProgressParams<WorkspaceSymbol[]>
                    {
                        Token = token,
                        Value = batch,
                    }).ConfigureAwait(false);
            }

            return null;
        }

        return results.Count > 0 ? results.ToArray() : null;
    }

    [JsonRpcMethod(CustomLspMethods.WorkspaceSymbolResolveName, UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceSymbol?> Resolve(WorkspaceSymbolResolveParams @params, CancellationToken ct = default)
    {
        var data = @params.Data;
        if (data is null)
        {
            return Task.FromResult<WorkspaceSymbol?>(new WorkspaceSymbol
            {
                Name = @params.Name ?? string.Empty,
                Kind = @params.Kind ?? LspSymbolKind.Object,
                Location = @params.Location!,
                ContainerName = @params.ContainerName,
            });
        }

        return Task.FromResult<WorkspaceSymbol?>(new WorkspaceSymbol
        {
            Name = @params.Name ?? string.Empty,
            Kind = data.Kind != 0 ? data.Kind : (@params.Kind ?? LspSymbolKind.Object),
            Location = @params.Location!,
            ContainerName = data.ContainerName ?? @params.ContainerName,
            Detail = data.Detail,
            Data = data,
        });
    }

    private static WorkspaceSymbol? ToWorkspaceSymbol(ISymbol symbol, string workspaceRoot)
    {
        var location = LspSymbolMapper.ToLocation(symbol.Locations.FirstOrDefault(l => l.IsInSource));
        if (location is null)
        {
            return null;
        }

        var containerName = symbol.ContainingType?.Name ?? symbol.ContainingNamespace?.ToDisplayString();
        var kind = LspSymbolMapper.ToSymbolKind(symbol);
        var isDeprecated = symbol.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute");
        return new WorkspaceSymbol
        {
            Name = LspSymbolMapper.GetDisplayName(symbol),
            Kind = kind,
            Tags = isDeprecated ? new[] { 1 } : null,
            Location = location,
            ContainerName = containerName,
            Detail = LspSymbolMapper.GetDetail(symbol),
            Data = new WorkspaceSymbolData
            {
                ContainerName = containerName,
                Detail = LspSymbolMapper.GetDetail(symbol),
                Kind = kind,
                Workspaces = new[] { workspaceRoot },
            },
        };
    }
}
