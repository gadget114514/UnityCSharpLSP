using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Stores the client capabilities negotiated during initialization for use by request handlers.
/// </summary>
public sealed class ClientCapabilityStore
{
    public ClientCapabilities Capabilities { get; private set; } = new();

    public bool SupportsHierarchicalDocumentSymbols { get; private set; }

    public bool SupportsPrepareRename { get; private set; }

    public bool SupportsSignatureHelpContext { get; private set; }

    public MarkupKind[] HoverDocumentationFormats { get; private set; } = Array.Empty<MarkupKind>();

    public MarkupKind[] CompletionDocumentationFormats { get; private set; } = Array.Empty<MarkupKind>();

    public MarkupKind HoverDocumentationKind => SelectMarkupKind(HoverDocumentationFormats);

    public MarkupKind CompletionDocumentationKind => SelectMarkupKind(CompletionDocumentationFormats);

    public void Update(ClientCapabilities? capabilities, JObject? rawCapabilities = null)
    {
        Capabilities = capabilities ?? new ClientCapabilities();
        var textDocument = Capabilities.TextDocument;

        SupportsHierarchicalDocumentSymbols =
            textDocument?.DocumentSymbol?.HierarchicalDocumentSymbolSupport == true;
        SupportsPrepareRename =
            rawCapabilities?["textDocument"]?["rename"]?["prepareSupport"]?.Value<bool>() == true;
        SupportsSignatureHelpContext = textDocument?.SignatureHelp?.ContextSupport == true;
        HoverDocumentationFormats = textDocument?.Hover?.ContentFormat ?? Array.Empty<MarkupKind>();
        CompletionDocumentationFormats =
            textDocument?.Completion?.CompletionItem?.DocumentationFormat ?? Array.Empty<MarkupKind>();
    }

    private static MarkupKind SelectMarkupKind(IReadOnlyCollection<MarkupKind> supportedFormats)
    {
        if (supportedFormats.Contains(MarkupKind.Markdown))
        {
            return MarkupKind.Markdown;
        }

        if (supportedFormats.Contains(MarkupKind.PlainText))
        {
            return MarkupKind.PlainText;
        }

        return MarkupKind.Markdown;
    }
}
