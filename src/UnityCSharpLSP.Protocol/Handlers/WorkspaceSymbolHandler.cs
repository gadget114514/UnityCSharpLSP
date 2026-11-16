using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>workspace/symbol</c> requests to search declarations across all assemblies.
/// </summary>
public sealed class WorkspaceSymbolHandler
{
    private const int MaxResults = 200;

    private readonly Func<UnityWorkspaceManager?> _workspaceManagerProvider;

    public WorkspaceSymbolHandler(Func<UnityWorkspaceManager?> workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.WorkspaceSymbolName, UseSingleObjectParameterDeserialization = true)]
    public async Task<SymbolInformation[]?> WorkspaceSymbol(WorkspaceSymbolParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null)
        {
            return null;
        }

        var query = @params.Query ?? string.Empty;
        var results = new List<SymbolInformation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in ws.CurrentSolution.Projects)
        {
            if (results.Count >= MaxResults)
            {
                break;
            }

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
                if (results.Count >= MaxResults)
                {
                    break;
                }

                if (symbol.IsImplicitlyDeclared)
                {
                    continue;
                }

                var info = LspSymbolMapper.ToSymbolInformation(symbol);
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

        return results.Count > 0 ? results.ToArray() : null;
    }
}
