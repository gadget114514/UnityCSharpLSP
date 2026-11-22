using System.Collections.Concurrent;
using System.Text;

namespace UnityCSharpLSP.AssetServer.Parsing;

/// <summary>One textual occurrence of a GUID inside a text asset.</summary>
public sealed record GuidReferenceHit(
    string FullPath,
    int Line,
    int StartCharacter,
    int EndCharacter,
    string LineText,
    int? ClassId,
    long? ObjectFileId,
    long? FileId);

/// <summary>Location of the <c>guid:</c> value inside a <c>.meta</c> file.</summary>
public readonly record struct MetaGuidLocation(string Guid, string MetaPath, string AssetPath, int Line, int Character);

/// <summary>
/// Backs the <c>unitylsp/guidDefinition</c> / <c>unitylsp/guidReferences</c> extensions
/// (same behaviour as UnityCSharpFastLSP): reads the GUID position from <c>.meta</c> files and
/// byte-scans text assets (YAML / JSON, including <c>.meta</c> and <c>.asmdef</c>/<c>.asmref</c>)
/// for occurrences of a GUID on each request. Binary-serialized assets are skipped.
/// </summary>
public static class UnityGuidReferenceScanner
{
    public const int DefaultMaxResults = 500;
    private const int MaxLineTextLength = 300;
    private const int MetaHeadLines = 16;

    /// <summary>Extensions of text assets that may contain GUID references.</summary>
    public static readonly IReadOnlySet<string> ReferenceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".unity", ".prefab", ".asset", ".mat", ".anim", ".controller", ".overrideController",
        ".playable", ".mask", ".physicMaterial", ".physicsMaterial2D", ".spriteatlas", ".spriteatlasv2",
        ".lighting", ".terrainlayer", ".mixer", ".renderTexture", ".preset", ".signal", ".guiskin",
        ".fontsettings", ".shadervariants", ".cubemap", ".flare", ".brush", ".meta",
        ".asmdef", ".asmref",
    };

    private static readonly byte[] ObjectHeaderMarker = "--- !u!"u8.ToArray();
    private static readonly byte[] FileIdMarker = "fileID:"u8.ToArray();

    /// <summary>True when <paramref name="value"/> is a 32-character hexadecimal string.</summary>
    public static bool IsGuid(string? value)
    {
        if (value is null || value.Length != 32)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads the <c>guid:</c> line of a <c>.meta</c> file and returns its position.</summary>
    public static MetaGuidLocation? TryReadMetaGuid(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return null;
            }

            using var reader = new StreamReader(metaPath, Encoding.UTF8);
            for (int lineNo = 0; lineNo < MetaHeadLines; lineNo++)
            {
                string? line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                if (!line.StartsWith("guid:", StringComparison.Ordinal))
                {
                    continue;
                }

                string value = line["guid:".Length..].Trim();
                if (!IsGuid(value))
                {
                    return null;
                }

                string fullMeta = Path.GetFullPath(metaPath);
                int character = line.IndexOf(value, "guid:".Length, StringComparison.Ordinal);
                return new MetaGuidLocation(
                    value.ToLowerInvariant(),
                    fullMeta,
                    fullMeta[..^".meta".Length],
                    lineNo,
                    character);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Scans <c>Assets/</c>, <c>Packages/</c> and <c>Library/PackageCache/</c> under
    /// <paramref name="rootPath"/> (or the whole root when none exist) for <paramref name="guid"/>.
    /// The asset's own <c>.meta</c> (<paramref name="selfMetaPath"/>) is excluded.
    /// </summary>
    public static (List<GuidReferenceHit> References, bool Truncated) FindReferences(
        string rootPath,
        string guid,
        string? selfMetaPath,
        int? maxResults,
        CancellationToken ct = default)
    {
        int limit = Math.Clamp(maxResults is > 0 ? maxResults.Value : DefaultMaxResults, 1, 100_000);
        string? selfMeta = selfMetaPath is null ? null : Path.GetFullPath(selfMetaPath);
        var found = new ConcurrentBag<GuidReferenceHit>();
        int total = 0;
        bool truncated = false;

        if (Directory.Exists(rootPath))
        {
            byte[] needle = Encoding.ASCII.GetBytes(guid.ToLowerInvariant());
            var options = new ParallelOptions { CancellationToken = ct };
            Parallel.ForEach(EnumerateFiles(rootPath), options, (path, state) =>
            {
                if (!ReferenceExtensions.Contains(Path.GetExtension(path)))
                {
                    return;
                }

                if (selfMeta is not null &&
                    string.Equals(Path.GetFullPath(path), selfMeta, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                foreach (GuidReferenceHit hit in ScanFile(path, needle))
                {
                    if (Interlocked.Increment(ref total) > limit)
                    {
                        truncated = true;
                        state.Stop();
                        return;
                    }

                    found.Add(hit);
                }
            });
        }

        var references = found
            .OrderBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.StartCharacter)
            .ToList();
        return (references, truncated);
    }

    /// <summary>Scans one file for occurrences of the lowercase ASCII GUID <paramref name="needle"/>.</summary>
    public static List<GuidReferenceHit> ScanFile(string path, byte[] needle)
    {
        var results = new List<GuidReferenceHit>();
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return results;
        }
        catch (UnauthorizedAccessException)
        {
            return results;
        }

        ReadOnlySpan<byte> data = bytes;
        if (data.IndexOf(needle) < 0)
        {
            return results;
        }

        // Binary-serialized assets (containing NUL) store GUIDs as raw bytes, not hex text.
        if (data[..Math.Min(data.Length, 512)].IndexOf((byte)0) >= 0)
        {
            return results;
        }

        string fullPath = Path.GetFullPath(path);
        int searchFrom = 0;
        int countedUpTo = 0;
        int lineNo = 0;

        while (searchFrom < data.Length)
        {
            int rel = data[searchFrom..].IndexOf(needle);
            if (rel < 0)
            {
                break;
            }

            int hit = searchFrom + rel;
            searchFrom = hit + needle.Length;

            lineNo += data[countedUpTo..hit].Count((byte)'\n');
            countedUpTo = hit;

            int lineStart = data[..hit].LastIndexOf((byte)'\n') + 1;
            int lineEndRel = data[hit..].IndexOf((byte)'\n');
            int lineEnd = lineEndRel < 0 ? data.Length : hit + lineEndRel;
            if (lineEnd > lineStart && data[lineEnd - 1] == (byte)'\r')
            {
                lineEnd--;
            }

            int character = Encoding.UTF8.GetCharCount(data[lineStart..hit]);
            string lineText = Encoding.UTF8.GetString(data[lineStart..lineEnd]).Trim();
            if (lineText.Length > MaxLineTextLength)
            {
                lineText = lineText[..MaxLineTextLength] + "…";
            }

            (int? classId, long? objectFileId) = FindObjectHeader(data[..lineStart]);
            long? fileId = FindFileId(data[lineStart..hit]);

            results.Add(new GuidReferenceHit(
                fullPath,
                lineNo,
                character,
                character + needle.Length,
                lineText,
                classId,
                objectFileId,
                fileId));
        }

        return results;
    }

    /// <summary>Reads the preceding YAML document header <c>--- !u!&lt;classId&gt; &amp;&lt;fileID&gt;</c>.</summary>
    private static (int? ClassId, long? FileId) FindObjectHeader(ReadOnlySpan<byte> before)
    {
        int idx = before.LastIndexOf(ObjectHeaderMarker);
        if (idx < 0)
        {
            return (null, null);
        }

        ReadOnlySpan<byte> rest = before[(idx + ObjectHeaderMarker.Length)..];
        int newline = rest.IndexOf((byte)'\n');
        if (newline >= 0)
        {
            rest = rest[..newline];
        }

        int? classId = ParseInt(rest, out int consumed) is long c ? (int)c : null;
        long? fileId = null;
        int amp = rest[consumed..].IndexOf((byte)'&');
        if (amp >= 0)
        {
            fileId = ParseInt(rest[(consumed + amp + 1)..], out _);
        }

        return (classId, fileId);
    }

    /// <summary>Reads <c>fileID: &lt;n&gt;</c> that precedes the GUID on the same line.</summary>
    private static long? FindFileId(ReadOnlySpan<byte> linePrefix)
    {
        int idx = linePrefix.LastIndexOf(FileIdMarker);
        if (idx < 0)
        {
            return null;
        }

        ReadOnlySpan<byte> rest = linePrefix[(idx + FileIdMarker.Length)..].TrimStart((byte)' ');
        return ParseInt(rest, out _);
    }

    private static long? ParseInt(ReadOnlySpan<byte> span, out int consumed)
    {
        consumed = 0;
        bool negative = false;
        if (span.Length > 0 && span[0] == (byte)'-')
        {
            negative = true;
            consumed = 1;
        }

        long value = 0;
        int digits = 0;
        while (consumed < span.Length && span[consumed] is >= (byte)'0' and <= (byte)'9' && digits < 18)
        {
            value = value * 10 + (span[consumed] - '0');
            consumed++;
            digits++;
        }

        return digits == 0 ? null : (negative ? -value : value);
    }

    private static IEnumerable<string> EnumerateFiles(string rootPath)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        string[] dirs =
        [
            Path.Combine(rootPath, "Assets"),
            Path.Combine(rootPath, "Packages"),
            Path.Combine(rootPath, "Library", "PackageCache"),
        ];

        bool any = false;
        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            any = true;
            foreach (string file in Directory.EnumerateFiles(dir, "*", options))
            {
                yield return file;
            }
        }

        if (!any)
        {
            foreach (string file in Directory.EnumerateFiles(rootPath, "*", options))
            {
                yield return file;
            }
        }
    }
}
