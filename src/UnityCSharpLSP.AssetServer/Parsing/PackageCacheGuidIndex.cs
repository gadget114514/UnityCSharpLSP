using System.Collections.Concurrent;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Parsing;

/// <summary>
/// Lightweight, lazily built index of GUIDs declared by <c>.meta</c> files under
/// <c>Library/PackageCache</c>. Unity package assets are not part of the indexed project graph,
/// so this index is consulted before reporting <c>UNITY0002</c> to avoid false positives.
/// The index is built once on first use (shared by every service for the same project root) and
/// then cached for the lifetime of the process.
/// </summary>
public sealed class PackageCacheGuidIndex
{
    private static readonly ConcurrentDictionary<string, Lazy<HashSet<AssetGuid>>> SharedIndexes =
        new(BuildIndexKeyComparer);

    private readonly Lazy<HashSet<AssetGuid>> _guids;

    public PackageCacheGuidIndex(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        PackageCacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
        _guids = SharedIndexes.GetOrAdd(
            PackageCacheRoot,
            _ => new Lazy<HashSet<AssetGuid>>(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication));
    }

    public string PackageCacheRoot { get; }

    public bool Contains(AssetGuid guid)
    {
        if (guid.IsZero)
        {
            return false;
        }

        return _guids.Value.Contains(guid);
    }

    public bool Contains(string guidHex)
    {
        return AssetGuid.TryParseHex(guidHex, out AssetGuid guid) && Contains(guid);
    }

    private HashSet<AssetGuid> BuildIndex()
    {
        var guids = new ConcurrentBag<AssetGuid>();
        List<string> metaPaths;

        try
        {
            if (!Directory.Exists(PackageCacheRoot))
            {
                return new HashSet<AssetGuid>();
            }

            metaPaths = Directory.EnumerateFiles(PackageCacheRoot, "*.meta", SearchOption.AllDirectories).ToList();
        }
        catch (IOException)
        {
            return new HashSet<AssetGuid>();
        }
        catch (UnauthorizedAccessException)
        {
            return new HashSet<AssetGuid>();
        }

        Parallel.ForEach(
            metaPaths,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) },
            metaPath =>
            {
                if (MetaFileParser.TryReadGuid(metaPath, out AssetGuid guid) && !guid.IsZero)
                {
                    guids.Add(guid);
                }
            });

        return new HashSet<AssetGuid>(guids);
    }

    private static StringComparer BuildIndexKeyComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
