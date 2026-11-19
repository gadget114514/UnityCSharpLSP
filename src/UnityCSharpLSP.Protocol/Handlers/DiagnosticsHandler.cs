using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Semantics.Analyzers;
using UnityCSharpLSP.Workspace;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSemanticModel = Microsoft.CodeAnalysis.SemanticModel;
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
        if (doc is null && UnityAssetFileHelper.IsYamlAssetFile(filePath))
        {
            return await GetYamlAssetDiagnosticsAsync(filePath, ct).ConfigureAwait(false);
        }

        var semanticModel = doc is not null ? await doc.GetSemanticModelAsync(ct).ConfigureAwait(false) : null;
        var diagnostics = await ws.GetDiagnosticsAsync(filePath, ct).ConfigureAwait(false);

        var filteredDiagnostics = await FilterDiagnosticsAsync(
            ws,
            semanticModel,
            diagnostics,
            ct).ConfigureAwait(false);

        var allDiagnostics = new List<RoslynDiagnostic>(filteredDiagnostics);
        if (doc is not null && _settings.Current.ReportUnattachedScripts)
        {
            var unattached = await UnattachedScriptAnalyzer
                .GetDiagnosticsAsync(ws, filePath, ct)
                .ConfigureAwait(false);
            allDiagnostics.AddRange(unattached);
        }

        return ConvertDiagnostics(allDiagnostics);
    }

    /// <summary>
    /// Filters Roslyn diagnostics by applying Unity-specific suppressors such as
    /// <c>CS0649</c> for <c>[SerializeField]</c> and <c>IDE0051</c> for <c>UnityEvent</c>-referenced methods.
    /// </summary>
    public static async Task<IReadOnlyList<RoslynDiagnostic>> FilterDiagnosticsAsync(
        UnityWorkspaceManager ws,
        RoslynSemanticModel? semanticModel,
        IEnumerable<RoslynDiagnostic> diagnostics,
        CancellationToken ct = default)
    {
        var filtered = new List<RoslynDiagnostic>();
        foreach (var diagnostic in diagnostics)
        {
            if (semanticModel is not null &&
                await SerializeFieldSuppressor.ShouldSuppressAsync(diagnostic, semanticModel, ws.AssetService, ct)
                    .ConfigureAwait(false))
            {
                continue;
            }

            filtered.Add(diagnostic);
        }

        return filtered;
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

    private async Task<Diagnostic[]> GetYamlAssetDiagnosticsAsync(
        string filePath,
        CancellationToken ct)
    {
        var assetDiagnostics = await _workspaceManagerProvider(filePath)!.AssetService
            .GetAssetDiagnosticsAsync(filePath, ct).ConfigureAwait(false);
        if (assetDiagnostics.Length == 0)
        {
            return Array.Empty<Diagnostic>();
        }

        if (!_settings.Current.ReportDanglingAssetReferences)
        {
            assetDiagnostics = assetDiagnostics
                .Where(d => d.Code != "UNITY0002")
                .ToArray();
        }

        if (!_settings.Current.ReportPrefabOverrideImpact)
        {
            assetDiagnostics = assetDiagnostics
                .Where(d => d.Code != "UNITY0003")
                .ToArray();
        }

        if (assetDiagnostics.Length == 0)
        {
            return Array.Empty<Diagnostic>();
        }

        return assetDiagnostics.Select(d => new Diagnostic
        {
            Range = new Microsoft.VisualStudio.LanguageServer.Protocol.Range
            {
                Start = new Position(Math.Max(0, d.Line), Math.Max(0, d.StartCharacter)),
                End = new Position(Math.Max(0, d.Line), Math.Max(0, d.EndCharacter)),
            },
            Severity = DiagnosticSeverity.Warning,
            Code = d.Code,
            Source = "UnityCSharpLSP",
            Message = d.Message,
        }).ToArray();
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
