using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using UnityCSharpLSP.AssetServer.Contracts;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;
using LspPosition = Microsoft.VisualStudio.LanguageServer.Protocol.Position;
using LspSymbolKind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind;
using RoslynLocation = Microsoft.CodeAnalysis.Location;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Converts Roslyn symbols and locations into their LSP equivalents.
/// </summary>
internal static class LspSymbolMapper
{
    public static readonly SymbolDisplayFormat DetailFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeType |
                       SymbolDisplayMemberOptions.IncludeContainingType |
                       SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                          SymbolDisplayParameterOptions.IncludeName,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static LspRange ToRange(FileLinePositionSpan span)
    {
        return new LspRange
        {
            Start = new LspPosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
            End = new LspPosition(span.EndLinePosition.Line, span.EndLinePosition.Character),
        };
    }

    public static LspRange ToRange(RoslynLocation location)
    {
        return ToRange(location.GetLineSpan());
    }

    public static LspLocation? ToLocation(RoslynLocation? location)
    {
        if (location is null ||
            !location.IsInSource ||
            location.SourceTree is null ||
            string.IsNullOrEmpty(location.SourceTree.FilePath))
        {
            return null;
        }

        return new LspLocation
        {
            Uri = new Uri(Path.GetFullPath(location.SourceTree.FilePath)),
            Range = ToRange(location),
        };
    }

    public static LspLocation? ToLocation(AssetReferenceLocationDto? location)
    {
        if (location is null || string.IsNullOrWhiteSpace(location.FullPath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(location.FullPath);
        var endCharacter = Math.Max(location.Character, location.EndCharacter);
        return new LspLocation
        {
            Uri = new Uri(fullPath),
            Range = new LspRange
            {
                Start = new LspPosition(location.Line, location.Character),
                End = new LspPosition(location.Line, endCharacter),
            },
        };
    }

    public static void AddUniqueLocation(List<LspLocation> target, HashSet<string> seen, LspLocation? location)
    {
        if (location is null)
        {
            return;
        }

        var key = $"{location.Uri.LocalPath}:{location.Range.Start.Line}:{location.Range.Start.Character}";
        if (seen.Add(key))
        {
            target.Add(location);
        }
    }

    public static LspSymbolKind ToSymbolKind(ISymbol symbol)
    {
        return symbol switch
        {
            INamespaceSymbol => LspSymbolKind.Namespace,
            INamedTypeSymbol named => ToSymbolKind(named),
            IMethodSymbol method => ToSymbolKind(method),
            IPropertySymbol => LspSymbolKind.Property,
            IFieldSymbol field when field.ContainingType?.TypeKind == TypeKind.Enum => LspSymbolKind.EnumMember,
            IFieldSymbol field when field.IsConst => LspSymbolKind.Constant,
            IFieldSymbol => LspSymbolKind.Field,
            IEventSymbol => LspSymbolKind.Event,
            ILocalSymbol or IParameterSymbol => LspSymbolKind.Variable,
            ITypeParameterSymbol => LspSymbolKind.TypeParameter,
            _ => LspSymbolKind.Object,
        };
    }

    private static LspSymbolKind ToSymbolKind(INamedTypeSymbol symbol)
    {
        return symbol.TypeKind switch
        {
            TypeKind.Class => LspSymbolKind.Class,
            TypeKind.Struct => LspSymbolKind.Struct,
            TypeKind.Interface => LspSymbolKind.Interface,
            TypeKind.Enum => LspSymbolKind.Enum,
            TypeKind.Delegate => LspSymbolKind.Class,
            _ => LspSymbolKind.Object,
        };
    }

    private static LspSymbolKind ToSymbolKind(IMethodSymbol symbol)
    {
        return symbol.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => LspSymbolKind.Constructor,
            MethodKind.Destructor => LspSymbolKind.Method,
            MethodKind.UserDefinedOperator or MethodKind.Conversion => LspSymbolKind.Operator,
            MethodKind.LocalFunction => LspSymbolKind.Function,
            _ => LspSymbolKind.Method,
        };
    }

    public static string GetDisplayName(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ctor
                => ctor.ContainingType?.Name ?? ctor.Name,
            IMethodSymbol { MethodKind: MethodKind.Destructor } dtor
                => "~" + (dtor.ContainingType?.Name ?? dtor.Name),
            _ => symbol.Name,
        };
    }

    public static string GetDetail(ISymbol symbol)
    {
        try
        {
            return symbol.ToDisplayString(DetailFormat);
        }
        catch
        {
            return symbol.Name;
        }
    }

    public static SymbolInformation? ToSymbolInformation(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var lspLocation = ToLocation(location);
        if (lspLocation is null)
        {
            return null;
        }

        return new SymbolInformation
        {
            Name = GetDisplayName(symbol),
            Kind = ToSymbolKind(symbol),
            Location = lspLocation,
            ContainerName = symbol.ContainingType?.Name ?? symbol.ContainingNamespace?.ToDisplayString(),
        };
    }

    public static LspRange GetSelectionRange(ISymbol symbol, SyntaxTree tree, LspRange fallback)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource &&
                location.SourceTree is not null &&
                string.Equals(location.SourceTree.FilePath, tree.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return ToRange(location);
            }
        }

        return fallback;
    }
}
