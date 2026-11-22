using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Parsing;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Semantics.Features;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles <c>textDocument/completion</c> requests to provide member, symbol, and Unity message completions.
/// </summary>
public sealed class CompletionHandler
{
    private const int MaxAssetHierarchyCompletionItems = 200;
    private const int SymbolCacheCapacity = 6;
    private const long AssetCompletionCacheTtlMs = 5_000;

    private readonly WorkspaceManagerProvider _workspaceManagerProvider;
    private readonly ClientCapabilityStore _clientCapabilities;

    private readonly object _symbolCacheGate = new();
    private readonly LinkedList<SymbolCacheEntry> _symbolCacheLru = new();
    private readonly Dictionary<int, SymbolCacheEntry> _symbolCacheByKey = new();
    private int _nextSymbolCacheKey;

    private readonly object _assetCacheGate = new();
    private readonly Dictionary<string, AssetCompletionCacheEntry> _assetCompletionCache = new(StringComparer.Ordinal);

    public CompletionHandler(
        WorkspaceManagerProvider workspaceManagerProvider,
        ClientCapabilityStore clientCapabilities)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
        _clientCapabilities = clientCapabilities;
    }

    private sealed class SymbolCacheEntry
    {
        public SymbolCacheEntry(int key, Dictionary<string, ISymbol> symbols)
        {
            Key = key;
            Symbols = symbols;
        }

        public int Key { get; }
        public Dictionary<string, ISymbol> Symbols { get; }
        public LinkedListNode<SymbolCacheEntry>? Node { get; set; }
    }

    private sealed class AssetCompletionCacheEntry
    {
        public required CompletionItem[] Items { get; init; }
        public required long Timestamp { get; init; }
    }

    private sealed class SymbolCompletionData
    {
        [JsonProperty("k")]
        public int Key { get; set; }

        [JsonProperty("n")]
        public string? Name { get; set; }
    }

    private int CacheSymbols(Dictionary<string, ISymbol> symbols)
    {
        lock (_symbolCacheGate)
        {
            int key = ++_nextSymbolCacheKey;
            var entry = new SymbolCacheEntry(key, symbols);
            entry.Node = _symbolCacheLru.AddFirst(entry);
            _symbolCacheByKey[key] = entry;

            while (_symbolCacheLru.Count > SymbolCacheCapacity)
            {
                var last = _symbolCacheLru.Last!.Value;
                _symbolCacheLru.RemoveLast();
                _symbolCacheByKey.Remove(last.Key);
            }

            return key;
        }
    }

    private ISymbol? GetCachedSymbol(int key, string name)
    {
        lock (_symbolCacheGate)
        {
            if (!_symbolCacheByKey.TryGetValue(key, out var entry))
            {
                return null;
            }

            _symbolCacheLru.Remove(entry.Node!);
            _symbolCacheLru.AddFirst(entry.Node!);
            return entry.Symbols.TryGetValue(name, out var symbol) ? symbol : null;
        }
    }

    private static readonly bool Profile = Environment.GetEnvironmentVariable("UCSLSP_PROFILE") == "1";

    private static void P(string label, System.Diagnostics.Stopwatch sw, System.Diagnostics.Stopwatch total)
    {
        if (Profile) Console.Error.WriteLine($"PROF {label}: {sw.Elapsed.TotalMilliseconds:F2}ms (total {total.Elapsed.TotalMilliseconds:F2}ms)");
        sw.Restart();
    }

    [JsonRpcMethod(Methods.TextDocumentCompletionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionItem[]?> Completion(CompletionParams @params, CancellationToken ct = default)
    {
        var profTotal = System.Diagnostics.Stopwatch.StartNew();
        var prof = System.Diagnostics.Stopwatch.StartNew();
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var filePath = @params.TextDocument.Uri.LocalPath;
        var doc = ws.GetDocument(filePath);
        if (doc is null)
        {
            return UnityAssetFileHelper.IsYamlAssetFile(filePath)
                ? await AddAssetGuidCompletionsAsync(ws, filePath, @params.Position, ct).ConfigureAwait(false)
                : null;
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
        var items = new List<CompletionItem>();
        P("setup(text/tree/model/root)", prof, profTotal);

        if (SceneManagerCallHelper.TryGetSceneNameCompletionContext(root, semanticModel, position, ct, out _))
        {
            P("sceneContextCheck", prof, profTotal);
            await AddBuildSceneCompletionsAsync(ws, items, ct).ConfigureAwait(false);
            P("buildSceneCompletions", prof, profTotal);
            return items.Count > 0 ? items.ToArray() : null;
        }
        P("sceneContextCheck(false)", prof, profTotal);

        if (IsGetComponentTypeArgumentPosition(root, position))
        {
            var componentTypes = new List<CompletionItem>();
            await AddAssetComponentTypeCompletionsAsync(ws, componentTypes, ct).ConfigureAwait(false);
            P("componentTypeCompletions", prof, profTotal);
            return componentTypes.Count > 0 ? componentTypes.ToArray() : null;
        }

        var token = root.FindToken(position > 0 ? position - 1 : 0);

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
            P($"LookupSymbols(count={symbols.Length})", prof, profTotal);
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
                    Data = sym.GetDocumentationCommentId(),
                });
            }
            P($"scopeItems(count={items.Count})", prof, profTotal);

            // Check if cursor is inside a class declaration that inherits from MonoBehaviour
            var classDecl = token.Parent?.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl is not null)
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, ct);
                if (classSymbol is not null && MonoBehaviourMessages.InheritsFrom(classSymbol, "MonoBehaviour"))
                {
                    AddMonoBehaviourMessages(items);
                    P("monoBehaviourMessages", prof, profTotal);
                    await AddAssetGameObjectCompletionsAsync(ws, classSymbol, items, seen, ct).ConfigureAwait(false);
                    P($"assetGameObjectCompletions(count={items.Count})", prof, profTotal);
                }
            }
        }

        P("done", prof, profTotal);
        return items.Count > 0 ? items.ToArray() : null;
    }

    [JsonRpcMethod(Methods.TextDocumentCompletionResolveName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionItem?> Resolve(CompletionItem item, CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider();
        var documentationCommentId = GetDocumentationCommentId(item.Data);
        if (ws is null || string.IsNullOrWhiteSpace(documentationCommentId))
        {
            return item;
        }

        foreach (var project in ws.CurrentSolution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(
                documentationCommentId,
                compilation);
            if (symbol is null)
            {
                continue;
            }

            item.Detail = symbol.ToDisplayString(LspSymbolMapper.DetailFormat);
            var documentation = GetDocumentation(symbol);
            if (!string.IsNullOrWhiteSpace(documentation))
            {
                item.Documentation = new MarkupContent
                {
                    Kind = _clientCapabilities.CompletionDocumentationKind,
                    Value = documentation,
                };
            }

            break;
        }

        return item;
    }

    private static string? GetDocumentationCommentId(object? data)
    {
        return data switch
        {
            string id => id,
            JValue value => value.Value<string>(),
            JObject descriptor => descriptor["documentationCommentId"]?.Value<string>()
                ?? descriptor["id"]?.Value<string>(),
            _ => null,
        };
    }

    private static string? GetDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse($"<root>{xml}</root>");
            var fragments = document.Root?
                .Descendants()
                .Where(element => element.Name.LocalName is "summary" or "remarks")
                .Select(element => NormalizeXmlText(element.Value))
                .Where(text => !string.IsNullOrWhiteSpace(text));
            return fragments is null ? null : string.Join("\n\n", fragments);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeXmlText(string value)
    {
        var lines = value.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join(" ", lines);
    }

    private static async Task<CompletionItem[]?> AddAssetGuidCompletionsAsync(
        UnityWorkspaceManager ws,
        string filePath,
        Position position,
        CancellationToken ct)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if ((uint)position.Line >= (uint)lines.Length)
        {
            return null;
        }

        string lineText = lines[position.Line];
        int guidIndex = lineText.IndexOf("guid:", StringComparison.OrdinalIgnoreCase);
        if (guidIndex < 0)
        {
            return null;
        }

        int valueStart = guidIndex + "guid:".Length;
        while (valueStart < lineText.Length && (lineText[valueStart] == ' ' || lineText[valueStart] == '\t'))
        {
            valueStart++;
        }

        if (position.Character < valueStart || position.Character > lineText.Length)
        {
            return null;
        }

        AssetKind? kind = null;
        if (lineText.Contains("m_Script:", StringComparison.OrdinalIgnoreCase))
        {
            kind = AssetKind.CSharpScript;
        }
        else if (lineText.Contains("m_SourcePrefab:", StringComparison.OrdinalIgnoreCase))
        {
            kind = AssetKind.Prefab;
        }

        AssetEntryDto[] assets = await ws.AssetService.GetAssetsAsync(kind, ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CompletionItem>();

        foreach (AssetEntryDto asset in assets)
        {
            string label = Path.GetFileNameWithoutExtension(asset.RelativePath);
            if (string.IsNullOrWhiteSpace(label) || !seen.Add(asset.Guid))
            {
                continue;
            }

            items.Add(new CompletionItem
            {
                Label = label,
                Kind = kind == AssetKind.CSharpScript
                    ? CompletionItemKind.Class
                    : kind == AssetKind.Prefab
                        ? CompletionItemKind.Reference
                        : CompletionItemKind.File,
                Detail = asset.RelativePath,
                InsertText = asset.Guid,
                Documentation = asset.FullPath,
            });

            if (items.Count >= 500)
            {
                break;
            }
        }

        return items.Count > 0 ? items.ToArray() : null;
    }

    private static async Task AddBuildSceneCompletionsAsync(
        UnityWorkspaceManager ws,
        List<CompletionItem> items,
        CancellationToken ct)
    {
        var buildScenes = await ws.AssetService.GetBuildScenesAsync(ct).ConfigureAwait(false);
        if (buildScenes.Length == 0)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in buildScenes)
        {
            if (!scene.Enabled)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(scene.SceneName) && seen.Add(scene.SceneName))
            {
                items.Add(new CompletionItem
                {
                    Label = scene.SceneName,
                    Kind = CompletionItemKind.File,
                    Detail = $"Build Scene #{scene.BuildIndex}: {scene.RelativePath}",
                    Documentation = scene.FullPath,
                });
            }

            if (!string.IsNullOrWhiteSpace(scene.RelativePath) && seen.Add(scene.RelativePath))
            {
                items.Add(new CompletionItem
                {
                    Label = scene.RelativePath,
                    Kind = CompletionItemKind.File,
                    Detail = $"Build Scene #{scene.BuildIndex}: {scene.SceneName}",
                    Documentation = scene.FullPath,
                });
            }
        }
    }

    private static bool IsGetComponentTypeArgumentPosition(SyntaxNode root, int position)
    {
        if (position <= 0)
        {
            return false;
        }

        SyntaxToken current = root.FindToken(position - 1);
        if (current.IsKind(SyntaxKind.EndOfFileToken))
        {
            return false;
        }

        while (current.IsKind(SyntaxKind.IdentifierToken))
        {
            SyntaxToken previous = current.GetPreviousToken();
            if (previous.IsKind(SyntaxKind.DotToken))
            {
                SyntaxToken qualifier = previous.GetPreviousToken();
                if (!qualifier.IsKind(SyntaxKind.IdentifierToken))
                {
                    return false;
                }

                current = qualifier;
                continue;
            }

            current = previous;
            break;
        }

        if (!current.IsKind(SyntaxKind.LessThanToken))
        {
            return false;
        }

        SyntaxToken method = current.GetPreviousToken();
        return method.IsKind(SyntaxKind.IdentifierToken) &&
               string.Equals(method.ValueText, "GetComponent", StringComparison.Ordinal);
    }

    private static async Task AddAssetComponentTypeCompletionsAsync(
        UnityWorkspaceManager ws,
        List<CompletionItem> items,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await AddComponentTypeCompletionsFromKindAsync(ws, AssetKind.Prefab, seen, items, ct).ConfigureAwait(false);
        await AddComponentTypeCompletionsFromKindAsync(ws, AssetKind.Scene, seen, items, ct).ConfigureAwait(false);
    }

    private static async Task AddComponentTypeCompletionsFromKindAsync(
        UnityWorkspaceManager ws,
        AssetKind kind,
        HashSet<string> seen,
        List<CompletionItem> items,
        CancellationToken ct)
    {
        AssetEntryDto[] assets = await ws.AssetService.GetAssetsAsync(kind, ct).ConfigureAwait(false);

        foreach (AssetEntryDto asset in assets)
        {
            if (items.Count >= MaxAssetHierarchyCompletionItems)
            {
                break;
            }

            string assetPath = !string.IsNullOrWhiteSpace(asset.RelativePath)
                ? asset.RelativePath
                : asset.FullPath;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                continue;
            }

            AssetHierarchyNodeDto[] hierarchy = await ws.AssetService
                .GetHierarchyAsync(assetPath, maxDepth: 16, includeSerializedValues: false, ct)
                .ConfigureAwait(false);

            foreach (AssetHierarchyNodeDto root in hierarchy)
            {
                CollectPrefabComponentTypes(root, assetPath, seen, items);
                if (items.Count >= MaxAssetHierarchyCompletionItems)
                {
                    break;
                }
            }
        }
    }

    private static void CollectPrefabComponentTypes(
        AssetHierarchyNodeDto node,
        string prefabPath,
        HashSet<string> seen,
        List<CompletionItem> items)
    {
        foreach (AssetComponentSummaryDto component in node.Components)
        {
            if (!UnityComponentTypeMap.TryGetTypeName(component, out string typeName) || !seen.Add(typeName))
            {
                continue;
            }

            items.Add(new CompletionItem
            {
                Label = typeName,
                Kind = CompletionItemKind.Class,
                Detail = $"from {prefabPath}",
            });

            if (items.Count >= MaxAssetHierarchyCompletionItems)
            {
                return;
            }
        }

        foreach (AssetHierarchyNodeDto child in node.Children)
        {
            CollectPrefabComponentTypes(child, prefabPath, seen, items);
            if (items.Count >= MaxAssetHierarchyCompletionItems)
            {
                return;
            }
        }
    }

    private static async Task AddAssetGameObjectCompletionsAsync(
        UnityWorkspaceManager ws,
        INamedTypeSymbol classSymbol,
        List<CompletionItem> items,
        HashSet<string> seen,
        CancellationToken ct)
    {
        if (!UnitySymbolGuidHelper.TryGetScriptGuid(classSymbol, out string scriptGuid))
        {
            return;
        }

        var ipcProf = System.Diagnostics.Stopwatch.StartNew();
        var ipcTotal = System.Diagnostics.Stopwatch.StartNew();
        AssetReferenceResultDto references = await ws.AssetService.FindReferencesAsync(
            new AssetReferenceQueryDto
            {
                ScriptGuid = scriptGuid,
                MemberKind = AssetMemberKind.Class,
                IncludeTransitiveScenes = false,
                MaxResults = MaxAssetHierarchyCompletionItems,
            },
            ct).ConfigureAwait(false);
        P($"  asset:FindReferences(locations={references.Locations.Length})", ipcProf, ipcTotal);

        int itemLimit = items.Count + MaxAssetHierarchyCompletionItems;
        var visitedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AssetReferenceLocationDto location in references.Locations)
        {
            if (items.Count >= itemLimit)
            {
                break;
            }

            if (location.AssetKind != AssetKind.Prefab || string.IsNullOrWhiteSpace(location.RelativePath) ||
                !visitedPrefabs.Add(location.RelativePath))
            {
                continue;
            }

            AssetHierarchyNodeDto[] hierarchy = await ws.AssetService
                .GetHierarchyAsync(location.RelativePath, maxDepth: 16, includeSerializedValues: false, ct)
                .ConfigureAwait(false);

            foreach (AssetHierarchyNodeDto root in hierarchy)
            {
                CollectPrefabGameObjectNames(root, location.RelativePath, seen, items, itemLimit);
                if (items.Count >= itemLimit)
                {
                    break;
                }
            }
        }

        P("  asset:prefabHierarchies", ipcProf, ipcTotal);
        AssetEntryDto[] scenes = await ws.AssetService.GetAssetsAsync(AssetKind.Scene, ct).ConfigureAwait(false);
        P($"  asset:GetScenes(count={scenes.Length})", ipcProf, ipcTotal);
        var visitedScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AssetEntryDto scene in scenes)
        {
            if (items.Count >= itemLimit)
            {
                break;
            }

            string scenePath = !string.IsNullOrWhiteSpace(scene.RelativePath)
                ? scene.RelativePath
                : scene.FullPath;
            if (string.IsNullOrWhiteSpace(scenePath) || !visitedScenes.Add(scenePath))
            {
                continue;
            }

            AssetHierarchyNodeDto[] hierarchy = await ws.AssetService
                .GetHierarchyAsync(scenePath, maxDepth: 16, includeSerializedValues: false, ct)
                .ConfigureAwait(false);

            foreach (AssetHierarchyNodeDto root in hierarchy)
            {
                CollectPrefabGameObjectNames(root, scenePath, seen, items, itemLimit);
                if (items.Count >= itemLimit)
                {
                    break;
                }
            }
        }
        P("  asset:sceneHierarchies", ipcProf, ipcTotal);
    }

    private static void CollectPrefabGameObjectNames(
        AssetHierarchyNodeDto node,
        string prefabPath,
        HashSet<string> seen,
        List<CompletionItem> items,
        int itemLimit)
    {
        if (!string.IsNullOrWhiteSpace(node.Name) && seen.Add(node.Name))
        {
            items.Add(new CompletionItem
            {
                Label = node.Name,
                Kind = CompletionItemKind.Variable,
                Detail = $"GameObject in {prefabPath}",
            });

            if (items.Count >= itemLimit)
            {
                return;
            }
        }

        foreach (AssetHierarchyNodeDto child in node.Children)
        {
            CollectPrefabGameObjectNames(child, prefabPath, seen, items, itemLimit);
            if (items.Count >= itemLimit)
            {
                return;
            }
        }
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
                    Data = member.GetDocumentationCommentId(),
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
