using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/signatureHelp</c> for method, constructor, and attribute arguments.
/// </summary>
public sealed class SignatureHelpHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    private static readonly SymbolDisplayFormat SignatureDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeType |
                       SymbolDisplayMemberOptions.IncludeContainingType |
                       SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                          SymbolDisplayParameterOptions.IncludeName |
                          SymbolDisplayParameterOptions.IncludeDefaultValue |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly SymbolDisplayFormat ParameterDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                          SymbolDisplayParameterOptions.IncludeName |
                          SymbolDisplayParameterOptions.IncludeDefaultValue |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public SignatureHelpHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentSignatureHelpName, UseSingleObjectParameterDeserialization = true)]
    public async Task<SignatureHelp?> SignatureHelp(
        SignatureHelpParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var document = ws.GetDocument(@params.TextDocument.Uri.LocalPath);
        if (document is null)
        {
            return null;
        }

        var sourceText = await document.GetTextAsync(ct).ConfigureAwait(false);
        if (!TryGetPosition(sourceText, @params.Position.Line, @params.Position.Character, out var position))
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || semanticModel is null || root.FullSpan.IsEmpty)
        {
            return null;
        }

        var token = root.FindToken(position > 0 ? position - 1 : 0);
        if (token.Parent is null)
        {
            return null;
        }

        var argumentList = token.Parent.AncestorsAndSelf()
            .OfType<ArgumentListSyntax>()
            .FirstOrDefault(list => list.Parent is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax);

        if (argumentList?.Parent is InvocationExpressionSyntax invocation)
        {
            return CreateInvocationHelp(semanticModel, invocation, argumentList, position, ct);
        }

        if (argumentList?.Parent is BaseObjectCreationExpressionSyntax objectCreation)
        {
            return CreateObjectCreationHelp(semanticModel, objectCreation, argumentList, position, ct);
        }

        var attributeArgumentList = token.Parent.AncestorsAndSelf()
            .OfType<AttributeArgumentListSyntax>()
            .FirstOrDefault(list => list.Parent is AttributeSyntax);

        if (attributeArgumentList?.Parent is AttributeSyntax attribute)
        {
            return CreateAttributeHelp(semanticModel, attribute, attributeArgumentList, position, ct);
        }

        return null;
    }

    private static SignatureHelp? CreateInvocationHelp(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        ArgumentListSyntax argumentList,
        int position,
        CancellationToken ct)
    {
        var methods = new List<IMethodSymbol>();
        foreach (var method in semanticModel.GetMemberGroup(invocation.Expression, ct).OfType<IMethodSymbol>())
        {
            AddMethod(methods, method);
        }

        var symbolInfo = semanticModel.GetSymbolInfo(invocation, ct);
        AddMethod(methods, symbolInfo.Symbol);
        AddMethods(methods, symbolInfo.CandidateSymbols);

        if (semanticModel.GetTypeInfo(invocation.Expression, ct).Type is INamedTypeSymbol
            {
                TypeKind: TypeKind.Delegate,
            } delegateType)
        {
            AddMethods(methods, delegateType.GetMembers("Invoke"));
        }

        var activeMethod = symbolInfo.Symbol as IMethodSymbol ??
                           symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        var arguments = GetArgumentContext(argumentList.Arguments, position);
        return CreateSignatureHelp(methods, activeMethod, arguments, ct);
    }

    private static SignatureHelp? CreateObjectCreationHelp(
        SemanticModel semanticModel,
        BaseObjectCreationExpressionSyntax objectCreation,
        ArgumentListSyntax argumentList,
        int position,
        CancellationToken ct)
    {
        var methods = new List<IMethodSymbol>();
        var createdType = semanticModel.GetTypeInfo(objectCreation, ct).Type as INamedTypeSymbol;

        if (createdType is null && objectCreation is ObjectCreationExpressionSyntax explicitCreation)
        {
            createdType = semanticModel.GetTypeInfo(explicitCreation.Type, ct).Type as INamedTypeSymbol ??
                          semanticModel.GetSymbolInfo(explicitCreation.Type, ct).Symbol as INamedTypeSymbol;
        }

        if (createdType is not null)
        {
            AddMethods(methods, createdType.InstanceConstructors);
        }

        var symbolInfo = semanticModel.GetSymbolInfo(objectCreation, ct);
        AddMethod(methods, symbolInfo.Symbol);
        AddMethods(methods, symbolInfo.CandidateSymbols);

        var activeMethod = symbolInfo.Symbol as IMethodSymbol ??
                           symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        var arguments = GetArgumentContext(argumentList.Arguments, position);
        return CreateSignatureHelp(methods, activeMethod, arguments, ct);
    }

    private static SignatureHelp? CreateAttributeHelp(
        SemanticModel semanticModel,
        AttributeSyntax attribute,
        AttributeArgumentListSyntax argumentList,
        int position,
        CancellationToken ct)
    {
        var methods = new List<IMethodSymbol>();
        var attributeType = semanticModel.GetTypeInfo(attribute.Name, ct).Type as INamedTypeSymbol ??
                            semanticModel.GetSymbolInfo(attribute.Name, ct).Symbol as INamedTypeSymbol;

        if (attributeType is not null)
        {
            AddMethods(methods, attributeType.InstanceConstructors);
        }

        var symbolInfo = semanticModel.GetSymbolInfo(attribute, ct);
        AddMethod(methods, symbolInfo.Symbol);
        AddMethods(methods, symbolInfo.CandidateSymbols);

        var activeMethod = symbolInfo.Symbol as IMethodSymbol ??
                           symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        var arguments = GetArgumentContext(argumentList.Arguments, position);
        return CreateSignatureHelp(methods, activeMethod, arguments, ct);
    }

    private static SignatureHelp? CreateSignatureHelp(
        List<IMethodSymbol> methods,
        IMethodSymbol? activeMethod,
        ArgumentContext arguments,
        CancellationToken ct)
    {
        if (methods.Count == 0)
        {
            return null;
        }

        var activeSignature = activeMethod is null
            ? 0
            : methods.FindIndex(method => MethodsEqual(method, activeMethod));
        if (activeSignature < 0)
        {
            activeSignature = 0;
        }

        var signatures = methods.Select(method => CreateSignatureInformation(method, ct)).ToArray();
        var activeParameter = GetActiveParameter(
            methods[activeSignature].Parameters,
            arguments.ArgumentIndex,
            arguments.ArgumentName);

        return new SignatureHelp
        {
            Signatures = signatures,
            ActiveSignature = activeSignature,
            ActiveParameter = activeParameter,
        };
    }

    private static SignatureInformation CreateSignatureInformation(IMethodSymbol method, CancellationToken ct)
    {
        var documentation = GetDocumentation(method, ct);
        var signature = new SignatureInformation
        {
            Label = method.ToDisplayString(SignatureDisplayFormat),
            Parameters = method.Parameters.Select(parameter =>
            {
                var information = new ParameterInformation
                {
                    Label = parameter.ToDisplayString(ParameterDisplayFormat),
                };

                if (documentation.Parameters.TryGetValue(parameter.Name, out var parameterDocumentation))
                {
                    information.Documentation = parameterDocumentation;
                }

                return information;
            }).ToArray(),
        };

        if (documentation.Summary is not null)
        {
            signature.Documentation = documentation.Summary;
        }

        return signature;
    }

    private static SymbolDocumentation GetDocumentation(IMethodSymbol method, CancellationToken ct)
    {
        var xml = method.GetDocumentationCommentXml(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(xml) && !SymbolEqualityComparer.Default.Equals(method, method.OriginalDefinition))
        {
            xml = method.OriginalDefinition.GetDocumentationCommentXml(cancellationToken: ct);
        }

        if (string.IsNullOrWhiteSpace(xml))
        {
            return SymbolDocumentation.Empty;
        }

        try
        {
            var root = XDocument.Parse($"<root>{xml}</root>").Root;
            var member = root?.Descendants("member").FirstOrDefault() ?? root;
            var summary = NormalizeDocumentation(member?.Element("summary")?.Value);
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var element in member?.Elements("param") ?? Enumerable.Empty<XElement>())
            {
                var name = element.Attribute("name")?.Value;
                var text = NormalizeDocumentation(element.Value);
                if (!string.IsNullOrEmpty(name) && text is not null)
                {
                    parameters[name] = text;
                }
            }

            return new SymbolDocumentation(summary, parameters);
        }
        catch
        {
            return SymbolDocumentation.Empty;
        }
    }

    private static string? NormalizeDocumentation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var lines = value.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        var result = string.Join(" ", lines);
        return result.Length > 0 ? result : null;
    }

    private static int GetActiveParameter(
        ImmutableArray<IParameterSymbol> parameters,
        int argumentIndex,
        string? argumentName)
    {
        if (parameters.IsDefaultOrEmpty)
        {
            return 0;
        }

        if (argumentName is not null)
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                if (string.Equals(parameters[i].Name, argumentName, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        if (argumentIndex < 0)
        {
            return 0;
        }

        if (argumentIndex >= parameters.Length)
        {
            return parameters.Length - 1;
        }

        if (parameters[^1].IsParams && argumentIndex >= parameters.Length - 1)
        {
            return parameters.Length - 1;
        }

        return argumentIndex;
    }

    private static ArgumentContext GetArgumentContext<T>(
        SeparatedSyntaxList<T> arguments,
        int position)
        where T : SyntaxNode
    {
        var argumentIndex = arguments.GetSeparators()
            .Count(separator => separator.SpanStart < position);
        string? argumentName = null;

        if (argumentIndex < arguments.Count)
        {
            var argument = arguments[argumentIndex];
            argumentName = argument switch
            {
                ArgumentSyntax { NameColon: not null } regular =>
                    regular.NameColon.Name.Identifier.ValueText,
                AttributeArgumentSyntax { NameColon: not null } attribute =>
                    attribute.NameColon.Name.Identifier.ValueText,
                _ => null,
            };
        }

        return new ArgumentContext(argumentIndex, argumentName);
    }

    private static bool TryGetPosition(
        Microsoft.CodeAnalysis.Text.SourceText sourceText,
        int line,
        int character,
        out int position)
    {
        position = 0;
        if (line < 0 || line >= sourceText.Lines.Count || character < 0)
        {
            return false;
        }

        var textLine = sourceText.Lines[line];
        position = Math.Min(textLine.Start + character, textLine.End);
        return true;
    }

    private static void AddMethods(List<IMethodSymbol> methods, IEnumerable<ISymbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            AddMethod(methods, symbol);
        }
    }

    private static void AddMethod(List<IMethodSymbol> methods, ISymbol? symbol)
    {
        if (symbol is not IMethodSymbol method ||
            methods.Any(existing => SymbolEqualityComparer.Default.Equals(existing, method)))
        {
            return;
        }

        methods.Add(method);
    }

    private static bool MethodsEqual(IMethodSymbol left, IMethodSymbol right)
    {
        return SymbolEqualityComparer.Default.Equals(left, right) ||
               SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);
    }

    private sealed record ArgumentContext(int ArgumentIndex, string? ArgumentName);

    private sealed record SymbolDocumentation(
        string? Summary,
        IReadOnlyDictionary<string, string> Parameters)
    {
        public static readonly SymbolDocumentation Empty = new(
            null,
            new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
