using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Converts Roslyn diagnostics to LSP diagnostics and publishes them to the client.
/// </summary>
public sealed class DiagnosticsHandler
{
    private readonly JsonRpc _rpc;
    private readonly UnityWorkspaceManager _workspaceManager;

    public DiagnosticsHandler(JsonRpc rpc, UnityWorkspaceManager workspaceManager)
    {
        _rpc = rpc;
        _workspaceManager = workspaceManager;
    }

    public async Task PublishDiagnosticsAsync(Uri uri, string filePath, CancellationToken ct = default)
    {
        var diagnostics = await _workspaceManager.GetDiagnosticsAsync(filePath, ct).ConfigureAwait(false);
        var lspDiagnostics = ConvertDiagnostics(diagnostics);

        await _rpc.NotifyWithParameterObjectAsync(
            Methods.TextDocumentPublishDiagnosticsName,
            new PublishDiagnosticParams
            {
                Uri = uri,
                Diagnostics = lspDiagnostics,
            }).ConfigureAwait(false);
    }

    public static Diagnostic[] ConvertDiagnostics(IEnumerable<RoslynDiagnostic> diagnostics)
    {
        return diagnostics.Select(d =>
        {
            var lineSpan = d.Location.GetLineSpan();
            var start = lineSpan.StartLinePosition;
            var end = lineSpan.EndLinePosition;

            return new Diagnostic
            {
                Range = new Microsoft.VisualStudio.LanguageServer.Protocol.Range
                {
                    Start = new Position(start.Line, start.Character),
                    End = new Position(end.Line, end.Character),
                },
                Severity = d.Severity switch
                {
                    RoslynSeverity.Error => DiagnosticSeverity.Error,
                    RoslynSeverity.Warning => DiagnosticSeverity.Warning,
                    RoslynSeverity.Info => DiagnosticSeverity.Information,
                    RoslynSeverity.Hidden => DiagnosticSeverity.Hint,
                    _ => DiagnosticSeverity.Information,
                },
                Code = d.Id,
                Source = "UnityCSharpLSP",
                Message = d.GetMessage(),
            };
        }).ToArray();
    }
}
