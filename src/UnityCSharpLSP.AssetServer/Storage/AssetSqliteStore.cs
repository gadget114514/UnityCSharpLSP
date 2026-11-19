using System.IO.Enumeration;
using Microsoft.Data.Sqlite;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Parsing;

namespace UnityCSharpLSP.AssetServer.Storage;

/// <summary>
/// Summary counts returned by <see cref="AssetSqliteStore.GetStatusCounts"/>.
/// </summary>
public readonly record struct AssetStatusCounts(
    int TotalAssets,
    int TotalScripts,
    int TotalPrefabs,
    int TotalScenes);

/// <summary>
/// Result counters returned by <see cref="AssetSqliteStore.SyncWorkspaceAsync"/>.
/// </summary>
public readonly record struct AssetSyncResult(
    int TotalAssets,
    int UpdatedOrAdded,
    int Removed,
    int SkippedUnchanged);

/// <summary>
/// Represents a <c>MonoBehaviour</c> attachment whose <c>script_guid</c> is missing from <c>assets</c>.
/// </summary>
public readonly record struct MissingScriptAttachment(
    long ComponentFileId,
    long GameObjectFileId,
    string? GameObjectName,
    AssetGuid ScriptGuid,
    int LineNumber);

/// <summary>
/// A <c>PrefabInstance.m_Modifications</c> override row joined with the asset that contains it.
/// </summary>
public readonly record struct PrefabOverrideModification(
    int AssetId,
    long PrefabInstanceFileId,
    AssetGuid TargetGuid,
    long TargetFileId,
    string RootFieldName,
    string PropertyPath,
    int LineNumber,
    string OwningAssetRelativePath);

/// <summary>
/// Single-writer SQLite storage engine (<c>WAL + mmap</c>) for Unity asset metadata,
/// script attachments, prefab instances, property modifications, serialized fields, and UnityEvent calls.
/// </summary>
public sealed class AssetSqliteStore : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxGuidsPerInClause = 500;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library",
        "Temp",
        "Logs",
        "obj",
        "bin",
        ".git",
        ".hancho",
        ".vs",
    };

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public string ProjectRoot { get; }
    public string DatabasePath { get; }
    public InMemoryAssetGraph Graph { get; }

    public AssetSqliteStore(string projectRoot, string? databasePath = null, InMemoryAssetGraph? graph = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root cannot be empty.", nameof(projectRoot));
        }

        ProjectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        DatabasePath = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(ProjectRoot, "Library", "UnityCSharpLSP", "asset-db.sqlite")
            : Path.GetFullPath(databasePath);
        Graph = graph ?? new InMemoryAssetGraph();

        string? dbDir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        InitializeDatabase();
        Graph.RebuildFromStore(this);
    }

    /// <summary>
    /// Incrementally synchronizes all indexed assets under <c>Assets/</c> and <c>Packages/</c>
    /// (or <see cref="ProjectRoot"/> if neither exists) using <see cref="FileSystemEnumerable{TResult}"/>
    /// without opening unchanged files.
    /// </summary>
    public async Task<AssetSyncResult> SyncWorkspaceAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            using SqliteConnection connection = OpenConnection();

            Dictionary<string, ExistingAssetRow> existingByRelPath = LoadExistingAssets(connection);
            var existingByGuid = new Dictionary<AssetGuid, int>(existingByRelPath.Count);
            var existingGuidById = new Dictionary<int, AssetGuid>(existingByRelPath.Count);
            foreach (ExistingAssetRow row in existingByRelPath.Values)
            {
                existingByGuid[row.Guid] = row.AssetId;
                existingGuidById[row.AssetId] = row.Guid;
            }

            (Dictionary<string, long> metaMap, Dictionary<string, (long LastWriteTicks, long Length)> assetMap) =
                EnumerateWorkspaceFiles(ct);

            var seenRelPaths = new HashSet<string>(PathComparer);
            var deletedAssetIds = new List<int>();
            var pendingUpserts = new List<PendingAssetUpsert>();
            int skippedUnchanged = 0;

            foreach ((string assetFullPath, (long assetMtime, long assetSize)) in assetMap)
            {
                ct.ThrowIfCancellationRequested();

                if (!metaMap.TryGetValue(assetFullPath, out long metaMtime))
                {
                    continue;
                }

                string relPath = ToRelativePath(assetFullPath);
                seenRelPaths.Add(relPath);

                if (existingByRelPath.TryGetValue(relPath, out ExistingAssetRow? existing) &&
                    existing.MetaMtime == metaMtime &&
                    existing.AssetMtime == assetMtime &&
                    existing.AssetSize == assetSize)
                {
                    skippedUnchanged++;
                    continue;
                }

                if (!MetaFileParser.TryReadGuid(assetFullPath + ".meta", out AssetGuid guid) || guid.IsZero)
                {
                    if (existing is not null)
                    {
                        deletedAssetIds.Add(existing.AssetId);
                    }
                    continue;
                }

                AssetKind kind = ClassifyAssetKind(assetFullPath);
                YamlScanResult? scanResult = ShouldScanYaml(kind)
                    ? FastUnityYamlScanner.ScanFile(assetFullPath)
                    : null;

                pendingUpserts.Add(new PendingAssetUpsert(
                    existing?.AssetId,
                    guid,
                    relPath,
                    kind,
                    metaMtime,
                    assetMtime,
                    assetSize,
                    scanResult));
            }

            foreach (ExistingAssetRow row in existingByRelPath.Values)
            {
                if (!seenRelPaths.Contains(row.RelPath))
                {
                    deletedAssetIds.Add(row.AssetId);
                }
            }

            if (deletedAssetIds.Count > 0 || pendingUpserts.Count > 0)
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                using PreparedWriteCommands writers = new(connection, transaction);

                foreach (int deletedId in deletedAssetIds)
                {
                    writers.DeleteAssetAndChildren(deletedId);
                    if (existingGuidById.Remove(deletedId, out AssetGuid removedGuid))
                    {
                        existingByGuid.Remove(removedGuid);
                    }
                }

                foreach (PendingAssetUpsert upsert in pendingUpserts)
                {
                    ct.ThrowIfCancellationRequested();

                    if (existingByGuid.TryGetValue(upsert.Guid, out int collidingAssetId) &&
                        collidingAssetId != upsert.ExistingAssetId)
                    {
                        writers.DeleteAssetAndChildren(collidingAssetId);
                        existingByGuid.Remove(upsert.Guid);
                    }

                    int assetId = writers.UpsertAssetAndChildren(upsert);
                    existingByGuid[upsert.Guid] = assetId;
                }

                transaction.Commit();
            }

            Graph.RebuildFromStore(this);
            return new AssetSyncResult(
                seenRelPaths.Count,
                pendingUpserts.Count,
                deletedAssetIds.Count,
                skippedUnchanged);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Incrementally updates or inserts a single asset (and its <c>.meta</c>) into SQLite
    /// and refreshes <see cref="Graph"/>. Removes the asset if it or its <c>.meta</c> no longer exists.
    /// </summary>
    public async Task UpdateSingleAssetAsync(string fullPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        string assetFullPath = NormalizeAssetFullPath(fullPath);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (!TryGetRelativePathInsideWorkspace(assetFullPath, out string relPath) ||
                IsUnderIgnoredDirectory(relPath))
            {
                return;
            }

            string metaFullPath = assetFullPath + ".meta";
            var assetInfo = new FileInfo(assetFullPath);
            var metaInfo = new FileInfo(metaFullPath);

            if (!assetInfo.Exists || !metaInfo.Exists)
            {
                RemoveSingleAssetCore(relPath);
                return;
            }

            if (!MetaFileParser.TryReadGuid(metaFullPath, out AssetGuid guid) || guid.IsZero)
            {
                RemoveSingleAssetCore(relPath);
                return;
            }

            long metaMtime = metaInfo.LastWriteTimeUtc.Ticks;
            long assetMtime = assetInfo.LastWriteTimeUtc.Ticks;
            long assetSize = assetInfo.Length;
            AssetKind kind = ClassifyAssetKind(assetFullPath);
            YamlScanResult? scanResult = ShouldScanYaml(kind)
                ? FastUnityYamlScanner.ScanFile(assetFullPath)
                : null;

            using SqliteConnection connection = OpenConnection();
            int? existingId = FindAssetIdByRelPath(connection, relPath);
            int? collidingGuidId = FindAssetIdByGuid(connection, guid);

            using SqliteTransaction transaction = connection.BeginTransaction();
            using PreparedWriteCommands writers = new(connection, transaction);

            if (collidingGuidId.HasValue && collidingGuidId != existingId)
            {
                writers.DeleteAssetAndChildren(collidingGuidId.Value);
            }

            writers.UpsertAssetAndChildren(new PendingAssetUpsert(
                existingId,
                guid,
                relPath,
                kind,
                metaMtime,
                assetMtime,
                assetSize,
                scanResult));

            transaction.Commit();
            Graph.RebuildFromStore(this);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Removes a single asset from all tables in SQLite and refreshes <see cref="Graph"/>.
    /// </summary>
    public async Task RemoveSingleAssetAsync(string fullPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        string assetFullPath = NormalizeAssetFullPath(fullPath);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (!TryGetRelativePathInsideWorkspace(assetFullPath, out string relPath))
            {
                return;
            }

            RemoveSingleAssetCore(relPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Looks up an indexed asset by its 128-bit GUID.
    /// </summary>
    public AssetEntryDto? GetAssetByGuid(AssetGuid guid)
    {
        ThrowIfDisposed();
        if (guid.IsZero)
        {
            return null;
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT asset_id, guid, rel_path, asset_kind
            FROM assets
            WHERE guid = $guid
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$guid", guid.ToByteArray());

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAssetEntry(reader) : null;
    }

    /// <summary>
    /// Looks up an indexed asset by its relative path (from project root) or full filesystem path.
    /// </summary>
    public AssetEntryDto? GetAssetByPath(string relativeOrFullPath)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(relativeOrFullPath))
        {
            return null;
        }

        if (!TryNormalizeQueryRelPath(relativeOrFullPath, out string relPath))
        {
            return null;
        }

        using SqliteConnection connection = OpenConnection();
        AssetEntryDto? exact = QueryAssetByRelPath(connection, relPath, ignoreCase: false);
        if (exact is not null || !OperatingSystem.IsWindows())
        {
            return exact;
        }

        return QueryAssetByRelPath(connection, relPath, ignoreCase: true);
    }

    /// <summary>
    /// Returns aggregate asset counts for status reporting.
    /// </summary>
    public AssetStatusCounts GetStatusCounts()
    {
        ThrowIfDisposed();

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN asset_kind = 1 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN asset_kind = 2 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN asset_kind = 3 THEN 1 ELSE 0 END), 0)
            FROM assets;
            """;

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return default;
        }

        return new AssetStatusCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    /// <summary>
    /// Queries direct <c>MonoBehaviour</c> script attachments and, when
    /// <paramref name="includeTransitiveScenes"/> is <c>true</c>, indirect <c>PrefabInstance</c>
    /// placements referencing any prefab in <paramref name="transitivePrefabGuids"/>.
    /// </summary>
    public AssetReferenceResultDto QueryClassReferences(
        AssetGuid scriptGuid,
        IReadOnlyCollection<AssetGuid> transitivePrefabGuids,
        bool includeTransitiveScenes,
        int maxResults)
    {
        ThrowIfDisposed();
        if (scriptGuid.IsZero || maxResults <= 0)
        {
            return new AssetReferenceResultDto();
        }

        var locations = new List<AssetReferenceLocationDto>();
        bool truncated = false;

        using SqliteConnection connection = OpenConnection();
        using (SqliteCommand directCmd = connection.CreateCommand())
        {
            directCmd.CommandText = """
                SELECT a.rel_path, a.asset_kind, sa.line_number, sa.game_object_name
                FROM script_attachments sa
                JOIN assets a ON a.asset_id = sa.asset_id
                WHERE sa.script_guid = $scriptGuid
                ORDER BY a.rel_path, sa.line_number
                LIMIT $limitPlusOne;
                """;
            directCmd.Parameters.AddWithValue("$scriptGuid", scriptGuid.ToByteArray());
            directCmd.Parameters.AddWithValue("$limitPlusOne", maxResults + 1);

            using SqliteDataReader reader = directCmd.ExecuteReader();
            while (reader.Read())
            {
                if (locations.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                string relPath = reader.GetString(0);
                locations.Add(new AssetReferenceLocationDto
                {
                    RelativePath = relPath,
                    FullPath = ToFullPath(relPath),
                    AssetKind = (AssetKind)reader.GetInt32(1),
                    Line = reader.GetInt32(2),
                    Character = 0,
                    EndCharacter = 0,
                    GameObjectName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    PropertyPath = null,
                    IsOverride = false,
                });
            }
        }

        if (!truncated && includeTransitiveScenes && transitivePrefabGuids is { Count: > 0 })
        {
            var uniqueGuids = new List<AssetGuid>(transitivePrefabGuids.Count);
            var seen = new HashSet<AssetGuid>();
            foreach (AssetGuid prefabGuid in transitivePrefabGuids)
            {
                if (!prefabGuid.IsZero && seen.Add(prefabGuid))
                {
                    uniqueGuids.Add(prefabGuid);
                }
            }

            for (int offset = 0; offset < uniqueGuids.Count && !truncated; offset += MaxGuidsPerInClause)
            {
                int batchCount = Math.Min(MaxGuidsPerInClause, uniqueGuids.Count - offset);
                int remainingPlusOne = (maxResults - locations.Count) + 1;

                using SqliteCommand indirectCmd = connection.CreateCommand();
                var placeholders = new string[batchCount];
                for (int i = 0; i < batchCount; i++)
                {
                    string paramName = $"$g{i}";
                    placeholders[i] = paramName;
                    indirectCmd.Parameters.AddWithValue(paramName, uniqueGuids[offset + i].ToByteArray());
                }

                indirectCmd.CommandText = $"""
                    SELECT a.rel_path, a.asset_kind, pi.line_number
                    FROM prefab_instances pi
                    JOIN assets a ON a.asset_id = pi.asset_id
                    WHERE pi.source_prefab_guid IN ({string.Join(", ", placeholders)})
                    ORDER BY a.rel_path, pi.line_number
                    LIMIT $limitPlusOne;
                    """;
                indirectCmd.Parameters.AddWithValue("$limitPlusOne", remainingPlusOne);

                using SqliteDataReader reader = indirectCmd.ExecuteReader();
                while (reader.Read())
                {
                    if (locations.Count >= maxResults)
                    {
                        truncated = true;
                        break;
                    }

                    string relPath = reader.GetString(0);
                    locations.Add(new AssetReferenceLocationDto
                    {
                        RelativePath = relPath,
                        FullPath = ToFullPath(relPath),
                        AssetKind = (AssetKind)reader.GetInt32(1),
                        Line = reader.GetInt32(2),
                        Character = 0,
                        EndCharacter = 0,
                        GameObjectName = null,
                        PropertyPath = null,
                        IsOverride = false,
                    });
                }
            }
        }

        return new AssetReferenceResultDto
        {
            Locations = locations.ToArray(),
            Truncated = truncated,
        };
    }

    /// <summary>
    /// Queries direct <c>serialized_fields</c> definitions and <c>prefab_modifications</c> overrides
    /// for a serialized field on <paramref name="scriptGuid"/> via SQL <c>UNION ALL</c>.
    /// </summary>
    public AssetReferenceResultDto QueryFieldReferences(
        AssetGuid scriptGuid,
        string fieldName,
        int maxResults)
    {
        ThrowIfDisposed();
        if (scriptGuid.IsZero || string.IsNullOrEmpty(fieldName) || maxResults <= 0)
        {
            return new AssetReferenceResultDto();
        }

        var locations = new List<AssetReferenceLocationDto>();
        bool truncated = false;

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.rel_path, a.asset_kind, sf.line_number, sa.game_object_name, sf.field_name AS path, 0 AS is_override
            FROM script_attachments sa
            JOIN serialized_fields sf ON sf.asset_id = sa.asset_id AND sf.component_fid = sa.component_fid
            JOIN assets a ON a.asset_id = sa.asset_id
            WHERE sa.script_guid = $scriptGuid AND sf.field_name = $fieldName

            UNION ALL

            SELECT scene_or_variant.rel_path, scene_or_variant.asset_kind, pm.line_number, sa.game_object_name, pm.property_path AS path, 1 AS is_override
            FROM script_attachments sa
            JOIN assets base_prefab ON base_prefab.asset_id = sa.asset_id
            JOIN prefab_modifications pm
              ON pm.target_guid = base_prefab.guid
             AND pm.target_fid = sa.component_fid
            JOIN assets scene_or_variant ON scene_or_variant.asset_id = pm.asset_id
            WHERE sa.script_guid = $scriptGuid AND pm.root_field_name = $fieldName
            LIMIT $limitPlusOne;
            """;
        cmd.Parameters.AddWithValue("$scriptGuid", scriptGuid.ToByteArray());
        cmd.Parameters.AddWithValue("$fieldName", fieldName);
        cmd.Parameters.AddWithValue("$limitPlusOne", maxResults + 1);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (locations.Count >= maxResults)
            {
                truncated = true;
                break;
            }

            string relPath = reader.GetString(0);
            locations.Add(new AssetReferenceLocationDto
            {
                RelativePath = relPath,
                FullPath = ToFullPath(relPath),
                AssetKind = (AssetKind)reader.GetInt32(1),
                Line = reader.GetInt32(2),
                Character = 0,
                EndCharacter = 0,
                GameObjectName = reader.IsDBNull(3) ? null : reader.GetString(3),
                PropertyPath = reader.GetString(4),
                IsOverride = reader.GetInt32(5) != 0,
            });
        }

        return new AssetReferenceResultDto
        {
            Locations = locations.ToArray(),
            Truncated = truncated,
        };
    }

    /// <summary>
    /// Reverse-lookups every <c>prefab_modifications</c> row targeting the object identified by
    /// <paramref name="targetGuid"/> + <paramref name="targetFileId"/>, joined with the asset that stores the override.
    /// </summary>
    public PrefabOverrideModification[] GetPrefabModificationsByTarget(AssetGuid targetGuid, long targetFileId)
    {
        ThrowIfDisposed();
        if (targetGuid.IsZero)
        {
            return Array.Empty<PrefabOverrideModification>();
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT pm.asset_id, pm.prefab_instance_fid, pm.target_guid, pm.target_fid,
                   pm.root_field_name, pm.property_path, pm.line_number, a.rel_path
            FROM prefab_modifications pm
            JOIN assets a ON a.asset_id = pm.asset_id
            WHERE pm.target_guid = $targetGuid AND pm.target_fid = $targetFid
            ORDER BY a.rel_path, pm.line_number;
            """;
        cmd.Parameters.AddWithValue("$targetGuid", targetGuid.ToByteArray());
        cmd.Parameters.AddWithValue("$targetFid", targetFileId);

        return ReadPrefabOverrideModifications(cmd);
    }

    /// <summary>
    /// Returns all <c>m_Modifications</c> override rows stored inside the asset identified by <paramref name="assetId"/>.
    /// </summary>
    public PrefabOverrideModification[] GetPrefabModificationsForAsset(int assetId)
    {
        ThrowIfDisposed();
        if (assetId <= 0)
        {
            return Array.Empty<PrefabOverrideModification>();
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT pm.asset_id, pm.prefab_instance_fid, pm.target_guid, pm.target_fid,
                   pm.root_field_name, pm.property_path, pm.line_number, a.rel_path
            FROM prefab_modifications pm
            JOIN assets a ON a.asset_id = pm.asset_id
            WHERE pm.asset_id = $assetId
            ORDER BY pm.line_number;
            """;
        cmd.Parameters.AddWithValue("$assetId", assetId);

        return ReadPrefabOverrideModifications(cmd);
    }

    private static PrefabOverrideModification[] ReadPrefabOverrideModifications(SqliteCommand cmd)
    {
        var results = new List<PrefabOverrideModification>();
        byte[] guidBuffer = new byte[16];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new PrefabOverrideModification(
                reader.GetInt32(0),
                reader.GetInt64(1),
                ReadGuid(reader, 2, guidBuffer),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7)));
        }

        return results.ToArray();
    }

    /// <summary>
    /// Queries <c>unity_event_calls</c> referencing <paramref name="methodName"/>
    /// on components whose script matches <paramref name="scriptGuid"/>.
    /// </summary>
    public AssetReferenceResultDto QueryMethodReferences(
        AssetGuid scriptGuid,
        string methodName,
        int maxResults)
    {
        ThrowIfDisposed();
        if (scriptGuid.IsZero || string.IsNullOrEmpty(methodName) || maxResults <= 0)
        {
            return new AssetReferenceResultDto();
        }

        var locations = new List<AssetReferenceLocationDto>();
        bool truncated = false;

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.rel_path, a.asset_kind, ue.line_number, sa.game_object_name, ue.method_name
            FROM unity_event_calls ue
            JOIN assets a ON a.asset_id = ue.asset_id
            JOIN script_attachments sa
              ON sa.asset_id = ue.asset_id
             AND sa.component_fid = ue.target_fid
            WHERE ue.target_guid IS NULL
              AND ue.method_name = $methodName
              AND sa.script_guid = $scriptGuid

            UNION ALL

            SELECT a.rel_path, a.asset_kind, ue.line_number, sa.game_object_name, ue.method_name
            FROM unity_event_calls ue
            JOIN assets a ON a.asset_id = ue.asset_id
            JOIN assets target_asset ON target_asset.guid = ue.target_guid
            JOIN script_attachments sa
              ON sa.asset_id = target_asset.asset_id
             AND sa.component_fid = ue.target_fid
            WHERE ue.target_guid IS NOT NULL
              AND ue.method_name = $methodName
              AND sa.script_guid = $scriptGuid
            LIMIT $limitPlusOne;
            """;
        cmd.Parameters.AddWithValue("$scriptGuid", scriptGuid.ToByteArray());
        cmd.Parameters.AddWithValue("$methodName", methodName);
        cmd.Parameters.AddWithValue("$limitPlusOne", maxResults + 1);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (locations.Count >= maxResults)
            {
                truncated = true;
                break;
            }

            string relPath = reader.GetString(0);
            locations.Add(new AssetReferenceLocationDto
            {
                RelativePath = relPath,
                FullPath = ToFullPath(relPath),
                AssetKind = (AssetKind)reader.GetInt32(1),
                Line = reader.GetInt32(2),
                Character = 0,
                EndCharacter = 0,
                GameObjectName = reader.IsDBNull(3) ? null : reader.GetString(3),
                PropertyPath = reader.GetString(4),
                IsOverride = false,
            });
        }

        return new AssetReferenceResultDto
        {
            Locations = locations.ToArray(),
            Truncated = truncated,
        };
    }

    /// <summary>
    /// Returns <c>true</c> if any <c>unity_event_calls</c> row targets <paramref name="methodName"/>
    /// on a component attached to <paramref name="scriptGuid"/>.
    /// </summary>
    public bool IsMethodReferencedByUnityEvent(AssetGuid scriptGuid, string methodName)
    {
        ThrowIfDisposed();
        if (scriptGuid.IsZero || string.IsNullOrEmpty(methodName))
        {
            return false;
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM unity_event_calls ue
            JOIN script_attachments sa
              ON sa.asset_id = ue.asset_id
             AND sa.component_fid = ue.target_fid
            WHERE ue.target_guid IS NULL
              AND ue.method_name = $methodName
              AND sa.script_guid = $scriptGuid

            UNION ALL

            SELECT 1
            FROM unity_event_calls ue
            JOIN assets target_asset ON target_asset.guid = ue.target_guid
            JOIN script_attachments sa
              ON sa.asset_id = target_asset.asset_id
             AND sa.component_fid = ue.target_fid
            WHERE ue.target_guid IS NOT NULL
              AND ue.method_name = $methodName
              AND sa.script_guid = $scriptGuid
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$scriptGuid", scriptGuid.ToByteArray());
        cmd.Parameters.AddWithValue("$methodName", methodName);

        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Returns <c>script_attachments</c> for <paramref name="assetId"/> whose <c>script_guid</c>
    /// does not exist in the <c>assets</c> table.
    /// </summary>
    public MissingScriptAttachment[] GetMissingScriptAttachments(int assetId)
    {
        ThrowIfDisposed();

        var results = new List<MissingScriptAttachment>();
        byte[] guidBuffer = new byte[16];

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT sa.component_fid, sa.game_object_fid, sa.game_object_name, sa.script_guid, sa.line_number
            FROM script_attachments sa
            LEFT JOIN assets a ON a.guid = sa.script_guid
            WHERE sa.asset_id = $assetId
              AND a.asset_id IS NULL
            ORDER BY sa.line_number;
            """;
        cmd.Parameters.AddWithValue("$assetId", assetId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            AssetGuid scriptGuid = ReadGuid(reader, 3, guidBuffer);
            results.Add(new MissingScriptAttachment(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                scriptGuid,
                reader.GetInt32(4)));
        }

        return results.ToArray();
    }

    /// <summary>
    /// Resolves the <c>script_guid</c> of a <c>MonoBehaviour</c> component (<paramref name="componentFileId"/>)
    /// defined inside the asset identified by <paramref name="assetGuid"/>.
    /// </summary>
    public AssetGuid? GetComponentScriptGuid(AssetGuid assetGuid, long componentFileId)
    {
        ThrowIfDisposed();
        if (assetGuid.IsZero)
        {
            return null;
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT sa.script_guid
            FROM assets a
            JOIN script_attachments sa
              ON sa.asset_id = a.asset_id
             AND sa.component_fid = $compFid
            WHERE a.guid = $assetGuid
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$assetGuid", assetGuid.ToByteArray());
        cmd.Parameters.AddWithValue("$compFid", componentFileId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        byte[] guidBuffer = new byte[16];
        AssetGuid scriptGuid = ReadGuid(reader, 0, guidBuffer);
        return scriptGuid.IsZero ? null : scriptGuid;
    }

    /// <summary>
    /// Projects the lightweight graph tables from SQLite into an immutable <see cref="AssetGraphSnapshot"/>.
    /// </summary>
    internal AssetGraphSnapshot LoadGraphSnapshot()
    {
        ThrowIfDisposed();

        var assetsById = new Dictionary<int, AssetNode>();
        var assetIdByGuid = new Dictionary<AssetGuid, int>();
        var assetIdByRelPath = new Dictionary<string, int>(PathComparer);
        var scriptBuckets = new Dictionary<AssetGuid, List<int>>();
        var prefabBuckets = new Dictionary<AssetGuid, List<int>>();
        byte[] guidBuffer = new byte[16];

        using SqliteConnection connection = OpenConnection();

        using (SqliteCommand assetsCmd = connection.CreateCommand())
        {
            assetsCmd.CommandText = "SELECT asset_id, guid, rel_path, asset_kind FROM assets;";
            using SqliteDataReader reader = assetsCmd.ExecuteReader();
            while (reader.Read())
            {
                int assetId = reader.GetInt32(0);
                AssetGuid guid = ReadGuid(reader, 1, guidBuffer);
                string relPath = reader.GetString(2);
                var kind = (AssetKind)reader.GetInt32(3);

                var node = new AssetNode(assetId, guid, relPath, ToFullPath(relPath), kind);
                assetsById[assetId] = node;
                assetIdByGuid[guid] = assetId;
                assetIdByRelPath[relPath] = assetId;
            }
        }

        using (SqliteCommand scriptCmd = connection.CreateCommand())
        {
            scriptCmd.CommandText = "SELECT DISTINCT script_guid, asset_id FROM script_attachments;";
            using SqliteDataReader reader = scriptCmd.ExecuteReader();
            while (reader.Read())
            {
                AssetGuid scriptGuid = ReadGuid(reader, 0, guidBuffer);
                int assetId = reader.GetInt32(1);

                if (!scriptBuckets.TryGetValue(scriptGuid, out List<int>? list))
                {
                    list = new List<int>();
                    scriptBuckets[scriptGuid] = list;
                }
                list.Add(assetId);
            }
        }

        using (SqliteCommand prefabCmd = connection.CreateCommand())
        {
            prefabCmd.CommandText = "SELECT DISTINCT source_prefab_guid, asset_id FROM prefab_instances;";
            using SqliteDataReader reader = prefabCmd.ExecuteReader();
            while (reader.Read())
            {
                AssetGuid prefabGuid = ReadGuid(reader, 0, guidBuffer);
                int assetId = reader.GetInt32(1);

                if (!prefabBuckets.TryGetValue(prefabGuid, out List<int>? list))
                {
                    list = new List<int>();
                    prefabBuckets[prefabGuid] = list;
                }
                list.Add(assetId);
            }
        }

        var scriptToDirectAssetIds = new Dictionary<AssetGuid, int[]>(scriptBuckets.Count);
        foreach ((AssetGuid key, List<int> list) in scriptBuckets)
        {
            scriptToDirectAssetIds[key] = list.ToArray();
        }

        var prefabToInstantiatingAssetIds = new Dictionary<AssetGuid, int[]>(prefabBuckets.Count);
        foreach ((AssetGuid key, List<int> list) in prefabBuckets)
        {
            prefabToInstantiatingAssetIds[key] = list.ToArray();
        }

        return new AssetGraphSnapshot
        {
            AssetsById = assetsById,
            AssetIdByGuid = assetIdByGuid,
            AssetIdByRelPath = assetIdByRelPath,
            ScriptToDirectAssetIds = scriptToDirectAssetIds,
            PrefabGuidToInstantiatingAssetIds = prefabToInstantiatingAssetIds,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();

        using var conn = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(conn);
    }

    private void InitializeDatabase()
    {
        using SqliteConnection connection = new(_connectionString);
        connection.Open();

        ExecuteNonQuery(connection, """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA mmap_size = 268435456;
            """);

        bool needsRebuild = false;
        using (SqliteCommand checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'schema_meta' LIMIT 1;";
            object? tableExists = checkCmd.ExecuteScalar();
            if (tableExists is null)
            {
                needsRebuild = true;
            }
            else
            {
                using SqliteCommand verCmd = connection.CreateCommand();
                verCmd.CommandText = "SELECT version FROM schema_meta LIMIT 1;";
                object? versionObj = verCmd.ExecuteScalar();
                if (versionObj is null || Convert.ToInt32(versionObj) != CurrentSchemaVersion)
                {
                    needsRebuild = true;
                }
            }
        }

        if (!needsRebuild)
        {
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        ExecuteNonQuery(connection, transaction, """
            DROP TABLE IF EXISTS serialized_fields;
            DROP TABLE IF EXISTS unity_event_calls;
            DROP TABLE IF EXISTS prefab_modifications;
            DROP TABLE IF EXISTS prefab_instances;
            DROP TABLE IF EXISTS script_attachments;
            DROP TABLE IF EXISTS assets;
            DROP TABLE IF EXISTS schema_meta;

            CREATE TABLE schema_meta (
                version INTEGER NOT NULL
            );

            CREATE TABLE assets (
                asset_id    INTEGER PRIMARY KEY AUTOINCREMENT,
                guid        BLOB(16) NOT NULL UNIQUE,
                rel_path    TEXT NOT NULL UNIQUE,
                asset_kind  INTEGER NOT NULL,
                meta_mtime  INTEGER NOT NULL,
                asset_mtime INTEGER NOT NULL,
                asset_size  INTEGER NOT NULL
            );

            CREATE TABLE script_attachments (
                asset_id         INTEGER NOT NULL,
                component_fid    INTEGER NOT NULL,
                game_object_fid  INTEGER NOT NULL,
                game_object_name TEXT,
                script_guid      BLOB(16) NOT NULL,
                line_number      INTEGER NOT NULL,
                PRIMARY KEY (asset_id, component_fid)
            ) WITHOUT ROWID;
            CREATE INDEX idx_script_attachments_guid ON script_attachments(script_guid);

            CREATE TABLE prefab_instances (
                asset_id            INTEGER NOT NULL,
                prefab_instance_fid INTEGER NOT NULL,
                source_prefab_guid  BLOB(16) NOT NULL,
                line_number         INTEGER NOT NULL,
                PRIMARY KEY (asset_id, prefab_instance_fid)
            ) WITHOUT ROWID;
            CREATE INDEX idx_prefab_instances_source ON prefab_instances(source_prefab_guid);

            CREATE TABLE prefab_modifications (
                asset_id            INTEGER NOT NULL,
                prefab_instance_fid INTEGER NOT NULL,
                target_guid         BLOB(16) NOT NULL,
                target_fid          INTEGER NOT NULL,
                root_field_name     TEXT NOT NULL,
                property_path       TEXT NOT NULL,
                line_number         INTEGER NOT NULL
            );
            CREATE INDEX idx_modifications_target ON prefab_modifications(target_guid, target_fid, root_field_name);
            CREATE INDEX idx_modifications_asset ON prefab_modifications(asset_id);

            CREATE TABLE unity_event_calls (
                asset_id        INTEGER NOT NULL,
                component_fid   INTEGER NOT NULL,
                target_guid     BLOB(16),
                target_fid      INTEGER NOT NULL,
                method_name     TEXT NOT NULL,
                line_number     INTEGER NOT NULL
            );
            CREATE INDEX idx_unity_event_method ON unity_event_calls(method_name);
            CREATE INDEX idx_unity_event_asset ON unity_event_calls(asset_id);

            CREATE TABLE serialized_fields (
                asset_id        INTEGER NOT NULL,
                component_fid   INTEGER NOT NULL,
                field_name      TEXT NOT NULL,
                ref_guid        BLOB(16),
                ref_fid         INTEGER,
                line_number     INTEGER NOT NULL,
                PRIMARY KEY (asset_id, component_fid, field_name)
            ) WITHOUT ROWID;
            CREATE INDEX idx_serialized_fields_ref_guid ON serialized_fields(ref_guid) WHERE ref_guid IS NOT NULL;
            """);

        using (SqliteCommand insertVer = connection.CreateCommand())
        {
            insertVer.Transaction = transaction;
            insertVer.CommandText = "INSERT INTO schema_meta (version) VALUES ($ver);";
            insertVer.Parameters.AddWithValue("$ver", CurrentSchemaVersion);
            insertVer.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ExecuteNonQuery(connection, """
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA mmap_size = 268435456;
            """);
        return connection;
    }

    private void RemoveSingleAssetCore(string relPath)
    {
        using SqliteConnection connection = OpenConnection();
        int? assetId = FindAssetIdByRelPath(connection, relPath);
        if (!assetId.HasValue)
        {
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        using PreparedWriteCommands writers = new(connection, transaction);
        writers.DeleteAssetAndChildren(assetId.Value);
        transaction.Commit();

        Graph.RebuildFromStore(this);
    }

    private (Dictionary<string, long> MetaMap, Dictionary<string, (long LastWriteTicks, long Length)> AssetMap)
        EnumerateWorkspaceFiles(CancellationToken ct)
    {
        var metaMap = new Dictionary<string, long>(PathComparer);
        var assetMap = new Dictionary<string, (long LastWriteTicks, long Length)>(PathComparer);

        string assetsDir = Path.Combine(ProjectRoot, "Assets");
        string packagesDir = Path.Combine(ProjectRoot, "Packages");

        var searchRoots = new List<string>(2);
        if (Directory.Exists(assetsDir))
        {
            searchRoots.Add(assetsDir);
        }
        if (Directory.Exists(packagesDir))
        {
            searchRoots.Add(packagesDir);
        }
        if (searchRoots.Count == 0 && Directory.Exists(ProjectRoot))
        {
            searchRoots.Add(ProjectRoot);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };

        foreach (string root in searchRoots)
        {
            ct.ThrowIfCancellationRequested();

            var enumerable = new FileSystemEnumerable<(string FullPath, long LastWriteTicks, long Length, bool IsDirectory)>(
                root,
                static (ref FileSystemEntry entry) => (
                    entry.ToFullPath(),
                    entry.LastWriteTimeUtc.Ticks,
                    entry.IsDirectory ? 0L : entry.Length,
                    entry.IsDirectory),
                options)
            {
                ShouldRecursePredicate = static (ref FileSystemEntry entry) =>
                    entry.IsDirectory && !IsIgnoredDirectoryName(entry.FileName),
                ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
            };

            foreach ((string fullPath, long lastWriteTicks, long length, bool isDirectory) in enumerable)
            {
                ct.ThrowIfCancellationRequested();
                if (isDirectory)
                {
                    continue;
                }

                if (fullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    string assetPath = fullPath[..^5];
                    metaMap[assetPath] = lastWriteTicks;
                }
                else
                {
                    assetMap[fullPath] = (lastWriteTicks, length);
                }
            }
        }

        return (metaMap, assetMap);
    }

    private static Dictionary<string, ExistingAssetRow> LoadExistingAssets(SqliteConnection connection)
    {
        var existing = new Dictionary<string, ExistingAssetRow>(PathComparer);
        byte[] guidBuffer = new byte[16];

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT asset_id, guid, rel_path, asset_kind, meta_mtime, asset_mtime, asset_size FROM assets;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int assetId = reader.GetInt32(0);
            AssetGuid guid = ReadGuid(reader, 1, guidBuffer);
            string relPath = reader.GetString(2);
            var kind = (AssetKind)reader.GetInt32(3);
            long metaMtime = reader.GetInt64(4);
            long assetMtime = reader.GetInt64(5);
            long assetSize = reader.GetInt64(6);

            existing[relPath] = new ExistingAssetRow(assetId, guid, relPath, kind, metaMtime, assetMtime, assetSize);
        }

        return existing;
    }

    private static int? FindAssetIdByRelPath(SqliteConnection connection, string relPath)
    {
        using SqliteCommand exactCmd = connection.CreateCommand();
        exactCmd.CommandText = "SELECT asset_id FROM assets WHERE rel_path = $relPath LIMIT 1;";
        exactCmd.Parameters.AddWithValue("$relPath", relPath);
        object? exact = exactCmd.ExecuteScalar();
        if (exact is not null)
        {
            return Convert.ToInt32(exact);
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using SqliteCommand nocaseCmd = connection.CreateCommand();
        nocaseCmd.CommandText = "SELECT asset_id FROM assets WHERE rel_path = $relPath COLLATE NOCASE LIMIT 1;";
        nocaseCmd.Parameters.AddWithValue("$relPath", relPath);
        object? nocase = nocaseCmd.ExecuteScalar();
        return nocase is not null ? Convert.ToInt32(nocase) : null;
    }

    private static int? FindAssetIdByGuid(SqliteConnection connection, AssetGuid guid)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT asset_id FROM assets WHERE guid = $guid LIMIT 1;";
        cmd.Parameters.AddWithValue("$guid", guid.ToByteArray());
        object? result = cmd.ExecuteScalar();
        return result is not null ? Convert.ToInt32(result) : null;
    }

    private AssetEntryDto? QueryAssetByRelPath(SqliteConnection connection, string relPath, bool ignoreCase)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = ignoreCase
            ? "SELECT asset_id, guid, rel_path, asset_kind FROM assets WHERE rel_path = $relPath COLLATE NOCASE LIMIT 1;"
            : "SELECT asset_id, guid, rel_path, asset_kind FROM assets WHERE rel_path = $relPath LIMIT 1;";
        cmd.Parameters.AddWithValue("$relPath", relPath);

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAssetEntry(reader) : null;
    }

    private AssetEntryDto ReadAssetEntry(SqliteDataReader reader)
    {
        byte[] guidBuffer = new byte[16];
        AssetGuid guid = ReadGuid(reader, 1, guidBuffer);
        string relPath = reader.GetString(2);

        return new AssetEntryDto
        {
            AssetId = reader.GetInt32(0),
            Guid = guid.ToHexString(),
            RelativePath = relPath,
            FullPath = ToFullPath(relPath),
            Kind = (AssetKind)reader.GetInt32(3),
        };
    }

    private static AssetGuid ReadGuid(SqliteDataReader reader, int ordinal, byte[] buffer)
    {
        long read = reader.GetBytes(ordinal, 0, buffer, 0, 16);
        return read == 16 ? AssetGuid.FromBytes(buffer) : AssetGuid.Zero;
    }

    private static string NormalizeAssetFullPath(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath);
        return normalized.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^5]
            : normalized;
    }

    private string ToRelativePath(string fullPath)
    {
        return Path.GetRelativePath(ProjectRoot, fullPath).Replace('\\', '/');
    }

    private string ToFullPath(string relPath)
    {
        return Path.GetFullPath(Path.Combine(ProjectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private bool TryGetRelativePathInsideWorkspace(string fullPath, out string relPath)
    {
        relPath = Path.GetRelativePath(ProjectRoot, fullPath).Replace('\\', '/');
        if (string.IsNullOrEmpty(relPath) ||
            relPath == "." ||
            relPath == ".." ||
            relPath.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathRooted(relPath))
        {
            return false;
        }

        return true;
    }

    private bool TryNormalizeQueryRelPath(string relativeOrFullPath, out string relPath)
    {
        if (Path.IsPathFullyQualified(relativeOrFullPath))
        {
            return TryGetRelativePathInsideWorkspace(Path.GetFullPath(relativeOrFullPath), out relPath);
        }

        relPath = relativeOrFullPath.Replace('\\', '/');
        while (relPath.StartsWith("./", StringComparison.Ordinal))
        {
            relPath = relPath[2..];
        }
        relPath = relPath.TrimStart('/');
        return !string.IsNullOrEmpty(relPath);
    }

    private static bool IsIgnoredDirectoryName(ReadOnlySpan<char> name)
    {
        foreach (string ignored in IgnoredDirectoryNames)
        {
            if (name.Equals(ignored.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsUnderIgnoredDirectory(string relPath)
    {
        int start = 0;
        while (start < relPath.Length)
        {
            int slash = relPath.IndexOf('/', start);
            if (slash < 0)
            {
                break;
            }

            ReadOnlySpan<char> segment = relPath.AsSpan(start, slash - start);
            if (IsIgnoredDirectoryName(segment))
            {
                return true;
            }

            start = slash + 1;
        }

        return false;
    }

    private static AssetKind ClassifyAssetKind(string fullPath)
    {
        string ext = Path.GetExtension(fullPath);
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return AssetKind.CSharpScript;
        if (ext.Equals(".prefab", StringComparison.OrdinalIgnoreCase)) return AssetKind.Prefab;
        if (ext.Equals(".unity", StringComparison.OrdinalIgnoreCase)) return AssetKind.Scene;
        if (ext.Equals(".asset", StringComparison.OrdinalIgnoreCase)) return AssetKind.ScriptableObject;
        return AssetKind.OtherAsset;
    }

    private static bool ShouldScanYaml(AssetKind kind) =>
        kind is AssetKind.Prefab or AssetKind.Scene or AssetKind.ScriptableObject;

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record ExistingAssetRow(
        int AssetId,
        AssetGuid Guid,
        string RelPath,
        AssetKind Kind,
        long MetaMtime,
        long AssetMtime,
        long AssetSize);

    private sealed record PendingAssetUpsert(
        int? ExistingAssetId,
        AssetGuid Guid,
        string RelPath,
        AssetKind Kind,
        long MetaMtime,
        long AssetMtime,
        long AssetSize,
        YamlScanResult? ScanResult);

    private sealed class PreparedWriteCommands : IDisposable
    {
        private readonly SqliteCommand _deleteScriptAttachments;
        private readonly SqliteCommand _deletePrefabInstances;
        private readonly SqliteCommand _deletePrefabModifications;
        private readonly SqliteCommand _deleteUnityEventCalls;
        private readonly SqliteCommand _deleteSerializedFields;
        private readonly SqliteCommand _deleteAsset;

        private readonly SqliteCommand _insertAsset;
        private readonly SqliteCommand _updateAsset;
        private readonly SqliteCommand _insertScriptAttachment;
        private readonly SqliteCommand _insertPrefabInstance;
        private readonly SqliteCommand _insertPrefabModification;
        private readonly SqliteCommand _insertUnityEventCall;
        private readonly SqliteCommand _insertSerializedField;

        public PreparedWriteCommands(SqliteConnection connection, SqliteTransaction transaction)
        {
            _deleteScriptAttachments = CreatePrepared(connection, transaction, "DELETE FROM script_attachments WHERE asset_id = $assetId;", ("$assetId", SqliteType.Integer));
            _deletePrefabInstances = CreatePrepared(connection, transaction, "DELETE FROM prefab_instances WHERE asset_id = $assetId;", ("$assetId", SqliteType.Integer));
            _deletePrefabModifications = CreatePrepared(connection, transaction, "DELETE FROM prefab_modifications WHERE asset_id = $assetId;", ("$assetId", SqliteType.Integer));
            _deleteUnityEventCalls = CreatePrepared(connection, transaction, "DELETE FROM unity_event_calls WHERE asset_id = $assetId;", ("$assetId", SqliteType.Integer));
            _deleteSerializedFields = CreatePrepared(connection, transaction, "DELETE FROM serialized_fields WHERE asset_id = $assetId;", ("$assetId", SqliteType.Integer));
            _deleteAsset = CreatePrepared(connection, transaction, "DELETE FROM assets WHERE asset_id = $assetId;", ("$assetId", SqliteType.Integer));

            _insertAsset = CreatePrepared(
                connection,
                transaction,
                """
                INSERT INTO assets (guid, rel_path, asset_kind, meta_mtime, asset_mtime, asset_size)
                VALUES ($guid, $relPath, $kind, $metaMtime, $assetMtime, $assetSize)
                RETURNING asset_id;
                """,
                ("$guid", SqliteType.Blob),
                ("$relPath", SqliteType.Text),
                ("$kind", SqliteType.Integer),
                ("$metaMtime", SqliteType.Integer),
                ("$assetMtime", SqliteType.Integer),
                ("$assetSize", SqliteType.Integer));

            _updateAsset = CreatePrepared(
                connection,
                transaction,
                """
                UPDATE assets
                SET guid = $guid,
                    rel_path = $relPath,
                    asset_kind = $kind,
                    meta_mtime = $metaMtime,
                    asset_mtime = $assetMtime,
                    asset_size = $assetSize
                WHERE asset_id = $assetId;
                """,
                ("$assetId", SqliteType.Integer),
                ("$guid", SqliteType.Blob),
                ("$relPath", SqliteType.Text),
                ("$kind", SqliteType.Integer),
                ("$metaMtime", SqliteType.Integer),
                ("$assetMtime", SqliteType.Integer),
                ("$assetSize", SqliteType.Integer));

            _insertScriptAttachment = CreatePrepared(
                connection,
                transaction,
                """
                INSERT OR REPLACE INTO script_attachments (asset_id, component_fid, game_object_fid, game_object_name, script_guid, line_number)
                VALUES ($assetId, $compFid, $goFid, $goName, $scriptGuid, $line);
                """,
                ("$assetId", SqliteType.Integer),
                ("$compFid", SqliteType.Integer),
                ("$goFid", SqliteType.Integer),
                ("$goName", SqliteType.Text),
                ("$scriptGuid", SqliteType.Blob),
                ("$line", SqliteType.Integer));

            _insertPrefabInstance = CreatePrepared(
                connection,
                transaction,
                """
                INSERT OR REPLACE INTO prefab_instances (asset_id, prefab_instance_fid, source_prefab_guid, line_number)
                VALUES ($assetId, $piFid, $sourceGuid, $line);
                """,
                ("$assetId", SqliteType.Integer),
                ("$piFid", SqliteType.Integer),
                ("$sourceGuid", SqliteType.Blob),
                ("$line", SqliteType.Integer));

            _insertPrefabModification = CreatePrepared(
                connection,
                transaction,
                """
                INSERT INTO prefab_modifications (asset_id, prefab_instance_fid, target_guid, target_fid, root_field_name, property_path, line_number)
                VALUES ($assetId, $piFid, $targetGuid, $targetFid, $rootField, $propPath, $line);
                """,
                ("$assetId", SqliteType.Integer),
                ("$piFid", SqliteType.Integer),
                ("$targetGuid", SqliteType.Blob),
                ("$targetFid", SqliteType.Integer),
                ("$rootField", SqliteType.Text),
                ("$propPath", SqliteType.Text),
                ("$line", SqliteType.Integer));

            _insertUnityEventCall = CreatePrepared(
                connection,
                transaction,
                """
                INSERT INTO unity_event_calls (asset_id, component_fid, target_guid, target_fid, method_name, line_number)
                VALUES ($assetId, $compFid, $targetGuid, $targetFid, $methodName, $line);
                """,
                ("$assetId", SqliteType.Integer),
                ("$compFid", SqliteType.Integer),
                ("$targetGuid", SqliteType.Blob),
                ("$targetFid", SqliteType.Integer),
                ("$methodName", SqliteType.Text),
                ("$line", SqliteType.Integer));

            _insertSerializedField = CreatePrepared(
                connection,
                transaction,
                """
                INSERT OR REPLACE INTO serialized_fields (asset_id, component_fid, field_name, ref_guid, ref_fid, line_number)
                VALUES ($assetId, $compFid, $fieldName, $refGuid, $refFid, $line);
                """,
                ("$assetId", SqliteType.Integer),
                ("$compFid", SqliteType.Integer),
                ("$fieldName", SqliteType.Text),
                ("$refGuid", SqliteType.Blob),
                ("$refFid", SqliteType.Integer),
                ("$line", SqliteType.Integer));
        }

        public void DeleteAssetAndChildren(int assetId)
        {
            DeleteChildren(assetId);
            _deleteAsset.Parameters["$assetId"].Value = assetId;
            _deleteAsset.ExecuteNonQuery();
        }

        public int UpsertAssetAndChildren(PendingAssetUpsert upsert)
        {
            byte[] guidBytes = upsert.Guid.ToByteArray();
            int assetId;

            if (upsert.ExistingAssetId.HasValue)
            {
                assetId = upsert.ExistingAssetId.Value;
                DeleteChildren(assetId);

                _updateAsset.Parameters["$assetId"].Value = assetId;
                _updateAsset.Parameters["$guid"].Value = guidBytes;
                _updateAsset.Parameters["$relPath"].Value = upsert.RelPath;
                _updateAsset.Parameters["$kind"].Value = (int)upsert.Kind;
                _updateAsset.Parameters["$metaMtime"].Value = upsert.MetaMtime;
                _updateAsset.Parameters["$assetMtime"].Value = upsert.AssetMtime;
                _updateAsset.Parameters["$assetSize"].Value = upsert.AssetSize;
                _updateAsset.ExecuteNonQuery();
            }
            else
            {
                _insertAsset.Parameters["$guid"].Value = guidBytes;
                _insertAsset.Parameters["$relPath"].Value = upsert.RelPath;
                _insertAsset.Parameters["$kind"].Value = (int)upsert.Kind;
                _insertAsset.Parameters["$metaMtime"].Value = upsert.MetaMtime;
                _insertAsset.Parameters["$assetMtime"].Value = upsert.AssetMtime;
                _insertAsset.Parameters["$assetSize"].Value = upsert.AssetSize;
                assetId = Convert.ToInt32(_insertAsset.ExecuteScalar());
            }

            YamlScanResult? scan = upsert.ScanResult;
            if (scan is null || !scan.IsValidYaml)
            {
                return assetId;
            }

            foreach (ScannedScriptAttachment att in scan.ScriptAttachments)
            {
                _insertScriptAttachment.Parameters["$assetId"].Value = assetId;
                _insertScriptAttachment.Parameters["$compFid"].Value = att.ComponentFileId;
                _insertScriptAttachment.Parameters["$goFid"].Value = att.GameObjectFileId;
                _insertScriptAttachment.Parameters["$goName"].Value = (object?)att.GameObjectName ?? DBNull.Value;
                _insertScriptAttachment.Parameters["$scriptGuid"].Value = att.ScriptGuid.ToByteArray();
                _insertScriptAttachment.Parameters["$line"].Value = att.LineNumber;
                _insertScriptAttachment.ExecuteNonQuery();
            }

            foreach (ScannedPrefabInstance pi in scan.PrefabInstances)
            {
                _insertPrefabInstance.Parameters["$assetId"].Value = assetId;
                _insertPrefabInstance.Parameters["$piFid"].Value = pi.PrefabInstanceFileId;
                _insertPrefabInstance.Parameters["$sourceGuid"].Value = pi.SourcePrefabGuid.ToByteArray();
                _insertPrefabInstance.Parameters["$line"].Value = pi.LineNumber;
                _insertPrefabInstance.ExecuteNonQuery();
            }

            foreach (ScannedPrefabModification mod in scan.PrefabModifications)
            {
                _insertPrefabModification.Parameters["$assetId"].Value = assetId;
                _insertPrefabModification.Parameters["$piFid"].Value = mod.PrefabInstanceFileId;
                _insertPrefabModification.Parameters["$targetGuid"].Value = mod.TargetGuid.ToByteArray();
                _insertPrefabModification.Parameters["$targetFid"].Value = mod.TargetFileId;
                _insertPrefabModification.Parameters["$rootField"].Value = mod.RootFieldName;
                _insertPrefabModification.Parameters["$propPath"].Value = mod.PropertyPath;
                _insertPrefabModification.Parameters["$line"].Value = mod.LineNumber;
                _insertPrefabModification.ExecuteNonQuery();
            }

            foreach (ScannedUnityEventCall ue in scan.UnityEventCalls)
            {
                _insertUnityEventCall.Parameters["$assetId"].Value = assetId;
                _insertUnityEventCall.Parameters["$compFid"].Value = ue.ComponentFileId;
                _insertUnityEventCall.Parameters["$targetGuid"].Value = ue.TargetGuid.HasValue
                    ? ue.TargetGuid.Value.ToByteArray()
                    : DBNull.Value;
                _insertUnityEventCall.Parameters["$targetFid"].Value = ue.TargetFileId;
                _insertUnityEventCall.Parameters["$methodName"].Value = ue.MethodName;
                _insertUnityEventCall.Parameters["$line"].Value = ue.LineNumber;
                _insertUnityEventCall.ExecuteNonQuery();
            }

            foreach (ScannedSerializedField sf in scan.SerializedFields)
            {
                _insertSerializedField.Parameters["$assetId"].Value = assetId;
                _insertSerializedField.Parameters["$compFid"].Value = sf.ComponentFileId;
                _insertSerializedField.Parameters["$fieldName"].Value = sf.FieldName;
                _insertSerializedField.Parameters["$refGuid"].Value = sf.RefGuid.HasValue
                    ? sf.RefGuid.Value.ToByteArray()
                    : DBNull.Value;
                _insertSerializedField.Parameters["$refFid"].Value = sf.RefFileId.HasValue
                    ? sf.RefFileId.Value
                    : DBNull.Value;
                _insertSerializedField.Parameters["$line"].Value = sf.LineNumber;
                _insertSerializedField.ExecuteNonQuery();
            }

            return assetId;
        }

        public void Dispose()
        {
            _deleteScriptAttachments.Dispose();
            _deletePrefabInstances.Dispose();
            _deletePrefabModifications.Dispose();
            _deleteUnityEventCalls.Dispose();
            _deleteSerializedFields.Dispose();
            _deleteAsset.Dispose();
            _insertAsset.Dispose();
            _updateAsset.Dispose();
            _insertScriptAttachment.Dispose();
            _insertPrefabInstance.Dispose();
            _insertPrefabModification.Dispose();
            _insertUnityEventCall.Dispose();
            _insertSerializedField.Dispose();
        }

        private void DeleteChildren(int assetId)
        {
            ExecuteDeleteByAssetId(_deleteScriptAttachments, assetId);
            ExecuteDeleteByAssetId(_deletePrefabInstances, assetId);
            ExecuteDeleteByAssetId(_deletePrefabModifications, assetId);
            ExecuteDeleteByAssetId(_deleteUnityEventCalls, assetId);
            ExecuteDeleteByAssetId(_deleteSerializedFields, assetId);
        }

        private static void ExecuteDeleteByAssetId(SqliteCommand cmd, int assetId)
        {
            cmd.Parameters["$assetId"].Value = assetId;
            cmd.ExecuteNonQuery();
        }

        private static SqliteCommand CreatePrepared(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql,
            params (string Name, SqliteType Type)[] parameters)
        {
            SqliteCommand cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            foreach ((string name, SqliteType type) in parameters)
            {
                cmd.Parameters.Add(new SqliteParameter(name, type) { Value = DBNull.Value });
            }
            cmd.Prepare();
            return cmd;
        }
    }
}
