using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Semantics.Features;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/completion</c> requests to provide member, symbol, and Unity message completions.
/// </summary>
public sealed class CompletionHandler
{
    private readonly Func<UnityWorkspaceManager?> _workspaceManagerProvider;

    public CompletionHandler(Func<UnityWorkspaceManager?> workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentCompletionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionItem[]?> Completion(CompletionParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider();
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var filePath = @params.TextDocument.Uri.LocalPath;
        var doc = ws.GetDocument(filePath);
        if (doc is null)
        {
            return null;
        }

        var sourceText = await doc.GetTextAsync(ct).ConfigureAwait(false);
        var line = @params.Position.Line;
        var character = @params.Position.Character;

        if (line < 0 || line >= sourceText.Lines.Count)
        {
            return null;
        }

        var textLine = sourceText.Lines[line];
        var position = Math.Min(textLine.Start + character, textLine.End);

        var syntaxTree = await doc.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        if (syntaxTree is null)
        {
            return null;
        }

        var semanticModel = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return null;
        }

        var root = await syntaxTree.GetRootAsync(ct).ConfigureAwait(false);
        var token = root.FindToken(position > 0 ? position - 1 : 0);

        var items = new List<CompletionItem>();

        // Check for member access (e.g. "foo.")
        if (token.IsKind(SyntaxKind.DotToken) && token.Parent is MemberAccessExpressionSyntax memberAccess)
        {
            var targetType = semanticModel.GetTypeInfo(memberAccess.Expression, ct).Type;
            if (targetType is not null)
            {
                AddMembersOf(targetType, items);
            }
        }
        else if (token.Parent is MemberAccessExpressionSyntax ma && ma.Name == token.Parent)
        {
            // Cursor is typing the member name after the dot: "foo.b|"
            var targetType = semanticModel.GetTypeInfo(ma.Expression, ct).Type;
            if (targetType is not null)
            {
                AddMembersOf(targetType, items);
            }
        }
        else
        {
            // Normal scope completion: local variables, parameters, members, etc.
            var symbols = semanticModel.LookupSymbols(position);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var sym in symbols)
            {
                if (sym.IsImplicitlyDeclared || !seen.Add(sym.Name))
                {
                    continue;
                }

                items.Add(new CompletionItem
                {
                    Label = sym.Name,
                    Kind = MapSymbolKind(sym),
                    Detail = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                });
            }

            // Check if cursor is inside a class declaration that inherits from MonoBehaviour
            var classDecl = token.Parent?.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl is not null)
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, ct);
                if (classSymbol is not null && MonoBehaviourMessages.InheritsFrom(classSymbol, "MonoBehaviour"))
                {
                    AddMonoBehaviourMessages(items);
                }
            }
        }

        return items.Count > 0 ? items.ToArray() : null;
    }

    private static void AddMembersOf(ITypeSymbol type, List<CompletionItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = type;

        while (current is not null)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                if (!seen.Add(member.Name))
                {
                    continue;
                }

                items.Add(new CompletionItem
                {
                    Label = member.Name,
                    Kind = MapSymbolKind(member),
                    Detail = member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                });
            }

            current = current.BaseType;
        }
    }

    private static void AddMonoBehaviourMessages(List<CompletionItem> items)
    {
        foreach (var msg in MonoBehaviourMessages.All)
        {
            items.Add(new CompletionItem
            {
                Label = msg.Name,
                Kind = CompletionItemKind.Method,
                Detail = $"Unity Message: {msg.Signature}",
                Documentation = msg.Description,
                InsertText = msg.InsertSnippet,
                InsertTextFormat = InsertTextFormat.Snippet,
            });
        }
    }

    private static CompletionItemKind MapSymbolKind(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol => CompletionItemKind.Method,
            IPropertySymbol => CompletionItemKind.Property,
            IFieldSymbol => CompletionItemKind.Field,
            INamedTypeSymbol n when n.TypeKind == TypeKind.Interface => CompletionItemKind.Interface,
            INamedTypeSymbol n when n.TypeKind == TypeKind.Enum => CompletionItemKind.Enum,
            INamedTypeSymbol n when n.TypeKind == TypeKind.Struct => CompletionItemKind.Struct,
            INamedTypeSymbol => CompletionItemKind.Class,
            ILocalSymbol or IParameterSymbol => CompletionItemKind.Variable,
            IEventSymbol => CompletionItemKind.Event,
            _ => CompletionItemKind.Text,
        };
    }
}
