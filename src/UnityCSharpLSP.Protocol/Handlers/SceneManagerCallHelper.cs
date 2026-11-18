using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UnityCSharpLSP.AssetServer.Contracts;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Syntax and semantic helpers for detecting <c>SceneManager.LoadScene</c> /
/// <c>SceneManager.LoadSceneAsync</c> scene-name string literal arguments and matching them
/// against build scenes.
/// </summary>
internal static class SceneManagerCallHelper
{
    public static bool TryGetSceneNameCompletionContext(
        SyntaxNode root,
        SemanticModel? semanticModel,
        int position,
        CancellationToken ct,
        out SyntaxToken stringToken)
    {
        var token = root.FindToken(position > 0 ? position - 1 : 0);
        if (!IsCursorInsideStringLiteralContent(token, position) ||
            !IsSceneManagerLoadSceneArgument(token, semanticModel, ct))
        {
            stringToken = default;
            return false;
        }

        stringToken = token;
        return true;
    }

    public static bool TryGetSceneNameDefinitionLiteral(
        SyntaxNode root,
        SemanticModel? semanticModel,
        int position,
        CancellationToken ct,
        out SyntaxToken stringToken,
        out string sceneName)
    {
        var token = root.FindToken(position);
        if (!token.IsKind(SyntaxKind.StringLiteralToken) && position > 0)
        {
            var previous = root.FindToken(position - 1);
            if (previous.IsKind(SyntaxKind.StringLiteralToken) && position <= previous.Span.End)
            {
                token = previous;
            }
        }

        if (IsSceneManagerLoadSceneArgument(token, semanticModel, ct) &&
            !string.IsNullOrWhiteSpace(token.ValueText))
        {
            stringToken = token;
            sceneName = token.ValueText;
            return true;
        }

        stringToken = default;
        sceneName = string.Empty;
        return false;
    }

    public static bool TryGetSceneNameArgumentLiteral(
        SyntaxToken token,
        SemanticModel? semanticModel,
        CancellationToken ct,
        out string sceneName)
    {
        if (IsSceneManagerLoadSceneArgument(token, semanticModel, ct) &&
            !string.IsNullOrWhiteSpace(token.ValueText))
        {
            sceneName = token.ValueText;
            return true;
        }

        sceneName = string.Empty;
        return false;
    }

    public static TextSpan GetStringLiteralContentSpan(SyntaxToken token)
    {
        var contentStart = GetContentStart(token);
        var contentEnd = HasClosingQuote(token.Text) ? token.Span.End - 1 : token.Span.End;
        return contentEnd > contentStart
            ? TextSpan.FromBounds(contentStart, contentEnd)
            : token.Span;
    }

    public static BuildSceneDto? FindMatchingScene(
        IReadOnlyList<BuildSceneDto> scenes,
        string sceneNameOrPath)
    {
        if (scenes.Count == 0 || string.IsNullOrWhiteSpace(sceneNameOrPath))
        {
            return null;
        }

        var normalized = sceneNameOrPath.Trim().Replace('\\', '/');

        // Prefer enabled build scenes first, then fall back to disabled entries.
        foreach (var requireEnabled in new[] { true, false })
        {
            foreach (var scene in scenes)
            {
                if (requireEnabled && !scene.Enabled)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(scene.FullPath))
                {
                    continue;
                }

                if (MatchesScene(scene, normalized))
                {
                    return scene;
                }
            }
        }

        return null;
    }

    public static bool IsSceneManagerLoadSceneArgument(
        SyntaxToken token,
        SemanticModel? semanticModel,
        CancellationToken ct)
    {
        if (!token.IsKind(SyntaxKind.StringLiteralToken))
        {
            return false;
        }

        if (token.Parent is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.StringLiteralExpression) ||
            literal.Parent is not ArgumentSyntax argument ||
            argument.Parent is not ArgumentListSyntax argumentList ||
            argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        if (argument.NameColon is not null)
        {
            if (!string.Equals(argument.NameColon.Name.Identifier.ValueText, "sceneName", StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (argumentList.Arguments.Count == 0 || argumentList.Arguments[0] != argument)
        {
            return false;
        }

        return IsSceneManagerLoadSceneInvocation(invocation, semanticModel, ct);
    }

    private static bool IsSceneManagerLoadSceneInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel? semanticModel,
        CancellationToken ct)
    {
        string? methodName;
        ExpressionSyntax? receiver = null;

        switch (invocation.Expression)
        {
            case MemberAccessExpressionSyntax memberAccess:
                methodName = memberAccess.Name.Identifier.ValueText;
                receiver = memberAccess.Expression;
                break;
            case IdentifierNameSyntax identifier:
                methodName = identifier.Identifier.ValueText;
                break;
            default:
                return false;
        }

        if (!IsLoadSceneMethodName(methodName))
        {
            return false;
        }

        if (semanticModel is not null)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation, ct);
            var method = symbolInfo.Symbol as IMethodSymbol
                ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (method is not null)
            {
                if (!IsLoadSceneMethodName(method.Name))
                {
                    return false;
                }

                var containingType = method.ContainingType;
                if (containingType is not null && containingType.TypeKind != TypeKind.Error)
                {
                    return string.Equals(containingType.Name, "SceneManager", StringComparison.Ordinal) &&
                           (containingType.ContainingNamespace is null ||
                            containingType.ContainingNamespace.IsGlobalNamespace ||
                            string.Equals(
                                containingType.ContainingNamespace.ToDisplayString(),
                                "UnityEngine.SceneManagement",
                                StringComparison.Ordinal));
                }
            }
        }

        return receiver switch
        {
            IdentifierNameSyntax id => string.Equals(id.Identifier.ValueText, "SceneManager", StringComparison.Ordinal),
            MemberAccessExpressionSyntax ma => string.Equals(ma.Name.Identifier.ValueText, "SceneManager", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsLoadSceneMethodName(string methodName) =>
        string.Equals(methodName, "LoadScene", StringComparison.Ordinal) ||
        string.Equals(methodName, "LoadSceneAsync", StringComparison.Ordinal);

    private static bool MatchesScene(BuildSceneDto scene, string normalized)
    {
        if (!string.IsNullOrWhiteSpace(scene.SceneName) &&
            string.Equals(scene.SceneName, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(scene.RelativePath))
        {
            var rel = scene.RelativePath.Replace('\\', '/');
            if (string.Equals(rel, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) &&
                rel.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rel[..^".unity".Length], normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCursorInsideStringLiteralContent(SyntaxToken token, int position)
    {
        if (!token.IsKind(SyntaxKind.StringLiteralToken))
        {
            return false;
        }

        var contentStart = GetContentStart(token);
        var contentEnd = HasClosingQuote(token.Text) ? token.Span.End - 1 : token.Span.End;
        return position >= contentStart && position <= contentEnd;
    }

    private static int GetContentStart(SyntaxToken token) =>
        token.Text.StartsWith("@\"", StringComparison.Ordinal)
            ? token.SpanStart + 2
            : token.SpanStart + 1;

    private static bool HasClosingQuote(string text)
    {
        if (text.StartsWith("@\"", StringComparison.Ordinal))
        {
            if (text.Length < 3 || text[^1] != '"')
            {
                return false;
            }

            int quoteRun = 0;
            for (int i = text.Length - 1; i >= 2 && text[i] == '"'; i--)
            {
                quoteRun++;
            }

            return (quoteRun % 2) == 1;
        }

        if (text.Length < 2 || text[^1] != '"')
        {
            return false;
        }

        int backslashCount = 0;
        for (int i = text.Length - 2; i >= 1 && text[i] == '\\'; i--)
        {
            backslashCount++;
        }

        return (backslashCount % 2) == 0;
    }
}
