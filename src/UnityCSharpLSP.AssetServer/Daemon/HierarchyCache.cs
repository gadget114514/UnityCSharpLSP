using UnityCSharpLSP.AssetServer.Contracts;

namespace UnityCSharpLSP.AssetServer.Daemon;

/// <summary>
/// Bounded LRU cache for <c>assetd/getHierarchy</c> results. Entries are keyed by the resolved
/// full asset path plus the build options, and can be invalidated by asset path when the
/// file watcher commits a differential update.
/// </summary>
public sealed class HierarchyCache
{
    public const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly StringComparer _pathComparer;
    private readonly Dictionary<HierarchyCacheKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _lru = new();
    private readonly object _gate = new();

    private long _version;
    private long _hits;
    private long _misses;

    public HierarchyCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _capacity = capacity;
        _pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _entries = new Dictionary<HierarchyCacheKey, LinkedListNode<Entry>>(HierarchyCacheKeyComparer.For(_pathComparer));
    }

    public int Capacity => _capacity;

    /// <summary>
    /// Incremented whenever an entry is added, replaced or invalidated. Used to avoid storing a
    /// result that was built while the cache was being invalidated.
    /// </summary>
    public long Version
    {
        get { lock (_gate) return _version; }
    }

    public long Hits
    {
        get { lock (_gate) return _hits; }
    }

    public long Misses
    {
        get { lock (_gate) return _misses; }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public bool TryGet(
        string fullPath,
        int maxDepth,
        bool includeSerializedValues,
        out AssetHierarchyNodeDto[]? value)
    {
        var key = new HierarchyCacheKey(fullPath, maxDepth, includeSerializedValues);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                _hits++;
                value = node.Value.Value;
                return true;
            }

            _misses++;
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Stores a result. When <paramref name="expectedVersion"/> does not match the current
    /// <see cref="Version"/>, the cache was invalidated while the result was being built and the
    /// stale result is deliberately discarded.
    /// </summary>
    public void Set(
        string fullPath,
        int maxDepth,
        bool includeSerializedValues,
        AssetHierarchyNodeDto[] value,
        long expectedVersion = -1)
    {
        var key = new HierarchyCacheKey(fullPath, maxDepth, includeSerializedValues);
        lock (_gate)
        {
            if (expectedVersion >= 0 && expectedVersion != _version)
            {
                return;
            }

            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                existing.Value.Value = value;
                _lru.Remove(existing);
                _lru.AddLast(existing);
                _version++;
                return;
            }

            LinkedListNode<Entry> node = _lru.AddLast(new Entry(key, value));
            _entries.Add(key, node);
            _version++;

            while (_entries.Count > _capacity)
            {
                LinkedListNode<Entry> leastRecentlyUsed = _lru.First!;
                _lru.RemoveFirst();
                _entries.Remove(leastRecentlyUsed.Value.Key);
            }
        }
    }

    /// <summary>
    /// Removes every entry for <paramref name="fullPath"/> (all option combinations).
    /// </summary>
    public int Invalidate(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return 0;
        }

        lock (_gate)
        {
            var keys = new List<HierarchyCacheKey>();
            foreach (KeyValuePair<HierarchyCacheKey, LinkedListNode<Entry>> pair in _entries)
            {
                if (_pathComparer.Equals(pair.Key.FullPath, fullPath))
                {
                    keys.Add(pair.Key);
                }
            }

            foreach (HierarchyCacheKey key in keys)
            {
                LinkedListNode<Entry> node = _entries[key];
                _entries.Remove(key);
                _lru.Remove(node);
            }

            if (keys.Count > 0)
            {
                _version++;
            }

            return keys.Count;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
            _lru.Clear();
            _version++;
        }
    }

    private sealed class Entry(HierarchyCacheKey key, AssetHierarchyNodeDto[] value)
    {
        public HierarchyCacheKey Key { get; } = key;

        public AssetHierarchyNodeDto[] Value { get; set; } = value;
    }

    private readonly record struct HierarchyCacheKey(string FullPath, int MaxDepth, bool IncludeSerializedValues);

    private sealed class HierarchyCacheKeyComparer : IEqualityComparer<HierarchyCacheKey>
    {
        private readonly StringComparer _pathComparer;

        private HierarchyCacheKeyComparer(StringComparer pathComparer)
        {
            _pathComparer = pathComparer;
        }

        public static HierarchyCacheKeyComparer For(StringComparer pathComparer) => new(pathComparer);

        public bool Equals(HierarchyCacheKey x, HierarchyCacheKey y)
        {
            return x.MaxDepth == y.MaxDepth
                && x.IncludeSerializedValues == y.IncludeSerializedValues
                && _pathComparer.Equals(x.FullPath, y.FullPath);
        }

        public int GetHashCode(HierarchyCacheKey obj)
        {
            var hash = new HashCode();
            hash.Add(_pathComparer.GetHashCode(obj.FullPath));
            hash.Add(obj.MaxDepth);
            hash.Add(obj.IncludeSerializedValues);
            return hash.ToHashCode();
        }
    }
}
