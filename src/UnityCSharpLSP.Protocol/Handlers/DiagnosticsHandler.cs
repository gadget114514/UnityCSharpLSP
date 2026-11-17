using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Semantics.Analyzers;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Converts Roslyn diagnostics to LSP diagnostics, filters suppressed Unity warnings,
/// and publishes them to the client for the workspace owning the document.
/// </summary>
public sealed class DiagnosticsHandler
{
    private readonly JsonRpc _rpc;
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;
    private readonly WorkspaceSettingsStore _settings;

    public DiagnosticsHandler(
        JsonRpc rpc,
        WorkspaceManagerProvider workspaceManagerProvider,
        WorkspaceSettingsStore settings)
    {
        _rpc = rpc;
        _workspaceManagerProvider = workspaceManagerProvider;
        _settings = settings;
    }

    public async Task PublishDiagnosticsAsync(Uri uri, string filePath, CancellationToken ct = default)
    {
        if (!_settings.Current.EnableDiagnostics)
        {
            return;
        }

        var lspDiagnostics = await GetLspDiagnosticsAsync(filePath, ct).ConfigureAwait(false);
        var ws = _workspaceManagerProvider(filePath);
        await PublishAsync(uri, lspDiagnostics, ws?.GetDocumentVersion(filePath)).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the filtered LSP diagnostics for a document, applying Unity suppressors.
    /// </summary>
    public async Task<Diagnostic[]> GetLspDiagnosticsAsync(string filePath, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(filePath);
        if (ws is null)
        {
            return Array.Empty<Diagnostic>();
        }

        var doc = ws.GetDocument(filePath);
        var semanticModel = doc is not null ? await doc.GetSemanticModelAsync(ct).ConfigureAwait(false) : null;
        var diagnostics = await ws.GetDiagnosticsAsync(filePath, ct).ConfigureAwait(false);

        // Apply Unity-specific diagnostic suppressors (e.g. CS0649 for [SerializeField])
        var filteredDiagnostics = diagnostics.Where(d =>
        {
            if (semanticModel is not null && SerializeFieldSuppressor.ShouldSuppress(d, semanticModel))
            {
                return false;
            }
            return true;
        });

        return ConvertDiagnostics(filteredDiagnostics);
    }

    public async Task ClearDiagnosticsAsync(Uri uri)
    {
        await PublishAsync(uri, Array.Empty<Diagnostic>(), version: null).ConfigureAwait(false);
    }

    private async Task PublishAsync(Uri uri, Diagnostic[] diagnostics, int? version)
    {
        await _rpc.NotifyWithParameterObjectAsync(
            Methods.TextDocumentPublishDiagnosticsName,
            new VersionedPublishDiagnosticsParams
            {
                Uri = uri,
                Diagnostics = diagnostics,
                Version = version,
            }).ConfigureAwait(false);
    }

    public static Diagnostic[] ConvertDiagnostics(IEnumerable<RoslynDiagnostic> diagnostics)
    {
        return diagnostics.Where(d => d.Location.IsInSource).Select(d =>
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
