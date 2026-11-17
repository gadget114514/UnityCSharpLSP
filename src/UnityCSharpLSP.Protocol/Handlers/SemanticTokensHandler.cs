using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles semantic token requests using Roslyn classification.
/// </summary>
public sealed class SemanticTokensHandler
{
    private static readonly string[] TokenTypes =
    [
        SemanticTokenTypes.Namespace,
        SemanticTokenTypes.Type,
        SemanticTokenTypes.Class,
        SemanticTokenTypes.Enum,
        SemanticTokenTypes.Interface,
        SemanticTokenTypes.Struct,
        SemanticTokenTypes.TypeParameter,
        SemanticTokenTypes.Parameter,
        SemanticTokenTypes.Variable,
        SemanticTokenTypes.Property,
        SemanticTokenTypes.EnumMember,
        SemanticTokenTypes.Event,
        SemanticTokenTypes.Function,
        SemanticTokenTypes.Method,
        SemanticTokenTypes.Keyword,
        SemanticTokenTypes.Modifier,
        SemanticTokenTypes.Comment,
        SemanticTokenTypes.String,
        SemanticTokenTypes.Number,
        SemanticTokenTypes.Operator,
    ];

    private static readonly string[] TokenModifiers =
    [
        SemanticTokenModifiers.Declaration,
        SemanticTokenModifiers.Definition,
        SemanticTokenModifiers.Static,
        SemanticTokenModifiers.Readonly,
    ];

    private static readonly HashSet<string> ModifierKeywords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "readonly", "const", "virtual",
        "override", "abstract", "sealed", "async", "extern", "unsafe", "new", "partial", "volatile",
    };

    private static readonly HashSet<string> OperatorTexts = new(StringComparer.Ordinal)
    {
        "+", "-", "*", "/", "%", "=", "==", "!=", "<", ">", "<=", ">=", "&&", "||", "!", "&", "|", "^", "~",
        "++", "--", "+=", "-=", "*=", "/=", "%=", "<<", ">>", "<<=", ">>=", "??", "=>", "?", ":",
    };

    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public SemanticTokensHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    public static LspSemanticTokensOptions CreateOptions() => new()
    {
        Legend = new LspSemanticTokensLegend
        {
            TokenTypes = TokenTypes,
            TokenModifiers = TokenModifiers,
        },
        Full = true,
        Range = true,
    };

    [JsonRpcMethod(Methods.TextDocumentSemanticTokensFullName, UseSingleObjectParameterDeserialization = true)]
    public Task<SemanticTokens?> Full(SemanticTokensParams @params, CancellationToken ct = default)
    {
        return GetTokensAsync(@params.TextDocument, range: null, ct);
    }

    [JsonRpcMethod(Methods.TextDocumentSemanticTokensRangeName, UseSingleObjectParameterDeserialization = true)]
    public Task<SemanticTokens?> Range(SemanticTokensRangeParams @params, CancellationToken ct = default)
    {
        return GetTokensAsync(@params.TextDocument, @params.Range, ct);
    }

    private async Task<SemanticTokens?> GetTokensAsync(
        TextDocumentIdentifier textDocument,
        Microsoft.VisualStudio.LanguageServer.Protocol.Range? range,
        CancellationToken ct)
    {
        var ws = _workspaceManagerProvider(textDocument?.Uri?.LocalPath);
        if (ws is null || textDocument?.Uri is null)
        {
            return null;
        }

        var document = ws.GetDocument(textDocument.Uri.LocalPath);
        if (document is null)
        {
            return null;
        }

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var span = range is null
            ? new TextSpan(0, text.Length)
            : LspTextMapper.ToSpan(text, range);
        if (span.Start >= text.Length || span.IsEmpty)
        {
            return new SemanticTokens { Data = [] };
        }

        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);

        IEnumerable<ClassifiedSpan> classified;
        try
        {
            classified = await Classifier.GetClassifiedSpansAsync(document, span, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Minimal (adhoc) workspaces do not expose the embedded-language classification
            // service; fall back to classifying C# syntax and symbols directly.
            classified = root is not null
                ? ClassifyManually(root, semanticModel, span, ct)
                : [];
        }

        var data = new List<int>();
        var previousLine = 0;
        var previousCharacter = 0;
        var lastEnd = span.Start;

        foreach (var item in classified
                     .Where(item => !item.TextSpan.IsEmpty)
                     .OrderBy(item => item.TextSpan.Start)
                     .ThenByDescending(item => item.TextSpan.Length))
        {
            if (item.TextSpan.Start < lastEnd || item.TextSpan.End > text.Length)
            {
                continue;
            }

            var tokenType = MapTokenType(item.ClassificationType);
            if (tokenType < 0)
            {
                continue;
            }

            var position = text.Lines.GetLinePosition(item.TextSpan.Start);
            var deltaLine = position.Line - previousLine;
            var deltaStart = deltaLine == 0 ? position.Character - previousCharacter : position.Character;

            data.Add(deltaLine);
            data.Add(deltaStart);
            data.Add(item.TextSpan.Length);
            data.Add(tokenType);
            data.Add(GetModifiers(item, root, semanticModel, ct));

            previousLine = position.Line;
            previousCharacter = position.Character;
            lastEnd = item.TextSpan.End;
        }

        return new SemanticTokens { Data = data.ToArray() };
    }

    private static int GetModifiers(
        ClassifiedSpan item,
        SyntaxNode? root,
        SemanticModel? semanticModel,
        CancellationToken ct)
    {
        if (root is null || semanticModel is null)
        {
            return 0;
        }

        var node = root.FindNode(item.TextSpan, getInnermostNodeForTie: true);
        var declared = semanticModel.GetDeclaredSymbol(node, ct);
        if (declared is null)
        {
            return 0;
        }

        var modifiers = 1 << 0; // declaration
        if (declared.IsStatic)
        {
            modifiers |= 1 << 2;
        }
        if (declared is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true })
        {
            modifiers |= 1 << 3;
        }

        return modifiers;
    }

    private static IEnumerable<ClassifiedSpan> ClassifyManually(
        SyntaxNode root,
        SemanticModel? semanticModel,
        TextSpan span,
        CancellationToken ct)
    {
        var result = new List<ClassifiedSpan>();

        foreach (var trivia in root.DescendantTrivia(span, descendIntoTrivia: true))
        {
            if ((trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                 trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) &&
                !trivia.Span.IsEmpty &&
                trivia.Span.IntersectsWith(span))
            {
                result.Add(new ClassifiedSpan(trivia.Span, ClassificationTypeNames.Comment));
            }
        }

        foreach (var token in root.DescendantTokens(span))
        {
            ct.ThrowIfCancellationRequested();
            if (token.Span.IsEmpty || !token.Span.IntersectsWith(span))
            {
                continue;
            }

            var classification = ClassifyToken(token, semanticModel, ct);
            if (classification is not null)
            {
                result.Add(new ClassifiedSpan(token.Span, classification));
            }
        }

        return result;
    }

    private static string? ClassifyToken(
        SyntaxToken token,
        SemanticModel? semanticModel,
        CancellationToken ct)
    {
        if (token.IsKind(SyntaxKind.StringLiteralToken) ||
            token.IsKind(SyntaxKind.InterpolatedStringTextToken))
        {
            return ClassificationTypeNames.StringLiteral;
        }

        if (token.IsKind(SyntaxKind.NumericLiteralToken))
        {
            return ClassificationTypeNames.NumericLiteral;
        }

        if (SyntaxFacts.IsKeywordKind(token.Kind()))
        {
            return ModifierKeywords.Contains(token.ValueText) ? "modifier" : ClassificationTypeNames.Keyword;
        }

        if (OperatorTexts.Contains(token.Text))
        {
            return ClassificationTypeNames.Operator;
        }

        if (!token.IsKind(SyntaxKind.IdentifierToken) || semanticModel is null || token.Parent is null)
        {
            return null;
        }

        var symbol = semanticModel.GetDeclaredSymbol(token.Parent, ct)
                     ?? semanticModel.GetSymbolInfo(token.Parent, ct).Symbol;

        return symbol switch
        {
            INamespaceSymbol => ClassificationTypeNames.NamespaceName,
            INamedTypeSymbol named => named.TypeKind switch
            {
                TypeKind.Struct => ClassificationTypeNames.StructName,
                TypeKind.Interface => ClassificationTypeNames.InterfaceName,
                TypeKind.Enum => ClassificationTypeNames.EnumName,
                TypeKind.Delegate => ClassificationTypeNames.DelegateName,
                _ => ClassificationTypeNames.ClassName,
            },
            ITypeParameterSymbol => ClassificationTypeNames.TypeParameterName,
            IParameterSymbol => ClassificationTypeNames.ParameterName,
            IFieldSymbol field when field.ContainingType?.TypeKind == TypeKind.Enum => ClassificationTypeNames.EnumMemberName,
            ILocalSymbol or IFieldSymbol => ClassificationTypeNames.LocalName,
            IPropertySymbol => ClassificationTypeNames.PropertyName,
            IEventSymbol => ClassificationTypeNames.EventName,
            IMethodSymbol method when method.MethodKind == MethodKind.LocalFunction => "function",
            IMethodSymbol => ClassificationTypeNames.MethodName,
            _ => null,
        };
    }

    private static int MapTokenType(string classificationType)
    {
        if (classificationType is ClassificationTypeNames.WhiteSpace or
            ClassificationTypeNames.Text or
            ClassificationTypeNames.Punctuation)
        {
            return -1;
        }

        if (classificationType == ClassificationTypeNames.NamespaceName) return Index(SemanticTokenTypes.Namespace);
        if (classificationType == ClassificationTypeNames.ClassName) return Index(SemanticTokenTypes.Class);
        if (classificationType == ClassificationTypeNames.StructName) return Index(SemanticTokenTypes.Struct);
        if (classificationType == ClassificationTypeNames.EnumName) return Index(SemanticTokenTypes.Enum);
        if (classificationType == ClassificationTypeNames.InterfaceName) return Index(SemanticTokenTypes.Interface);
        if (classificationType == ClassificationTypeNames.TypeParameterName) return Index(SemanticTokenTypes.TypeParameter);
        if (classificationType == ClassificationTypeNames.ParameterName) return Index(SemanticTokenTypes.Parameter);
        if (classificationType is ClassificationTypeNames.LocalName or
            ClassificationTypeNames.FieldName or
            ClassificationTypeNames.ConstantName) return Index(SemanticTokenTypes.Variable);
        if (classificationType == ClassificationTypeNames.PropertyName) return Index(SemanticTokenTypes.Property);
        if (classificationType == ClassificationTypeNames.EnumMemberName) return Index(SemanticTokenTypes.EnumMember);
        if (classificationType == ClassificationTypeNames.EventName) return Index(SemanticTokenTypes.Event);
        if (classificationType == ClassificationTypeNames.DelegateName) return Index(SemanticTokenTypes.Type);
        if (classificationType is ClassificationTypeNames.MethodName or
            ClassificationTypeNames.ExtensionMethodName) return Index(SemanticTokenTypes.Method);
        if (classificationType is ClassificationTypeNames.Keyword or
            ClassificationTypeNames.ControlKeyword or
            ClassificationTypeNames.PreprocessorKeyword) return Index(SemanticTokenTypes.Keyword);
        if (classificationType == "modifier") return Index(SemanticTokenTypes.Modifier);
        if (classificationType == "function") return Index(SemanticTokenTypes.Function);
        if (classificationType == ClassificationTypeNames.Comment) return Index(SemanticTokenTypes.Comment);
        if (classificationType == ClassificationTypeNames.XmlDocCommentComment ||
            classificationType == ClassificationTypeNames.XmlDocCommentText) return Index(SemanticTokenTypes.Comment);
        if (classificationType is ClassificationTypeNames.StringLiteral or
            ClassificationTypeNames.VerbatimStringLiteral) return Index(SemanticTokenTypes.String);
        if (classificationType == ClassificationTypeNames.NumericLiteral) return Index(SemanticTokenTypes.Number);
        if (classificationType == ClassificationTypeNames.Operator) return Index(SemanticTokenTypes.Operator);

        return -1;
    }

    private static int Index(string tokenType) => Array.IndexOf(TokenTypes, tokenType);
}
