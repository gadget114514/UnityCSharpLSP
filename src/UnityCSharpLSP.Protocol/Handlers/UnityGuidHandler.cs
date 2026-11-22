using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.AssetServer.Parsing;
using UnityCSharpLSP.Protocol.Models;
using UnityCSharpLSP.Workspace;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Handles the custom <c>unitylsp/guidDefinition</c> and <c>unitylsp/guidReferences</c> requests
/// (same names and result shapes as UnityCSharpFastLSP).
/// </summary>
public sealed class UnityGuidHandler
{
    private readonly Func<WorkspaceRouter?> _routerProvider;

    public UnityGuidHandler(Func<WorkspaceRouter?> routerProvider)
    {
        _routerProvider = routerProvider;
    }

    [JsonRpcMethod(UnityGuidMethods.GuidDefinitionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<GuidDefinitionResult?> GuidDefinition(GuidQueryParams? @params, CancellationToken ct = default)
    {
        var query = Resolve(@params);
        if (query is null)
        {
            return null;
        }

        var location = query.Value.Meta ?? await FindMetaByGuidAsync(query.Value.Guid, query.Value.Managers, ct).ConfigureAwait(false);
        if (location is null)
        {
            return null;
        }

        var meta = location.Value;
        var start = new Position(meta.Line, meta.Character);
        return new GuidDefinitionResult
        {
            Guid = meta.Guid,
            AssetUri = ToUri(meta.AssetPath),
            Meta = new LspLocation
            {
                Uri = new Uri(meta.MetaPath),
                Range = new LspRange
                {
                    Start = start,
                    End = new Position(meta.Line, meta.Character + meta.Guid.Length),
                },
            },
        };
    }

    [JsonRpcMethod(UnityGuidMethods.GuidReferencesName, UseSingleObjectParameterDeserialization = true)]
    public async Task<GuidReferencesResult?> GuidReferences(GuidQueryParams? @params, CancellationToken ct = default)
    {
        var query = Resolve(@params);
        if (query is null)
        {
            return null;
        }

        var (guid, knownMeta, managers) = query.Value;
        var meta = knownMeta ?? await FindMetaByGuidAsync(guid, managers, ct).ConfigureAwait(false);

        var result = new GuidReferencesResult
        {
            Guid = guid,
            AssetUri = meta is null ? null : ToUri(meta.Value.AssetPath),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manager in managers)
        {
            var (hits, truncated) = await Task.Run(
                () => UnityGuidReferenceScanner.FindReferences(
                    manager.ProjectInfo.ProjectRootPath,
                    guid,
                    meta?.MetaPath,
                    @params?.MaxResults,
                    ct),
                ct).ConfigureAwait(false);

            result.Truncated |= truncated;
            foreach (var hit in hits)
            {
                if (!seen.Add($"{hit.FullPath}:{hit.Line}:{hit.StartCharacter}"))
                {
                    continue;
                }

                result.References.Add(new GuidReference
                {
                    Uri = ToUri(hit.FullPath),
                    Range = new LspRange
                    {
                        Start = new Position(hit.Line, hit.StartCharacter),
                        End = new Position(hit.Line, hit.EndCharacter),
                    },
                    LineText = hit.LineText,
                    ClassId = hit.ClassId,
                    ObjectFileId = hit.ObjectFileId,
                    FileId = hit.FileId,
                });
            }
        }

        return result;
    }

    private readonly record struct ResolvedQuery(
        string Guid,
        MetaGuidLocation? Meta,
        IReadOnlyList<UnityWorkspaceManager> Managers);

    /// <summary>
    /// Resolves the GUID from <c>guid</c>, or from the <c>.meta</c> of <c>asset</c>, and picks the
    /// workspace(s) to search. Returns null when neither yields a GUID.
    /// </summary>
    private ResolvedQuery? Resolve(GuidQueryParams? @params)
    {
        var router = _routerProvider();
        if (router is null || @params is null)
        {
            return null;
        }

        var all = router.AllManagers;
        if (!string.IsNullOrWhiteSpace(@params.Guid))
        {
            var guid = @params.Guid.Trim();
            return UnityGuidReferenceScanner.IsGuid(guid)
                ? new ResolvedQuery(guid.ToLowerInvariant(), null, all)
                : null;
        }

        if (string.IsNullOrWhiteSpace(@params.Asset))
        {
            return null;
        }

        var path = ResolveAssetPath(@params.Asset.Trim(), all);
        if (path is null)
        {
            return null;
        }

        var metaPath = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? path : path + ".meta";
        var meta = UnityGuidReferenceScanner.TryReadMetaGuid(metaPath);
        if (meta is null)
        {
            return null;
        }

        var owner = router.GetManagerForFile(path);
        IReadOnlyList<UnityWorkspaceManager> managers = owner is null ? all : new[] { owner };
        return new ResolvedQuery(meta.Value.Guid, meta, managers);
    }

    private static async Task<MetaGuidLocation?> FindMetaByGuidAsync(
        string guid,
        IReadOnlyList<UnityWorkspaceManager> managers,
        CancellationToken ct)
    {
        foreach (var manager in managers)
        {
            var entry = await manager.AssetService.GetAssetByGuidAsync(guid, ct).ConfigureAwait(false);
            if (entry is null || string.IsNullOrWhiteSpace(entry.FullPath))
            {
                continue;
            }

            var meta = UnityGuidReferenceScanner.TryReadMetaGuid(entry.FullPath + ".meta");
            if (meta is not null && string.Equals(meta.Value.Guid, guid, StringComparison.OrdinalIgnoreCase))
            {
                return meta;
            }
        }

        return null;
    }

    /// <summary>Accepts a file URI, an absolute path, or a path relative to a workspace root.</summary>
    private static string? ResolveAssetPath(string asset, IReadOnlyList<UnityWorkspaceManager> managers)
    {
        if (asset.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(asset, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return Path.GetFullPath(uri.LocalPath);
        }

        if (Path.IsPathFullyQualified(asset))
        {
            return Path.GetFullPath(asset);
        }

        foreach (var manager in managers)
        {
            var candidate = Path.GetFullPath(Path.Combine(manager.ProjectInfo.ProjectRootPath, asset));
            if (File.Exists(candidate) || File.Exists(candidate + ".meta"))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ToUri(string fullPath) => new Uri(Path.GetFullPath(fullPath)).AbsoluteUri;
}
