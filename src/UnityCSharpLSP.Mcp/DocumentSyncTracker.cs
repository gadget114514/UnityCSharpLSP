using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;

namespace UnityCSharpLSP.Mcp;

/// <summary>
/// Tracks open LSP documents and synchronizes disk edits on demand before LSP requests are dispatched.
/// Normalizes file paths and relative paths to canonical <c>file:///</c> URIs.
/// </summary>
public sealed class DocumentSyncTracker : IDisposable
{
    private sealed record TrackedDocument(string Uri, int Version, DateTime LastWriteTimeUtc, long Length);

    private readonly LspLoopbackClient _client;
    private readonly string _workspaceRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TrackedDocument> _openDocuments;

    public DocumentSyncTracker(LspLoopbackClient client, string workspaceRoot)
    {
        _client = client;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _openDocuments = new Dictionary<string, TrackedDocument>(comparer);
    }

    /// <summary>
    /// Normalizes a <c>file:///</c> URI, absolute path, or workspace-relative path into its
    /// full local path and canonical <c>file:///</c> URI.
    /// </summary>
    public (string FullPath, string CanonicalUri) NormalizePathAndUri(string uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath))
        {
            throw new ArgumentException("URI or path must not be empty.", nameof(uriOrPath));
        }

        string localPath;
        if (Uri.TryCreate(uriOrPath, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            localPath = parsed.LocalPath;
        }
        else if (Path.IsPathFullyQualified(uriOrPath))
        {
            localPath = uriOrPath;
        }
        else
        {
            localPath = Path.Combine(_workspaceRoot, uriOrPath);
        }

        var fullPath = Path.GetFullPath(localPath);
        var canonicalUri = new Uri(fullPath).AbsoluteUri;
        return (fullPath, canonicalUri);
    }

    /// <summary>
    /// Normalizes URI fields in <paramref name="params"/> in place and ensures any referenced
    /// document on disk is opened or updated via <c>textDocument/didOpen</c> / <c>textDocument/didChange</c>.
    /// </summary>
    public async Task EnsureSyncedForParamsAsync(JObject @params, CancellationToken ct = default)
    {
        var filesToSync = new List<(string FullPath, string CanonicalUri)>();

        if (@params["textDocument"] is JObject textDoc &&
            textDoc["uri"]?.Value<string>() is { Length: > 0 } rawDocUri)
        {
            var normalized = NormalizePathAndUri(rawDocUri);
            textDoc["uri"] = normalized.CanonicalUri;
            filesToSync.Add(normalized);
        }

        if (@params["item"] is JObject item &&
            item["uri"]?.Value<string>() is { Length: > 0 } rawItemUri)
        {
            var normalized = NormalizePathAndUri(rawItemUri);
            item["uri"] = normalized.CanonicalUri;
            filesToSync.Add(normalized);
        }

        if (@params["files"] is JArray fileEntries)
        {
            foreach (var entry in fileEntries.OfType<JObject>())
            {
                NormalizeUriProperty(entry, "uri");
                NormalizeUriProperty(entry, "oldUri");
                NormalizeUriProperty(entry, "newUri");
            }
        }

        if (@params["previousResultIds"] is JArray prevIds)
        {
            foreach (var entry in prevIds.OfType<JObject>())
            {
                NormalizeUriProperty(entry, "uri");
            }
        }

        foreach (var (fullPath, canonicalUri) in filesToSync)
        {
            await EnsureDocumentSyncedAsync(fullPath, canonicalUri, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures a single document is opened on the LSP server or updated if its disk mtime or size changed.
    /// </summary>
    public async Task EnsureDocumentSyncedAsync(string fullPath, string canonicalUri, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(fullPath))
            {
                return;
            }

            var info = new FileInfo(fullPath);
            var mtime = info.LastWriteTimeUtc;
            var length = info.Length;

            if (!_openDocuments.TryGetValue(fullPath, out var tracked))
            {
                var text = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
                var openParams = new JObject
                {
                    ["textDocument"] = new JObject
                    {
                        ["uri"] = canonicalUri,
                        ["languageId"] = GetLanguageId(fullPath),
                        ["version"] = 1,
                        ["text"] = text,
                    },
                };

                await _client.NotifyAsync(Methods.TextDocumentDidOpenName, openParams).ConfigureAwait(false);
                _openDocuments[fullPath] = new TrackedDocument(canonicalUri, 1, mtime, length);
                return;
            }

            if (tracked.LastWriteTimeUtc != mtime || tracked.Length != length)
            {
                var text = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
                var nextVersion = tracked.Version + 1;
                var changeParams = new JObject
                {
                    ["textDocument"] = new JObject
                    {
                        ["uri"] = tracked.Uri,
                        ["version"] = nextVersion,
                    },
                    ["contentChanges"] = new JArray
                    {
                        new JObject { ["text"] = text },
                    },
                };

                await _client.NotifyAsync(Methods.TextDocumentDidChangeName, changeParams).ConfigureAwait(false);
                _openDocuments[fullPath] = new TrackedDocument(tracked.Uri, nextVersion, mtime, length);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends <c>textDocument/didClose</c> for all currently tracked documents and clears the tracker state.
    /// </summary>
    public async Task<int> CloseAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var count = _openDocuments.Count;
            foreach (var doc in _openDocuments.Values)
            {
                await _client.NotifyAsync(
                    Methods.TextDocumentDidCloseName,
                    new JObject
                    {
                        ["textDocument"] = new JObject { ["uri"] = doc.Uri },
                    }).ConfigureAwait(false);
            }

            _openDocuments.Clear();
            return count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public int OpenDocumentCount
    {
        get
        {
            _gate.Wait();
            try
            {
                return _openDocuments.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private void NormalizeUriProperty(JObject obj, string propertyName)
    {
        if (obj[propertyName]?.Value<string>() is { Length: > 0 } raw)
        {
            obj[propertyName] = NormalizePathAndUri(raw).CanonicalUri;
        }
    }

    private static string GetLanguageId(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".prefab" or ".unity" or ".asset" or ".mat" => "yaml",
            _ => "plaintext",
        };
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
