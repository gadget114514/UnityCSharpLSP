using UnityCSharpLSP.AssetServer.Contracts;

namespace UnityCSharpLSP.AssetServer.Storage;

/// <summary>
/// Immutable point-in-time snapshot of the asset dependency graph for lock-free reads.
/// </summary>
public sealed class AssetGraphSnapshot
{
    public static readonly AssetGraphSnapshot Empty = new()
    {
        AssetsById = new Dictionary<int, AssetNode>(),
        AssetIdByGuid = new Dictionary<AssetGuid, int>(),
        AssetIdByRelPath = new Dictionary<string, int>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal),
        ScriptToDirectAssetIds = new Dictionary<AssetGuid, int[]>(),
        PrefabGuidToInstantiatingAssetIds = new Dictionary<AssetGuid, int[]>(),
    };

    public required IReadOnlyDictionary<int, AssetNode> AssetsById { get; init; }
    public required IReadOnlyDictionary<AssetGuid, int> AssetIdByGuid { get; init; }
    public required IReadOnlyDictionary<string, int> AssetIdByRelPath { get; init; }
    public required IReadOnlyDictionary<AssetGuid, int[]> ScriptToDirectAssetIds { get; init; }
    public required IReadOnlyDictionary<AssetGuid, int[]> PrefabGuidToInstantiatingAssetIds { get; init; }
}

/// <summary>
/// Lightweight in-memory node representing an indexed Unity asset.
/// </summary>
public readonly record struct AssetNode(
    int AssetId,
    AssetGuid Guid,
    string RelativePath,
    string FullPath,
    AssetKind Kind);

/// <summary>
/// Result of resolving direct and transitive prefab/scene usages for a script GUID.
/// </summary>
public readonly record struct TransitiveUsageResult(
    int[] DirectPrefabIds,
    int[] TransitivePrefabIds,
    int[] SceneIds,
    AssetGuid[] AllPrefabGuidsInChain);

/// <summary>
/// Lock-free, in-memory adjacency graph projected from <see cref="AssetSqliteStore"/>
/// for sub-millisecond CodeLens reference counts and transitive prefab/scene BFS traversal.
/// </summary>
public sealed class InMemoryAssetGraph
{
    private volatile AssetGraphSnapshot _current = AssetGraphSnapshot.Empty;

    /// <summary>
    /// Gets the latest immutable graph snapshot.
    /// </summary>
    public AssetGraphSnapshot Current => _current;

    /// <summary>
    /// Rebuilds the in-memory snapshot from the current committed SQLite state.
    /// </summary>
    public void RebuildFromStore(AssetSqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _current = store.LoadGraphSnapshot();
    }

    /// <summary>
    /// Resolves direct prefabs, transitive/variant prefabs, and scenes that reference
    /// <paramref name="scriptGuid"/> using cycle-safe BFS traversal over prefab instantiations.
    /// </summary>
    public TransitiveUsageResult ResolveTransitiveUsages(AssetGuid scriptGuid)
    {
        AssetGraphSnapshot snapshot = _current;
        if (scriptGuid.IsZero ||
            !snapshot.ScriptToDirectAssetIds.TryGetValue(scriptGuid, out int[]? directIds) ||
            directIds.Length == 0)
        {
            return new TransitiveUsageResult(
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<AssetGuid>());
        }

        var directPrefabIds = new List<int>();
        var directPrefabIdSet = new HashSet<int>();
        var transitivePrefabIds = new List<int>();
        var transitivePrefabIdSet = new HashSet<int>();
        var sceneIds = new HashSet<int>();
        var visitedPrefabGuids = new HashSet<AssetGuid>();
        var queue = new Queue<AssetGuid>();

        foreach (int assetId in directIds)
        {
            if (!snapshot.AssetsById.TryGetValue(assetId, out AssetNode node))
            {
                continue;
            }

            if (node.Kind == AssetKind.Prefab)
            {
                if (directPrefabIdSet.Add(assetId))
                {
                    directPrefabIds.Add(assetId);
                }

                if (!node.Guid.IsZero && visitedPrefabGuids.Add(node.Guid))
                {
                    queue.Enqueue(node.Guid);
                }
            }
            else if (node.Kind == AssetKind.Scene)
            {
                sceneIds.Add(assetId);
            }
        }

        while (queue.Count > 0)
        {
            AssetGuid currentPrefabGuid = queue.Dequeue();
            if (!snapshot.PrefabGuidToInstantiatingAssetIds.TryGetValue(currentPrefabGuid, out int[]? instantiatingIds))
            {
                continue;
            }

            foreach (int instId in instantiatingIds)
            {
                if (!snapshot.AssetsById.TryGetValue(instId, out AssetNode instNode))
                {
                    continue;
                }

                if (instNode.Kind == AssetKind.Prefab)
                {
                    if (!directPrefabIdSet.Contains(instId) && transitivePrefabIdSet.Add(instId))
                    {
                        transitivePrefabIds.Add(instId);
                    }

                    if (!instNode.Guid.IsZero && visitedPrefabGuids.Add(instNode.Guid))
                    {
                        queue.Enqueue(instNode.Guid);
                    }
                }
                else if (instNode.Kind == AssetKind.Scene)
                {
                    sceneIds.Add(instId);
                }
            }
        }

        return new TransitiveUsageResult(
            directPrefabIds.ToArray(),
            transitivePrefabIds.ToArray(),
            sceneIds.ToArray(),
            visitedPrefabGuids.ToArray());
    }

    /// <summary>
    /// Computes direct prefab, transitive prefab, and scene reference counts for each script GUID
    /// purely in memory without disk or SQLite I/O.
    /// </summary>
    public ScriptReferenceCountDto[] GetScriptReferenceCounts(IEnumerable<string> scriptGuids)
    {
        ArgumentNullException.ThrowIfNull(scriptGuids);

        var results = new List<ScriptReferenceCountDto>();
        foreach (string? rawGuid in scriptGuids)
        {
            string guidText = rawGuid ?? string.Empty;
            if (!AssetGuid.TryParseHex(guidText, out AssetGuid guid) || guid.IsZero)
            {
                results.Add(new ScriptReferenceCountDto { ScriptGuid = guidText });
                continue;
            }

            TransitiveUsageResult usage = ResolveTransitiveUsages(guid);
            results.Add(new ScriptReferenceCountDto
            {
                ScriptGuid = guidText,
                DirectPrefabCount = usage.DirectPrefabIds.Length,
                TransitivePrefabCount = usage.TransitivePrefabIds.Length,
                SceneCount = usage.SceneIds.Length,
            });
        }

        return results.ToArray();
    }
}
