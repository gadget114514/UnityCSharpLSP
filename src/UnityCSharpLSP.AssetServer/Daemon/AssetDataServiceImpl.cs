using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Parsing;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Daemon;

/// <summary>
/// Concrete implementation of <see cref="IAssetDataService"/> backed by <see cref="AssetSqliteStore"/>,
/// <see cref="InMemoryAssetGraph"/>, <see cref="HierarchyBuilder"/>, <see cref="BuildSettingsParser"/>,
/// and a debounced <see cref="AssetFileWatcher"/>.
/// </summary>
public sealed class AssetDataServiceImpl : IAssetDataService, IDisposable
{
    private const int ClassIdMonoBehaviour = 114;
    private const int ClassIdPrefabInstance = 1001;
    private const int MaxRenameCandidateResults = 1_000_000;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> BuiltInMonoBehaviourKeys = new(StringComparer.Ordinal)
    {
        "m_ObjectHideFlags",
        "m_CorrespondingSourceObject",
        "m_PrefabInstance",
        "m_PrefabAsset",
        "m_GameObject",
        "m_Enabled",
        "m_EditorHideFlags",
        "m_Script",
        "m_Name",
        "m_EditorClassIdentifier",
        "serializedVersion",
    };

    private static readonly HashSet<string> CSharpVisibilityOrFieldAttributes = new(StringComparer.Ordinal)
    {
        "public",
        "private",
        "protected",
        "internal",
        "readonly",
        "SerializeField",
        "SerializeReference",
    };

    private readonly AssetSqliteStore _store;
    private readonly InMemoryAssetGraph _graph;
    private readonly AssetFileWatcher? _watcher;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private volatile string _state = "Indexing";
    private bool _disposed;

    public string ProjectRoot => _store.ProjectRoot;
    public string DatabasePath => _store.DatabasePath;
    public AssetSqliteStore Store => _store;
    public InMemoryAssetGraph Graph => _graph;

    /// <summary>
    /// Raised when <see cref="ShutdownAsync"/> is invoked.
    /// </summary>
    public event EventHandler? ShutdownRequested;

    public AssetDataServiceImpl(
        string projectRoot,
        string? databasePath = null,
        bool enableFileWatcher = true,
        TimeSpan? watcherDebounceWindow = null)
    {
        _graph = new InMemoryAssetGraph();
        _store = new AssetSqliteStore(projectRoot, databasePath, _graph);
        _watcher = enableFileWatcher
            ? new AssetFileWatcher(_store, watcherDebounceWindow)
            : null;
    }

    /// <summary>
    /// Performs initial incremental synchronization of the workspace and starts the file watcher (if enabled).
    /// </summary>
    public async Task InitializeAndSyncAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _state = "Indexing";
            await _store.SyncWorkspaceAsync(ct).ConfigureAwait(false);
            _watcher?.Start();
            _state = "Ready";
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public Task<DaemonHandshakeResponse> HandshakeAsync(DaemonHandshakeRequest request, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        string version = typeof(AssetDataServiceImpl).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        return Task.FromResult(new DaemonHandshakeResponse
        {
            DaemonVersion = version,
            DaemonPid = Environment.ProcessId,
            ProjectRoot = _store.ProjectRoot,
            State = _state,
        });
    }

    public Task<DaemonStatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        AssetStatusCounts counts = _store.GetStatusCounts();
        return Task.FromResult(new DaemonStatusResponse
        {
            State = _state,
            TotalAssets = counts.TotalAssets,
            TotalScripts = counts.TotalScripts,
            TotalPrefabs = counts.TotalPrefabs,
            TotalScenes = counts.TotalScenes,
            DatabasePath = _store.DatabasePath,
        });
    }

    public Task<AssetEntryDto?> GetAssetByGuidAsync(string guidHex, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (!AssetGuid.TryParseHex(guidHex, out AssetGuid guid) || guid.IsZero)
        {
            return Task.FromResult<AssetEntryDto?>(null);
        }

        return Task.FromResult(ResolveAssetByGuid(guid));
    }

    public Task<AssetEntryDto?> GetAssetByPathAsync(string filePath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(_store.GetAssetByPath(filePath));
    }

    public Task<ScriptReferenceCountDto[]> GetScriptReferenceCountsAsync(string[] scriptGuids, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (scriptGuids is null || scriptGuids.Length == 0)
        {
            return Task.FromResult(Array.Empty<ScriptReferenceCountDto>());
        }

        return Task.FromResult(_graph.GetScriptReferenceCounts(scriptGuids));
    }

    public Task<AssetReferenceResultDto> FindReferencesAsync(AssetReferenceQueryDto query, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (query is null ||
            !AssetGuid.TryParseHex(query.ScriptGuid, out AssetGuid scriptGuid) ||
            scriptGuid.IsZero)
        {
            return Task.FromResult(new AssetReferenceResultDto());
        }

        int maxResults = query.MaxResults > 0 ? Math.Min(query.MaxResults, int.MaxValue - 1) : 500;

        AssetReferenceResultDto result = query.MemberKind switch
        {
            AssetMemberKind.Class => QueryClassReferences(scriptGuid, query.IncludeTransitiveScenes, maxResults),
            AssetMemberKind.Field => _store.QueryFieldReferences(scriptGuid, query.MemberName ?? string.Empty, maxResults),
            AssetMemberKind.Method => _store.QueryMethodReferences(scriptGuid, query.MemberName ?? string.Empty, maxResults),
            _ => new AssetReferenceResultDto(),
        };

        return Task.FromResult(result);
    }

    public Task<AssetWorkspaceEditDto> ComputeRenameEditsAsync(AssetRenameQueryDto query, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (query is null ||
            query.MemberKind == AssetMemberKind.Class ||
            string.IsNullOrEmpty(query.OldName) ||
            string.IsNullOrEmpty(query.NewName) ||
            string.Equals(query.OldName, query.NewName, StringComparison.Ordinal) ||
            !AssetGuid.TryParseHex(query.ScriptGuid, out AssetGuid scriptGuid) ||
            scriptGuid.IsZero)
        {
            return Task.FromResult(new AssetWorkspaceEditDto());
        }

        AssetReferenceResultDto candidates = query.MemberKind switch
        {
            AssetMemberKind.Field => _store.QueryFieldReferences(scriptGuid, query.OldName, MaxRenameCandidateResults),
            AssetMemberKind.Method => _store.QueryMethodReferences(scriptGuid, query.OldName, MaxRenameCandidateResults),
            _ => new AssetReferenceResultDto(),
        };

        if (candidates.Locations.Length == 0)
        {
            return Task.FromResult(new AssetWorkspaceEditDto());
        }

        var groupedByFile = new Dictionary<string, List<AssetReferenceLocationDto>>(PathComparer);
        foreach (AssetReferenceLocationDto loc in candidates.Locations)
        {
            if (string.IsNullOrWhiteSpace(loc.FullPath))
            {
                continue;
            }

            if (!groupedByFile.TryGetValue(loc.FullPath, out List<AssetReferenceLocationDto>? list))
            {
                list = new List<AssetReferenceLocationDto>();
                groupedByFile[loc.FullPath] = list;
            }

            list.Add(loc);
        }

        var fileEdits = new List<AssetFileEditDto>(groupedByFile.Count);
        foreach ((string fullPath, List<AssetReferenceLocationDto> locations) in groupedByFile)
        {
            ct.ThrowIfCancellationRequested();

            string[]? lines = TryReadAllLines(fullPath);
            if (lines is null || lines.Length == 0)
            {
                continue;
            }

            var edits = new List<AssetTextEditDto>(locations.Count);
            var seen = new HashSet<(int Line, int Start, int End)>();

            foreach (AssetReferenceLocationDto loc in locations)
            {
                if ((uint)loc.Line >= (uint)lines.Length)
                {
                    continue;
                }

                string lineText = lines[loc.Line];
                bool matched = query.MemberKind switch
                {
                    AssetMemberKind.Field when !loc.IsOverride =>
                        TryFindDirectSerializedFieldSpan(lineText, query.OldName, out int startChar, out int endChar)
                            ? AddEdit(edits, seen, loc.Line, startChar, endChar, query.NewName)
                            : false,
                    AssetMemberKind.Field when loc.IsOverride =>
                        TryFindPropertyPathRootSpan(lineText, query.OldName, out int startChar, out int endChar)
                            ? AddEdit(edits, seen, loc.Line, startChar, endChar, query.NewName)
                            : false,
                    AssetMemberKind.Method =>
                        TryFindUnityEventMethodSpan(lineText, query.OldName, out int startChar, out int endChar)
                            ? AddEdit(edits, seen, loc.Line, startChar, endChar, query.NewName)
                            : false,
                    _ => false,
                };
                _ = matched;
            }

            if (edits.Count > 0)
            {
                edits.Sort(static (a, b) =>
                {
                    int lineCmp = a.Line.CompareTo(b.Line);
                    return lineCmp != 0 ? lineCmp : a.StartCharacter.CompareTo(b.StartCharacter);
                });

                fileEdits.Add(new AssetFileEditDto
                {
                    FullPath = fullPath,
                    Edits = edits.ToArray(),
                });
            }
        }

        fileEdits.Sort((a, b) => PathComparer.Compare(a.FullPath, b.FullPath));
        return Task.FromResult(new AssetWorkspaceEditDto
        {
            FileEdits = fileEdits.ToArray(),
        });
    }

    public Task<AssetDocumentLinksDto> GetDocumentLinksAsync(string assetPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        string? fullPath = ResolveExistingFilePath(assetPath);
        if (fullPath is null)
        {
            return Task.FromResult(new AssetDocumentLinksDto());
        }

        List<ScannedGuidOccurrence> occurrences = FastUnityYamlScanner.ScanGuidOccurrences(fullPath);
        if (occurrences.Count == 0)
        {
            return Task.FromResult(new AssetDocumentLinksDto());
        }

        var links = new List<AssetDocumentLinkItemDto>(occurrences.Count);
        foreach (ScannedGuidOccurrence occ in occurrences)
        {
            ct.ThrowIfCancellationRequested();

            AssetEntryDto? target = ResolveAssetByGuid(occ.Guid);
            if (target is null)
            {
                continue;
            }

            links.Add(new AssetDocumentLinkItemDto
            {
                Line = occ.Line,
                StartCharacter = occ.StartCharacter,
                EndCharacter = occ.EndCharacter,
                TargetFullPath = target.FullPath,
                TargetGuid = target.Guid,
            });
        }

        return Task.FromResult(new AssetDocumentLinksDto
        {
            Links = links.ToArray(),
        });
    }

    public Task<AssetHoverDto?> GetHoverAsync(string assetPath, int line, int character, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (line < 0 || character < 0)
        {
            return Task.FromResult<AssetHoverDto?>(null);
        }

        string? fullPath = ResolveExistingFilePath(assetPath);
        if (fullPath is null)
        {
            return Task.FromResult<AssetHoverDto?>(null);
        }

        ScannedGuidOccurrence? guidHit = FindGuidOccurrenceAt(fullPath, line, character);
        if (guidHit is not null)
        {
            AssetEntryDto? target = ResolveAssetByGuid(guidHit.Guid);
            if (target is null)
            {
                return Task.FromResult<AssetHoverDto?>(new AssetHoverDto
                {
                    Markdown = $"Missing Unity asset reference (`{guidHit.Guid.ToHexString()}`)",
                    TargetFullPath = null,
                });
            }

            string fileName = Path.GetFileName(target.RelativePath);
            string markdown = $"""
                **{fileName}** (`{target.Kind}`)

                - Path: `{target.RelativePath}`
                - GUID: `{target.Guid}`
                """;

            return Task.FromResult<AssetHoverDto?>(new AssetHoverDto
            {
                Markdown = markdown,
                TargetFullPath = target.FullPath,
                ScriptGuid = target.Kind == AssetKind.CSharpScript ? target.Guid : null,
            });
        }

        if (TryResolveFieldContextAtPosition(fullPath, line, character, out AssetGuid scriptGuid, out string fieldName))
        {
            AssetEntryDto? scriptEntry = ResolveAssetByGuid(scriptGuid);
            string scriptName = scriptEntry is not null
                ? Path.GetFileNameWithoutExtension(scriptEntry.RelativePath)
                : scriptGuid.ToHexString();

            string markdown = scriptEntry is not null
                ? $"""
                  **{scriptName}.{fieldName}**

                  - Script: `{scriptEntry.RelativePath}`
                  - Field: `{fieldName}`
                  - GUID: `{scriptEntry.Guid}`
                  """
                : $"""
                  **{fieldName}**

                  - Field: `{fieldName}`
                  - Script GUID: `{scriptGuid.ToHexString()}`
                  """;

            return Task.FromResult<AssetHoverDto?>(new AssetHoverDto
            {
                Markdown = markdown,
                TargetFullPath = scriptEntry?.FullPath,
                ScriptGuid = scriptGuid.ToHexString(),
                FieldName = fieldName,
            });
        }

        return Task.FromResult<AssetHoverDto?>(null);
    }

    public Task<AssetLocationDto?> GetDefinitionAsync(string assetPath, int line, int character, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (line < 0 || character < 0)
        {
            return Task.FromResult<AssetLocationDto?>(null);
        }

        string? fullPath = ResolveExistingFilePath(assetPath);
        if (fullPath is null)
        {
            return Task.FromResult<AssetLocationDto?>(null);
        }

        ScannedGuidOccurrence? guidHit = FindGuidOccurrenceAt(fullPath, line, character);
        if (guidHit is not null)
        {
            AssetEntryDto? target = ResolveAssetByGuid(guidHit.Guid);
            if (target is null)
            {
                return Task.FromResult<AssetLocationDto?>(null);
            }

            return Task.FromResult<AssetLocationDto?>(new AssetLocationDto
            {
                FullPath = target.FullPath,
                Line = 0,
                Character = 0,
            });
        }

        if (TryResolveFieldContextAtPosition(fullPath, line, character, out AssetGuid scriptGuid, out string fieldName))
        {
            AssetEntryDto? scriptEntry = ResolveAssetByGuid(scriptGuid);
            if (scriptEntry is null)
            {
                return Task.FromResult<AssetLocationDto?>(null);
            }

            TryFindCSharpFieldLocation(scriptEntry.FullPath, fieldName, out int csLine, out int csChar);
            return Task.FromResult<AssetLocationDto?>(new AssetLocationDto
            {
                FullPath = scriptEntry.FullPath,
                Line = csLine,
                Character = csChar,
            });
        }

        return Task.FromResult<AssetLocationDto?>(null);
    }

    public Task<AssetHierarchyNodeDto[]> GetHierarchyAsync(
        string assetPath,
        int maxDepth,
        bool includeSerializedValues,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        string? fullPath = ResolveExistingFilePath(assetPath);
        if (fullPath is null)
        {
            return Task.FromResult(Array.Empty<AssetHierarchyNodeDto>());
        }

        AssetHierarchyNodeDto[] hierarchy = HierarchyBuilder.BuildHierarchy(
            fullPath,
            maxDepth,
            includeSerializedValues,
            ResolveHierarchyAsset);

        return Task.FromResult(hierarchy);
    }

    public Task<BuildSceneDto[]> GetBuildScenesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(BuildSettingsParser.Parse(_store.ProjectRoot));
    }

    public Task<AssetDiagnosticDto[]> GetAssetDiagnosticsAsync(string assetPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        string? fullPath = ResolveExistingFilePath(assetPath);
        if (fullPath is null)
        {
            return Task.FromResult(Array.Empty<AssetDiagnosticDto>());
        }

        string[]? lines = TryReadAllLines(fullPath);
        AssetEntryDto? entry = _store.GetAssetByPath(fullPath);
        var diagnostics = new List<AssetDiagnosticDto>();

        if (entry is not null)
        {
            MissingScriptAttachment[] missing = _store.GetMissingScriptAttachments(entry.AssetId);
            foreach (MissingScriptAttachment item in missing)
            {
                diagnostics.Add(CreateMissingScriptDiagnostic(
                    item.ScriptGuid,
                    item.GameObjectName,
                    item.LineNumber,
                    lines));
            }
        }
        else
        {
            YamlScanResult scan = FastUnityYamlScanner.ScanFile(fullPath);
            if (scan.IsValidYaml)
            {
                foreach (ScannedScriptAttachment att in scan.ScriptAttachments)
                {
                    if (!att.ScriptGuid.IsZero && ResolveAssetByGuid(att.ScriptGuid) is null)
                    {
                        diagnostics.Add(CreateMissingScriptDiagnostic(
                            att.ScriptGuid,
                            att.GameObjectName,
                            att.LineNumber,
                            lines));
                    }
                }
            }
        }

        return Task.FromResult(diagnostics.ToArray());
    }

    public Task<bool> IsMethodReferencedByUnityEventAsync(string scriptGuid, string methodName, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (!AssetGuid.TryParseHex(scriptGuid, out AssetGuid parsed) ||
            parsed.IsZero ||
            string.IsNullOrEmpty(methodName))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_store.IsMethodReferencedByUnityEvent(parsed, methodName));
    }

    public async Task<DaemonStatusResponse> RescanAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _state = "Indexing";
            await _store.SyncWorkspaceAsync(ct).ConfigureAwait(false);
            _state = "Ready";
        }
        finally
        {
            _syncLock.Release();
        }

        return await GetStatusAsync(ct).ConfigureAwait(false);
    }

    public Task ShutdownAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _watcher?.Dispose();
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _store.Dispose();
        _syncLock.Dispose();
    }

    private AssetReferenceResultDto QueryClassReferences(AssetGuid scriptGuid, bool includeTransitiveScenes, int maxResults)
    {
        TransitiveUsageResult usage = _graph.ResolveTransitiveUsages(scriptGuid);
        return _store.QueryClassReferences(scriptGuid, usage.AllPrefabGuidsInChain, includeTransitiveScenes, maxResults);
    }

    private AssetEntryDto? ResolveAssetByGuid(AssetGuid guid)
    {
        if (guid.IsZero)
        {
            return null;
        }

        AssetGraphSnapshot snapshot = _graph.Current;
        if (snapshot.AssetIdByGuid.TryGetValue(guid, out int assetId) &&
            snapshot.AssetsById.TryGetValue(assetId, out AssetNode node))
        {
            return new AssetEntryDto
            {
                AssetId = node.AssetId,
                Guid = node.Guid.ToHexString(),
                RelativePath = node.RelativePath,
                FullPath = node.FullPath,
                Kind = node.Kind,
            };
        }

        return _store.GetAssetByGuid(guid);
    }

    private (string? RelativePath, string? TypeName) ResolveHierarchyAsset(AssetGuid guid)
    {
        AssetEntryDto? entry = ResolveAssetByGuid(guid);
        if (entry is null)
        {
            return (null, null);
        }

        return (entry.RelativePath, Path.GetFileNameWithoutExtension(entry.RelativePath));
    }

    private string? ResolveExistingFilePath(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return null;
        }

        AssetEntryDto? indexed = _store.GetAssetByPath(assetPath);
        if (indexed is not null && File.Exists(indexed.FullPath))
        {
            return indexed.FullPath;
        }

        try
        {
            string candidate = Path.IsPathFullyQualified(assetPath)
                ? Path.GetFullPath(assetPath)
                : Path.GetFullPath(Path.Combine(_store.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));

            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ScannedGuidOccurrence? FindGuidOccurrenceAt(string fullPath, int line, int character)
    {
        List<ScannedGuidOccurrence> occurrences = FastUnityYamlScanner.ScanGuidOccurrences(fullPath);
        foreach (ScannedGuidOccurrence occ in occurrences)
        {
            if (occ.Line == line &&
                character >= occ.StartCharacter &&
                character <= occ.EndCharacter)
            {
                return occ;
            }
        }

        return null;
    }

    private bool TryResolveFieldContextAtPosition(
        string fullPath,
        int targetLine,
        int targetCharacter,
        out AssetGuid scriptGuid,
        out string fieldName)
    {
        scriptGuid = AssetGuid.Zero;
        fieldName = string.Empty;

        string[]? lines = TryReadAllLines(fullPath);
        if (lines is null || (uint)targetLine >= (uint)lines.Length)
        {
            return false;
        }

        if (lines.Length == 0 || !lines[0].StartsWith("%YAML", StringComparison.Ordinal))
        {
            return false;
        }

        int currentClassId = 0;
        bool isStripped = false;
        AssetGuid? currentMbScriptGuid = null;

        bool inModifications = false;
        int modificationsIndent = 0;
        bool hasPendingModTarget = false;
        long pendingModTargetFid = 0;
        AssetGuid pendingModTargetGuid = AssetGuid.Zero;

        int blockStartLine = 0;

        for (int i = 0; i <= targetLine; i++)
        {
            string line = lines[i];

            if (line.StartsWith("--- !u!", StringComparison.Ordinal))
            {
                currentClassId = 0;
                isStripped = false;
                currentMbScriptGuid = null;
                inModifications = false;
                hasPendingModTarget = false;
                pendingModTargetFid = 0;
                pendingModTargetGuid = AssetGuid.Zero;
                blockStartLine = i;

                byte[] headerBytes = Encoding.UTF8.GetBytes(line);
                UnityYamlSpanReader.TryParseDocumentHeader(headerBytes, out currentClassId, out _, out isStripped);
                continue;
            }

            if (isStripped)
            {
                continue;
            }

            if (currentClassId == ClassIdMonoBehaviour)
            {
                if (line.StartsWith("  m_Script:", StringComparison.Ordinal) &&
                    TryExtractGuidFromText(line, out AssetGuid parsedScriptGuid))
                {
                    currentMbScriptGuid = parsedScriptGuid;
                }

                if (i == targetLine)
                {
                    if (TryMatchSerializedFieldKeyAtCursor(line, targetCharacter, out string matchedField))
                    {
                        if (!currentMbScriptGuid.HasValue)
                        {
                            currentMbScriptGuid = ScanForwardForMonoBehaviourScriptGuid(lines, targetLine + 1);
                        }

                        if (!currentMbScriptGuid.HasValue && blockStartLine < targetLine)
                        {
                            // Already scanned from blockStartLine..targetLine.
                        }

                        if (currentMbScriptGuid.HasValue && !currentMbScriptGuid.Value.IsZero)
                        {
                            scriptGuid = currentMbScriptGuid.Value;
                            fieldName = matchedField;
                            return true;
                        }
                    }

                    return false;
                }
            }
            else if (currentClassId == ClassIdPrefabInstance)
            {
                int indent = CountLeadingSpaces(line);
                if (indent < line.Length)
                {
                    ReadOnlySpan<char> trimmed = line.AsSpan(indent);

                    if (inModifications &&
                        (indent < modificationsIndent || (indent == modificationsIndent && !trimmed.StartsWith("- ", StringComparison.Ordinal))))
                    {
                        inModifications = false;
                        hasPendingModTarget = false;
                    }

                    if (trimmed.StartsWith("m_Modifications:", StringComparison.Ordinal) &&
                        trimmed["m_Modifications:".Length..].Trim().IsEmpty)
                    {
                        inModifications = true;
                        modificationsIndent = indent;
                        hasPendingModTarget = false;
                    }
                    else if (inModifications)
                    {
                        if (indent == modificationsIndent && trimmed.StartsWith("- ", StringComparison.Ordinal))
                        {
                            hasPendingModTarget = false;
                            ReadOnlySpan<char> afterDash = trimmed[2..].TrimStart();
                            if (afterDash.StartsWith("target:", StringComparison.Ordinal))
                            {
                                byte[] targetBytes = Encoding.UTF8.GetBytes(afterDash["target:".Length..].ToString());
                                if (UnityYamlSpanReader.TryParseUnityReference(targetBytes, out long? tFid, out AssetGuid? tGuid) &&
                                    tFid.HasValue &&
                                    tGuid.HasValue)
                                {
                                    hasPendingModTarget = true;
                                    pendingModTargetFid = tFid.Value;
                                    pendingModTargetGuid = tGuid.Value;
                                }
                            }
                        }
                        else if (hasPendingModTarget && trimmed.StartsWith("propertyPath:", StringComparison.Ordinal))
                        {
                            if (i == targetLine)
                            {
                                if (TryMatchPropertyPathAtCursor(line, targetCharacter, out string rootField))
                                {
                                    AssetGuid? owningScript = _store.GetComponentScriptGuid(pendingModTargetGuid, pendingModTargetFid);
                                    if (owningScript.HasValue && !owningScript.Value.IsZero)
                                    {
                                        scriptGuid = owningScript.Value;
                                        fieldName = rootField;
                                        return true;
                                    }
                                }

                                return false;
                            }

                            hasPendingModTarget = false;
                        }
                    }
                }

                if (i == targetLine)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static AssetGuid? ScanForwardForMonoBehaviourScriptGuid(string[] lines, int startLine)
    {
        for (int i = startLine; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("--- !u!", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("  m_Script:", StringComparison.Ordinal) &&
                TryExtractGuidFromText(line, out AssetGuid guid))
            {
                return guid;
            }
        }

        return null;
    }

    private static bool TryExtractGuidFromText(string line, out AssetGuid guid)
    {
        guid = AssetGuid.Zero;
        int guidIdx = line.IndexOf("guid:", StringComparison.Ordinal);
        if (guidIdx < 0)
        {
            return false;
        }

        int hexStart = guidIdx + "guid:".Length;
        while (hexStart < line.Length && (line[hexStart] == ' ' || line[hexStart] == '\t'))
        {
            hexStart++;
        }

        if (hexStart + 32 > line.Length)
        {
            return false;
        }

        return AssetGuid.TryParseHex(line.AsSpan(hexStart, 32), out guid) && !guid.IsZero;
    }

    private static bool TryMatchSerializedFieldKeyAtCursor(string line, int cursorChar, out string fieldName)
    {
        fieldName = string.Empty;

        if (line.Length < 4 || line[0] != ' ' || line[1] != ' ' ||
            line[2] == ' ' || line[2] == '-' || line[2] == '#')
        {
            return false;
        }

        int colonIdx = line.IndexOf(':', 2);
        if (colonIdx <= 2)
        {
            return false;
        }

        if (colonIdx + 1 < line.Length && line[colonIdx + 1] is not (' ' or '\t'))
        {
            return false;
        }

        string candidate = line[2..colonIdx];
        foreach (char ch in candidate)
        {
            if (ch is ' ' or '\t' or '{' or '}')
            {
                return false;
            }
        }

        if (BuiltInMonoBehaviourKeys.Contains(candidate))
        {
            return false;
        }

        if (cursorChar < 2 || cursorChar > colonIdx)
        {
            return false;
        }

        fieldName = candidate;
        return true;
    }

    private static bool TryMatchPropertyPathAtCursor(string line, int cursorChar, out string rootFieldName)
    {
        rootFieldName = string.Empty;

        int indent = CountLeadingSpaces(line);
        if (!line.AsSpan(indent).StartsWith("propertyPath:", StringComparison.Ordinal))
        {
            return false;
        }

        int valueStart = indent + "propertyPath:".Length;
        while (valueStart < line.Length && (line[valueStart] == ' ' || line[valueStart] == '\t'))
        {
            valueStart++;
        }

        int valueEnd = line.Length;
        while (valueEnd > valueStart && (line[valueEnd - 1] == ' ' || line[valueEnd - 1] == '\t'))
        {
            valueEnd--;
        }

        if (valueEnd - valueStart >= 2)
        {
            char first = line[valueStart];
            char last = line[valueEnd - 1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                valueStart++;
                valueEnd--;
            }
        }

        if (valueStart >= valueEnd || cursorChar < valueStart || cursorChar > valueEnd)
        {
            return false;
        }

        string propertyPath = line[valueStart..valueEnd];
        int dotIdx = propertyPath.IndexOf('.');
        int bracketIdx = propertyPath.IndexOf('[');
        int splitIdx = -1;
        if (dotIdx >= 0 && bracketIdx >= 0)
        {
            splitIdx = Math.Min(dotIdx, bracketIdx);
        }
        else if (dotIdx >= 0)
        {
            splitIdx = dotIdx;
        }
        else if (bracketIdx >= 0)
        {
            splitIdx = bracketIdx;
        }

        rootFieldName = splitIdx > 0 ? propertyPath[..splitIdx] : propertyPath;
        return !string.IsNullOrEmpty(rootFieldName);
    }

    private static bool TryFindDirectSerializedFieldSpan(
        string lineText,
        string oldName,
        out int startChar,
        out int endChar)
    {
        startChar = 0;
        endChar = 0;

        if (!lineText.StartsWith("  ", StringComparison.Ordinal))
        {
            return false;
        }

        int keyEnd = 2 + oldName.Length;
        if (lineText.Length <= keyEnd ||
            !lineText.AsSpan(2).StartsWith(oldName.AsSpan(), StringComparison.Ordinal) ||
            lineText[keyEnd] != ':')
        {
            return false;
        }

        if (keyEnd + 1 < lineText.Length && lineText[keyEnd + 1] is not (' ' or '\t'))
        {
            return false;
        }

        startChar = 2;
        endChar = keyEnd;
        return true;
    }

    private static bool TryFindPropertyPathRootSpan(
        string lineText,
        string oldName,
        out int startChar,
        out int endChar)
    {
        startChar = 0;
        endChar = 0;

        int indent = CountLeadingSpaces(lineText);
        ReadOnlySpan<char> trimmed = lineText.AsSpan(indent);
        if (!trimmed.StartsWith("propertyPath:", StringComparison.Ordinal))
        {
            return false;
        }

        int valueStart = indent + "propertyPath:".Length;
        while (valueStart < lineText.Length && (lineText[valueStart] == ' ' || lineText[valueStart] == '\t'))
        {
            valueStart++;
        }

        int valueEnd = lineText.Length;
        while (valueEnd > valueStart && (lineText[valueEnd - 1] == ' ' || lineText[valueEnd - 1] == '\t'))
        {
            valueEnd--;
        }

        if (valueEnd - valueStart >= 2)
        {
            char first = lineText[valueStart];
            char last = lineText[valueEnd - 1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                valueStart++;
                valueEnd--;
            }
        }

        if (valueStart + oldName.Length > valueEnd)
        {
            return false;
        }

        ReadOnlySpan<char> valueSpan = lineText.AsSpan(valueStart, valueEnd - valueStart);
        if (!valueSpan.StartsWith(oldName.AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }

        if (valueSpan.Length > oldName.Length && valueSpan[oldName.Length] is not ('.' or '['))
        {
            return false;
        }

        startChar = valueStart;
        endChar = valueStart + oldName.Length;
        return true;
    }

    private static bool TryFindUnityEventMethodSpan(
        string lineText,
        string oldName,
        out int startChar,
        out int endChar)
    {
        startChar = 0;
        endChar = 0;

        int indent = CountLeadingSpaces(lineText);
        ReadOnlySpan<char> trimmed = lineText.AsSpan(indent);
        if (!trimmed.StartsWith("m_MethodName:", StringComparison.Ordinal))
        {
            return false;
        }

        int valueStart = indent + "m_MethodName:".Length;
        while (valueStart < lineText.Length && (lineText[valueStart] == ' ' || lineText[valueStart] == '\t'))
        {
            valueStart++;
        }

        int valueEnd = lineText.Length;
        while (valueEnd > valueStart && (lineText[valueEnd - 1] == ' ' || lineText[valueEnd - 1] == '\t'))
        {
            valueEnd--;
        }

        if (valueEnd - valueStart >= 2)
        {
            char first = lineText[valueStart];
            char last = lineText[valueEnd - 1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                valueStart++;
                valueEnd--;
            }
        }

        if (valueEnd - valueStart != oldName.Length ||
            !lineText.AsSpan(valueStart, oldName.Length).Equals(oldName.AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }

        startChar = valueStart;
        endChar = valueEnd;
        return true;
    }

    private static AssetDiagnosticDto CreateMissingScriptDiagnostic(
        AssetGuid scriptGuid,
        string? gameObjectName,
        int lineNumber,
        string[]? lines)
    {
        string guidHex = scriptGuid.ToHexString();
        int startChar = 0;
        int endChar = 0;

        if (lines is not null && (uint)lineNumber < (uint)lines.Length)
        {
            string lineText = lines[lineNumber];
            int guidIdx = lineText.IndexOf(guidHex, StringComparison.OrdinalIgnoreCase);
            if (guidIdx >= 0)
            {
                startChar = guidIdx;
                endChar = guidIdx + guidHex.Length;
            }
            else
            {
                endChar = lineText.Length;
            }
        }

        string message = !string.IsNullOrEmpty(gameObjectName)
            ? $"Missing script reference on '{gameObjectName}' (GUID: {guidHex})."
            : $"Missing script reference (GUID: {guidHex}).";

        return new AssetDiagnosticDto
        {
            Code = "UNITY0001",
            Message = message,
            Line = lineNumber,
            StartCharacter = startChar,
            EndCharacter = endChar,
        };
    }

    private static bool TryFindCSharpFieldLocation(
        string scriptFullPath,
        string fieldName,
        out int line,
        out int character)
    {
        line = 0;
        character = 0;

        string[]? lines = TryReadAllLines(scriptFullPath);
        if (lines is null || lines.Length == 0 || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        Regex pattern = new($@"\b{Regex.Escape(fieldName)}\b", RegexOptions.Compiled);
        bool prevLineHadSerializeAttr = false;
        int bestScore = int.MinValue;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string codeOnly = StripLineComment(raw);
            string trimmed = codeOnly.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            bool hasSerializeAttr = trimmed.Contains("SerializeField", StringComparison.Ordinal) ||
                                    trimmed.Contains("SerializeReference", StringComparison.Ordinal);

            MatchCollection matches = pattern.Matches(codeOnly);
            foreach (Match match in matches)
            {
                int idx = match.Index;
                int afterIdx = idx + match.Length;

                // Must look like a declaration: followed by ';', '=', ',', or '{' (ignoring whitespace).
                int lookahead = afterIdx;
                while (lookahead < codeOnly.Length && char.IsWhiteSpace(codeOnly[lookahead]))
                {
                    lookahead++;
                }

                if (lookahead >= codeOnly.Length || codeOnly[lookahead] is not (';' or '=' or ',' or '{'))
                {
                    continue;
                }

                // Must be preceded by a type name (not '.', 'return', 'var', etc.).
                int lookbehind = idx - 1;
                while (lookbehind >= 0 && char.IsWhiteSpace(codeOnly[lookbehind]))
                {
                    lookbehind--;
                }

                if (lookbehind < 0 || codeOnly[lookbehind] == '.')
                {
                    continue;
                }

                ReadOnlySpan<char> prefix = codeOnly.AsSpan(0, idx).Trim();
                if (prefix.EndsWith("return", StringComparison.Ordinal) ||
                    prefix.EndsWith("var", StringComparison.Ordinal))
                {
                    continue;
                }

                int score = 1;
                if (hasSerializeAttr || prevLineHadSerializeAttr)
                {
                    score += 10;
                }

                foreach (string keyword in CSharpVisibilityOrFieldAttributes)
                {
                    if (trimmed.Contains(keyword, StringComparison.Ordinal))
                    {
                        score += 5;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    line = i;
                    character = idx;
                }
            }

            prevLineHadSerializeAttr = hasSerializeAttr && !trimmed.Contains(';') && !trimmed.Contains('=');
        }

        return bestScore > int.MinValue;
    }

    private static string StripLineComment(string line)
    {
        bool inString = false;
        bool inChar = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (inChar)
            {
                if (c == '\\') i++;
                else if (c == '\'') inChar = false;
            }
            else
            {
                if (c == '"') inString = true;
                else if (c == '\'') inChar = true;
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    return line[..i];
                }
            }
        }

        return line;
    }

    private static bool AddEdit(
        List<AssetTextEditDto> edits,
        HashSet<(int Line, int Start, int End)> seen,
        int line,
        int startChar,
        int endChar,
        string newText)
    {
        if (seen.Add((line, startChar, endChar)))
        {
            edits.Add(new AssetTextEditDto
            {
                Line = line,
                StartCharacter = startChar,
                EndCharacter = endChar,
                NewText = newText,
            });
        }

        return true;
    }

    private static string[]? TryReadAllLines(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (reader.ReadLine() is string line)
            {
                lines.Add(line);
            }
            return lines.ToArray();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int CountLeadingSpaces(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }
        return i;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
