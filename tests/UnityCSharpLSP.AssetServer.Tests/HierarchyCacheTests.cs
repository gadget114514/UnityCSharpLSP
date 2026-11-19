using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Daemon;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class HierarchyCacheTests
{
    private const string PathA = "Assets/Prefabs/A.prefab";
    private const string PathB = "Assets/Prefabs/B.prefab";
    private const string PathC = "Assets/Prefabs/C.prefab";

    [Fact]
    public void TryGet_AfterSet_ReturnsCachedValueAndCountsHit()
    {
        var cache = new HierarchyCache();
        AssetHierarchyNodeDto[] value = Node("A");
        cache.Set(PathA, maxDepth: 8, includeSerializedValues: true, value);

        Assert.True(cache.TryGet(PathA, 8, true, out AssetHierarchyNodeDto[]? cached));
        Assert.Same(value, cached);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(0, cache.Misses);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TryGet_Missing_CountsMissAndIsCaseInsensitiveOnWindows()
    {
        var cache = new HierarchyCache();
        cache.Set(PathA, 8, false, Node("A"));

        Assert.False(cache.TryGet(PathB, 8, false, out _));
        Assert.Equal(0, cache.Hits);
        Assert.Equal(1, cache.Misses);

        Assert.True(cache.TryGet(PathA.ToUpperInvariant(), 8, false, out _));
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, cache.Hits);
    }

    [Fact]
    public void TryGet_DistinguishesBuildOptions()
    {
        var cache = new HierarchyCache();
        AssetHierarchyNodeDto[] shallow = Node("shallow");
        AssetHierarchyNodeDto[] deep = Node("deep");
        cache.Set(PathA, 4, false, shallow);
        cache.Set(PathA, 8, false, deep);

        Assert.True(cache.TryGet(PathA, 4, false, out AssetHierarchyNodeDto[]? foundShallow));
        Assert.Same(shallow, foundShallow);
        Assert.True(cache.TryGet(PathA, 8, false, out AssetHierarchyNodeDto[]? foundDeep));
        Assert.Same(deep, foundDeep);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Invalidate_RemovesAllOptionVariantsForThatPathOnly()
    {
        var cache = new HierarchyCache();
        cache.Set(PathA, 4, false, Node("a-shallow"));
        cache.Set(PathA, 8, true, Node("a-deep"));
        cache.Set(PathB, 8, true, Node("b"));

        Assert.Equal(2, cache.Invalidate(PathA));
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(PathA, 4, false, out _));
        Assert.False(cache.TryGet(PathA, 8, true, out _));
        Assert.True(cache.TryGet(PathB, 8, true, out _));
        Assert.Equal(0, cache.Invalidate(PathA));
    }

    [Fact]
    public void Set_EvictsLeastRecentlyUsedEntryWhenCapacityExceeded()
    {
        var cache = new HierarchyCache(capacity: 2);
        cache.Set(PathA, 8, false, Node("a"));
        cache.Set(PathB, 8, false, Node("b"));

        Assert.True(cache.TryGet(PathA, 8, false, out _));

        cache.Set(PathC, 8, false, Node("c"));

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(PathA, 8, false, out _));
        Assert.True(cache.TryGet(PathC, 8, false, out _));
        Assert.False(cache.TryGet(PathB, 8, false, out _));
    }

    [Fact]
    public void Set_WithStaleVersion_DiscardsResultBuiltDuringInvalidation()
    {
        var cache = new HierarchyCache();
        cache.Set(PathA, 8, false, Node("a"));
        long version = cache.Version;

        Assert.Equal(1, cache.Invalidate(PathA));
        cache.Set(PathA, 8, false, Node("stale"), expectedVersion: version);

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(PathA, 8, false, out _));
    }

    [Fact]
    public void Clear_RemovesEveryEntry()
    {
        var cache = new HierarchyCache();
        cache.Set(PathA, 8, false, Node("a"));
        cache.Set(PathB, 8, false, Node("b"));

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(PathA, 8, false, out _));
        Assert.False(cache.TryGet(PathB, 8, false, out _));
    }

    private static AssetHierarchyNodeDto[] Node(string name) =>
        [new AssetHierarchyNodeDto { Name = name }];
}
