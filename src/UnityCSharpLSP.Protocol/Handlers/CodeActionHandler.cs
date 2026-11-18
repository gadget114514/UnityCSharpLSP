using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspDiagnostic = Microsoft.VisualStudio.LanguageServer.Protocol.Diagnostic;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/codeAction</c> and <c>codeAction/resolve</c>, offering quick fixes
/// based on Roslyn diagnostics and symbols in the current solution.
/// </summary>
public sealed class CodeActionHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public CodeActionHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentCodeActionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CodeAction[]> CodeAction(CodeActionParams @params, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null)
        {
            return Array.Empty<CodeAction>();
        }

        var filePath = @params.TextDocument.Uri.LocalPath;
        var document = ws.GetDocument(filePath);
        if (document is null)
        {
            return Array.Empty<CodeAction>();
        }

        var diagnostics = @params.Context?.Diagnostics ?? Array.Empty<LspDiagnostic>();
        var actions = new List<CodeAction>();

        foreach (var diagnostic in diagnostics)
        {
            var code = GetDiagnosticCode(diagnostic);
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            if (code is "CS0246" or "CS0103")
            {
                actions.AddRange(await CreateUsingActionsAsync(ws, document, diagnostic, ct).ConfigureAwait(false));
            }

            actions.Add(CreatePragmaAction(filePath, diagnostic, code));
        }

        actions.AddRange(await CreateFormerlySerializedAsActionsAsync(document, @params.Range, ct).ConfigureAwait(false));

        return actions.ToArray();
    }

    [JsonRpcMethod(Methods.CodeActionResolveName, UseSingleObjectParameterDeserialization = true)]
    public Task<CodeAction> Resolve(CodeAction action, CancellationToken ct = default)
    {
        // All actions carry their edits eagerly, so resolution is a no-op.
        return Task.FromResult(action);
    }

    private static async Task<IEnumerable<CodeAction>> CreateUsingActionsAsync(
        UnityWorkspaceManager ws,
        Document document,
        LspDiagnostic diagnostic,
        CancellationToken ct)
    {
        var message = diagnostic.Message ?? string.Empty;
        var match = Regex.Match(message, "'([^']+)'");
        if (!match.Success)
        {
            return Array.Empty<CodeAction>();
        }

        var typeName = match.Groups[1].Value;
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        if (root is null)
        {
            return Array.Empty<CodeAction>();
        }

        var existingUsings = new HashSet<string>(
            root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Select(usingDirective => usingDirective.Name?.ToString())
                .Where(name => name is not null)
                .Select(name => name!),
            StringComparer.Ordinal);

        var actions = new List<CodeAction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in ws.CurrentSolution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var candidate in compilation.GetSymbolsWithName(typeName, SymbolFilter.Type, ct))
            {
                if (candidate is not INamedTypeSymbol type || !seen.Add(type.ToDisplayString()))
                {
                    continue;
                }

                var containingNamespace = type.ContainingNamespace;
                if (containingNamespace is null || containingNamespace.IsGlobalNamespace)
                {
                    continue;
                }

                var namespaceName = containingNamespace.ToDisplayString();
                if (!existingUsings.Contains(namespaceName))
                {
                    var insertion = FindUsingInsertionOffset(root);
                    var newText = insertion == 0
                        ? $"using {namespaceName};\n"
                        : $"\nusing {namespaceName};";

                    actions.Add(new CodeAction
                    {
                        Title = $"Add using {namespaceName}",
                        Kind = CodeActionKind.QuickFix,
                        Diagnostics = new[] { diagnostic },
                        Edit = new WorkspaceEdit
                        {
                            Changes = new Dictionary<string, TextEdit[]>
                            {
                                [new Uri(Path.GetFullPath(document.FilePath!)).AbsoluteUri] =
                                [
                                    new TextEdit
                                    {
                                        Range = LspTextMapper.ToRange(text, insertion),
                                        NewText = newText,
                                    },
                                ],
                            },
                        },
                    });
                }

                // Fully qualifying always works, including when a using would be ambiguous.
                var qualifiedName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    .Replace("global::", string.Empty, StringComparison.Ordinal);
                actions.Add(new CodeAction
                {
                    Title = $"Use fully qualified name '{qualifiedName}'",
                    Kind = CodeActionKind.QuickFix,
                    Diagnostics = new[] { diagnostic },
                    Edit = new WorkspaceEdit
                    {
                        Changes = new Dictionary<string, TextEdit[]>
                        {
                            [new Uri(Path.GetFullPath(document.FilePath!)).AbsoluteUri] =
                            [
                                new TextEdit
                                {
                                    Range = diagnostic.Range,
                                    NewText = qualifiedName,
                                },
                            ],
                        },
                    },
                });
            }
        }

        return actions;
    }

    private static async Task<IEnumerable<CodeAction>> CreateFormerlySerializedAsActionsAsync(
        Document document,
        LspRange? requestedRange,
        CancellationToken ct)
    {
        if (requestedRange is null || string.IsNullOrEmpty(document.FilePath))
        {
            return Array.Empty<CodeAction>();
        }

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return Array.Empty<CodeAction>();
        }

        var requestedSpan = LspTextMapper.ToSpan(text, requestedRange);
        var fileUri = new Uri(Path.GetFullPath(document.FilePath)).AbsoluteUri;
        var actions = new List<CodeAction>();

        foreach (var fieldDecl in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!fieldDecl.Span.IntersectsWith(requestedSpan) ||
                fieldDecl.Declaration.Variables.Count == 0)
            {
                continue;
            }

            var matchingVars = fieldDecl.Declaration.Variables
                .Where(v => v.Span.IntersectsWith(requestedSpan))
                .ToArray();
            var candidateVars = matchingVars.Length > 0
                ? matchingVars
                : fieldDecl.Declaration.Variables.ToArray();

            foreach (var variable in candidateVars)
            {
                if (semanticModel.GetDeclaredSymbol(variable, ct) is not IFieldSymbol fieldSymbol ||
                    !UnitySymbolGuidHelper.IsUnityObjectType(fieldSymbol.ContainingType) ||
                    !UnitySymbolGuidHelper.IsSerializableField(fieldSymbol) ||
                    HasFormerlySerializedAsAttribute(fieldSymbol, fieldDecl))
                {
                    continue;
                }

                var startLine = text.Lines.GetLineFromPosition(fieldDecl.SpanStart);
                var prefixText = text.ToString(TextSpan.FromBounds(startLine.Start, fieldDecl.SpanStart));
                var isFirstOnLine = string.IsNullOrWhiteSpace(prefixText);
                var lineEnding = startLine.EndIncludingLineBreak > startLine.End
                    ? text.ToString(TextSpan.FromBounds(startLine.End, startLine.EndIncludingLineBreak))
                    : "\n";
                var attributeText = $"[UnityEngine.Serialization.FormerlySerializedAs(\"{fieldSymbol.Name}\")]";
                var newText = isFirstOnLine
                    ? $"{attributeText}{lineEnding}{prefixText}"
                    : $"{attributeText} ";

                actions.Add(new CodeAction
                {
                    Title = $"Add [FormerlySerializedAs(\"{fieldSymbol.Name}\")]",
                    Kind = CodeActionKind.Refactor,
                    Edit = new WorkspaceEdit
                    {
                        Changes = new Dictionary<string, TextEdit[]>
                        {
                            [fileUri] =
                            [
                                new TextEdit
                                {
                                    Range = LspTextMapper.ToRange(text, fieldDecl.SpanStart),
                                    NewText = newText,
                                },
                            ],
                        },
                    },
                });
            }
        }

        return actions;
    }

    private static bool HasFormerlySerializedAsAttribute(IFieldSymbol fieldSymbol, FieldDeclarationSyntax fieldDecl)
    {
        foreach (var attr in fieldSymbol.GetAttributes())
        {
            var name = attr.AttributeClass?.Name;
            if (name is "FormerlySerializedAs" or "FormerlySerializedAsAttribute")
            {
                return true;
            }
        }

        foreach (var attrList in fieldDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var rawName = attr.Name.ToString();
                var dotIndex = rawName.LastIndexOf('.');
                var simpleName = dotIndex >= 0 ? rawName[(dotIndex + 1)..] : rawName;
                if (simpleName is "FormerlySerializedAs" or "FormerlySerializedAsAttribute")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? GetDiagnosticCode(LspDiagnostic diagnostic)
    {
        var code = diagnostic.Code;
        return code?.Second ?? code?.First.ToString();
    }

    private static int FindUsingInsertionOffset(SyntaxNode root)
    {
        var usingDirectives = root.DescendantNodes().OfType<UsingDirectiveSyntax>().ToArray();
        return usingDirectives.Length > 0 ? usingDirectives[^1].Span.End : 0;
    }

    private static CodeAction CreatePragmaAction(string filePath, LspDiagnostic diagnostic, string code)
    {
        var uri = new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
        var line = Math.Max(0, diagnostic.Range.Start.Line);

        return new CodeAction
        {
            Title = $"Suppress {code} with #pragma warning disable",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new[] { diagnostic },
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<string, TextEdit[]>
                {
                    [uri] =
                    [
                        new TextEdit
                        {
                            Range = new LspRange
                            {
                                Start = new Position(line, 0),
                                End = new Position(line, 0),
                            },
                            NewText = $"#pragma warning disable {code}\n",
                        },
                        new TextEdit
                        {
                            Range = new LspRange
                            {
                                Start = new Position(line + 1, 0),
                                End = new Position(line + 1, 0),
                            },
                            NewText = $"\n#pragma warning restore {code}",
                        },
                    ],
                },
            },
        };
    }
}
