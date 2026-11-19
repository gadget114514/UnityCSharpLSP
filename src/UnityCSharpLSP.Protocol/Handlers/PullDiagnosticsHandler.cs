using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles pull diagnostics with result ids (LSP 3.17) while sharing filtering with push diagnostics.
/// </summary>
public sealed class PullDiagnosticsHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;
    private readonly Func<DiagnosticsHandler?> _diagnosticsHandlerProvider;

    public PullDiagnosticsHandler(
        WorkspaceManagerProvider workspaceManagerProvider,
        Func<DiagnosticsHandler?> diagnosticsHandlerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
        _diagnosticsHandlerProvider = diagnosticsHandlerProvider;
    }

    [JsonRpcMethod(DiagnosticMethods.Name, UseSingleObjectParameterDeserialization = true)]
    public async Task<DocumentDiagnosticReport?> Diagnostic(
        DocumentDiagnosticParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null)
        {
            return null;
        }

        var filePath = @params.TextDocument.Uri.LocalPath;
        var document = ws.GetDocument(filePath);
        if (document is null)
        {
            if (!UnityAssetFileHelper.IsYamlAssetFile(filePath))
            {
                return new DocumentDiagnosticReport
                {
                    Kind = "full",
                    Diagnostics = [],
                };
            }

            return await GetYamlAssetReportAsync(filePath, @params.PreviousResultId, ct).ConfigureAwait(false);
        }

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var diagnosticsHandler = _diagnosticsHandlerProvider();
        var diagnostics = diagnosticsHandler is not null
            ? await diagnosticsHandler.GetLspDiagnosticsAsync(filePath, ct).ConfigureAwait(false)
            : [];

        var resultId = ComputeResultId(
            filePath,
            ws.GetDocumentVersion(filePath),
            $"{text}\n{ComputeUnityDiagnosticKey(diagnostics)}");
        var previousResultId = ToResultId(@params.PreviousResultId);

        if (previousResultId is not null && string.Equals(previousResultId, resultId, StringComparison.Ordinal))
        {
            return new DocumentDiagnosticReport
            {
                Kind = "unchanged",
                ResultId = resultId,
            };
        }

        return new DocumentDiagnosticReport
        {
            Kind = "full",
            ResultId = resultId,
            Diagnostics = diagnostics,
        };
    }

    private async Task<DocumentDiagnosticReport> GetYamlAssetReportAsync(
        string filePath,
        object? previousResultIdObj,
        CancellationToken ct)
    {
        var diagnosticsHandler = _diagnosticsHandlerProvider();
        var diagnostics = diagnosticsHandler is not null
            ? await diagnosticsHandler.GetLspDiagnosticsAsync(filePath, ct).ConfigureAwait(false)
            : [];

        var content = await TryReadFileTextAsync(filePath, ct).ConfigureAwait(false);
        var diagKey = ComputeUnityDiagnosticKey(diagnostics);
        var resultId = ComputeResultId(filePath, version: null, $"{content}\n{diagKey}");
        var previousResultId = ToResultId(previousResultIdObj);

        if (previousResultId is not null && string.Equals(previousResultId, resultId, StringComparison.Ordinal))
        {
            return new DocumentDiagnosticReport
            {
                Kind = "unchanged",
                ResultId = resultId,
            };
        }

        return new DocumentDiagnosticReport
        {
            Kind = "full",
            ResultId = resultId,
            Diagnostics = diagnostics,
        };
    }

    private static async Task<string> TryReadFileTextAsync(string filePath, CancellationToken ct)
    {
        try
        {
            return File.Exists(filePath)
                ? await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false)
                : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string? ToResultId(object? value)
    {
        return value switch
        {
            string text => text,
            JValue jValue => jValue.Value<string>(),
            JToken token => token.Value<string>(),
            _ => null,
        };
    }

    /// <summary>
    /// Builds a stable signature of the Unity-specific diagnostics (UNITY0001 - UNITY0004).
    /// Including it in the result id makes pull diagnostics report <c>unchanged</c> only when
    /// both the document content and the Unity diagnostics are identical, while still detecting
    /// option or asset-index changes that do not touch the document text.
    /// </summary>
    internal static string ComputeUnityDiagnosticKey(IEnumerable<Diagnostic> diagnostics)
    {
        return string.Join(";", diagnostics
            .Where(d => d.Code?.Value?.ToString() is string code &&
                        code.StartsWith("UNITY", StringComparison.Ordinal))
            .Select(d => string.Join(":",
                d.Code?.Value?.ToString(),
                d.Range.Start.Line,
                d.Range.Start.Character,
                d.Range.End.Line,
                d.Range.End.Character,
                d.Severity,
                d.Message))
            .OrderBy(key => key, StringComparer.Ordinal));
    }

    internal static string ComputeResultId(string filePath, int? version, string content)
    {
        var input = $"{Path.GetFullPath(filePath)}\n{version?.ToString() ?? "-"}\n{content}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }
}
