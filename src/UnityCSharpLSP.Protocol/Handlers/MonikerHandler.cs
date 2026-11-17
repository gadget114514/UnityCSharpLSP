using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspMoniker = UnityCSharpLSP.Protocol.Models.Moniker;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/moniker</c> requests for C# symbols.
/// </summary>
public sealed class MonikerHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public MonikerHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(MonikerMethods.TextDocumentMonikerName, UseSingleObjectParameterDeserialization = true)]
    public async Task<LspMoniker[]?> GetMonikers(
        TextDocumentPositionParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var (symbol, _) = await ws.GetSymbolAtPositionAsync(
            @params.TextDocument.Uri.LocalPath,
            @params.Position.Line,
            @params.Position.Character,
            ct).ConfigureAwait(false);

        if (symbol is null)
        {
            return null;
        }

        var identifier = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(identifier))
        {
            identifier = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        return new[]
        {
            new LspMoniker
            {
                Scheme = "csharp",
                Identifier = identifier,
                Unique = "default",
            },
        };
    }
}
