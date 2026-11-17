using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>workspace/diagnostic</c> (LSP 3.17) by reporting diagnostics for
/// every document in every registered workspace.
/// </summary>
public sealed class WorkspaceDiagnosticsHandler
{
    private readonly Func<IReadOnlyList<UnityWorkspaceManager>> _allManagersProvider;
    private readonly Func<DiagnosticsHandler?> _diagnosticsHandlerProvider;

    public WorkspaceDiagnosticsHandler(
        Func<IReadOnlyList<UnityWorkspaceManager>> allManagersProvider,
        Func<DiagnosticsHandler?> diagnosticsHandlerProvider)
    {
        _allManagersProvider = allManagersProvider;
        _diagnosticsHandlerProvider = diagnosticsHandlerProvider;
    }

    [JsonRpcMethod(DiagnosticMethods.WorkspaceName, UseSingleObjectParameterDeserialization = true)]
    public async Task<WorkspaceDiagnosticReport> Diagnostic(
        WorkspaceDiagnosticParams @params,
        CancellationToken ct = default)
    {
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (@params.PreviousResultIds is not null)
        {
            foreach (var id in @params.PreviousResultIds)
            {
                if (!string.IsNullOrEmpty(id.Uri) && !string.IsNullOrEmpty(id.Value))
                {
                    previous[id.Uri] = id.Value;
                }
            }
        }

        var items = new List<WorkspaceDocumentDiagnosticReport>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ws in _allManagersProvider())
        {
            foreach (var project in ws.CurrentSolution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    ct.ThrowIfCancellationRequested();

                    var filePath = document.FilePath;
                    if (string.IsNullOrEmpty(filePath) ||
                        !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var uri = new Uri(filePath).AbsoluteUri;
                    if (!seen.Add(uri))
                    {
                        continue;
                    }

                    var text = await document.GetTextAsync(ct).ConfigureAwait(false);
                    var version = ws.GetDocumentVersion(filePath);
                    var resultId = PullDiagnosticsHandler.ComputeResultId(filePath, version, text.ToString());

                    if (previous.TryGetValue(uri, out var previousResultId) &&
                        string.Equals(previousResultId, resultId, StringComparison.Ordinal))
                    {
                        items.Add(new WorkspaceDocumentDiagnosticReport
                        {
                            Kind = "unchanged",
                            Uri = uri,
                            Version = version,
                            ResultId = resultId,
                        });
                        continue;
                    }

                    var diagnosticsHandler = _diagnosticsHandlerProvider();
                    var diagnostics = diagnosticsHandler is not null
                        ? await diagnosticsHandler.GetLspDiagnosticsAsync(filePath, ct).ConfigureAwait(false)
                        : Array.Empty<Microsoft.VisualStudio.LanguageServer.Protocol.Diagnostic>();

                    items.Add(new WorkspaceDocumentDiagnosticReport
                    {
                        Kind = "full",
                        Uri = uri,
                        Version = version,
                        ResultId = resultId,
                        Diagnostics = diagnostics,
                    });
                }
            }
        }

        return new WorkspaceDiagnosticReport { Items = items.ToArray() };
    }
}
